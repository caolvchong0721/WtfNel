using System.Diagnostics;
using WtfNel.Core.Auth.Yggdrasil;
using WtfNel.Core.Bridge;
using WtfNel.Core.Install;
using WtfNel.Core.Injection;
using WtfNel.Core.Logging;
using WtfNel.Core.Proxy;

namespace WtfNel.Core.Launch;

/// <summary>Builds the launch command from the embedded template (account, character, local paths and ports) and runs the client under the parent process until it exits.</summary>
public static class GameRunner
{
    /// <summary>Installs, starts the bridge/auth services and the client, then blocks until the client exits or <paramref name="ct"/> is cancelled.</summary>
    public static int Run(ShadowConfig cfg, CancellationToken ct = default)
    {
        var gameId = cfg.GameIdOrDefault;
        cfg.LauncherGameId = gameId;
        var userToken = YggdrasilSetup.ResolveUserToken(cfg, null);
        int port = cfg.BridgePort;

        var installer = new GameInstaller(cfg, YggdrasilSetup.CreateModProvider(cfg));
        if (cfg.AutoInstall) installer.InstallAllAsync(gameId, userToken).GetAwaiter().GetResult();
        ct.ThrowIfCancellationRequested();
        var java = installer.LaunchJava;
        if (!File.Exists(java)) throw new FileNotFoundException("找不到 java（打开 AutoInstall 可自动下载）", java);

        LauncherIdentity.Ensure(cfg, installer.MclRoot);

        if (cfg.AutoConnect)
        {
            ResolveServerAsync(cfg, YggdrasilSetup.CreateModProvider(cfg)).GetAwaiter().GetResult();
            Log.Info($"[tpl] 自动进服 -> {cfg.ConnectHost}:{cfg.ConnectPort}");
            var listIp = cfg.RemotePort == 25565 ? cfg.RemoteHost : $"{cfg.RemoteHost}:{cfg.RemotePort}";
            ServerList.Ensure(cfg.GameDir, listIp, "布吉岛服务器");
        }

        var raw = LaunchCommand.Build(cfg, java, gameId, userToken);
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "logs"));
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "logs", "launch-cmdline.txt"), raw);
        Log.Info($"[tpl] 角色 = {cfg.UserName}  uuid = {cfg.Uuid}  gameId = {gameId}");
        Log.Info("[tpl] java = " + java);
        Log.Info($"[tpl] 桥接端口 = {port}  认证端口 = {cfg.AuthPort}  模式 = {(cfg.EnableProxy ? "代理" : "白端")}");

        GameRelay? relay = null;
        if (cfg.UseRelay)
        {
            relay = new GameRelay(cfg.RemoteHost, cfg.RemotePort, cfg.LocalPort, Log.Verbose);
            relay.StartAsync().GetAwaiter().GetResult();
            Log.Info($"[tpl] 转发层 127.0.0.1:{cfg.LocalPort} -> {cfg.RemoteHost}:{cfg.RemotePort}");
        }

        var bridge = new LauncherBridge(port, cfg.AutoConnect ? (cfg.RemoteHost, cfg.RemotePort, "BedJsland") : null);
        bridge.StartAsync().GetAwaiter().GetResult();

        var modProvider = YggdrasilSetup.CreateModProvider(cfg);
        var authLib = YggdrasilSetup.CreateServer(cfg, userToken, modProvider);
        authLib.Start();

        _ = modProvider.GetAsync(gameId, cfg.Uid, userToken)
                .ContinueWith(t => { if (t.IsFaulted) Log.Warn("[tpl] 预取 modInfo 失败: " + t.Exception!.GetBaseException().Message); });

        using var guard = new ProcessGuard();
        try
        {
            int parent = ParentedLauncher.EnsureLauncher(cfg.LauncherPath, cfg.LauncherProcessName);
            guard.Track(parent, "WPFLauncher(父进程)");
            Log.Info("[tpl] 父进程 pid=" + parent);

            var childEnv = ParentedLauncher.BuildChildEnvironment(
                new[] { cfg.RuntimeDir, cfg.NativeDir, Path.GetDirectoryName(java) ?? "" }, out var prepended);
            childEnv["WTFNEL_LOG_DIR"] = Path.Combine(AppContext.BaseDirectory, "logs");
            Log.Info("[tpl] 子进程 PATH 追加: " + (prepended.Count > 0 ? string.Join(" | ", prepended) : "(无，目录不存在)"));

            int pid = ParentedLauncher.StartRaw(java, raw, cfg.GameDir, parent, env: childEnv);
            guard.Track(pid, "客户端");
            Log.Info("[tpl] 客户端 pid=" + pid);

            InjectionRunner.StartBackground(cfg);

            try
            {
                using var process = Process.GetProcessById(pid);
                while (!process.WaitForExit(500))
                {
                    if (ct.IsCancellationRequested)
                    {
                        Log.Info("[tpl] 已请求停止客户端");
                        break;
                    }
                }
            }
            catch (ArgumentException) { }
            Log.Info("[tpl] 客户端已退出");
        }
        finally
        {
            authLib.DisposeAsync().AsTask().GetAwaiter().GetResult();
            relay?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            guard.KillAll("客户端退出收尾");
        }
        return 0;
    }

    /// <summary>When RemoteHost is empty or "auto", resolves the real network server address from X19 by gameId.</summary>
    private static async Task ResolveServerAsync(ShadowConfig cfg, ModInfoProvider x19)
    {
        if (!string.IsNullOrWhiteSpace(cfg.RemoteHost) && !cfg.RemoteHost.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            Log.Info($"服务器(配置) = {cfg.RemoteHost}:{cfg.RemotePort}");
            return;
        }
        if (string.IsNullOrEmpty(cfg.LauncherGameId))
            throw new InvalidOperationException("RemoteHost=auto 需要 LauncherGameId（或在 shadow.json 直接填 RemoteHost/RemotePort）");
        (cfg.RemoteHost, cfg.RemotePort) = await x19.GetServerAddressAsync(cfg.LauncherGameId, cfg.Uid, cfg.UserToken);
        Log.Info($"服务器(X19) = {cfg.RemoteHost}:{cfg.RemotePort}");
    }
}
