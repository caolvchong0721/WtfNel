using System.Diagnostics;
using WtfNel.Core.Auth;
using WtfNel.Core.Auth.Yggdrasil;
using WtfNel.Core.Bridge;
using WtfNel.Core;
using WtfNel.Core.Install;
using WtfNel.Core.Injection;
using WtfNel.Core.Logging;
using WtfNel.Core.Proxy;
using WtfNel.Tui;
using WtfNel.Core.Terminal;
using Spectre.Console;

namespace WtfNel;

public static class Program
{
    /// <summary>Double-clicking the exe enters the TUI where everything happens; the window holds on exit so logs can be read.</summary>
    public static async Task<int> Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;
        Console.Title = "WtfNel";
        Term.Detect();
        try { return await RunAsync(); }
        finally
        {
            ShadowTui.WaitForKey();
        }
    }

    private static async Task<int> RunAsync()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "shadow.json");
        var cfg = ShadowConfig.Load(configPath);
        if (!File.Exists(configPath)) cfg.Save(configPath);

        Log.Open(Path.Combine(AppContext.BaseDirectory, "logs", $"shadow-{DateTime.Now:yyyyMMdd-HHmmss}.log"));
        ShadowTui.Banner();

        try
        {
            var gameId = cfg.GameIdOrDefault;
            if (!await ShadowTui.RunAsync(cfg, gameId)) return 0;
            AnsiConsole.Write(new Rule("[mediumpurple]启动[/]").LeftJustified());
            Log.Info($"工作目录: {cfg.GameDir}");
            return TemplateRun(cfg);
        }
        catch (Exception ex)
        {
            Log.Error("出错了: " + ex.GetBaseException().Message);
            Log.Debug(ex.ToString());
            return 95;
        }
        finally
        {
            Log.Close();
        }
    }

    /// <summary>Builds the launch command from the embedded template (account, character, local paths and ports) and starts the client under the parent process.</summary>
    private static int TemplateRun(ShadowConfig cfg)
    {
        var gameId = cfg.GameIdOrDefault;
        cfg.LauncherGameId = gameId;
        var userToken = YggdrasilSetup.ResolveUserToken(cfg, null);
        int port = cfg.BridgePort;

        var installer = new GameInstaller(cfg, YggdrasilSetup.CreateModProvider(cfg));
        if (cfg.AutoInstall) installer.InstallAllAsync(gameId, userToken).GetAwaiter().GetResult();
        var java = installer.LaunchJava;
        if (!File.Exists(java)) throw new FileNotFoundException("找不到 java（打开 AutoInstall 可自动下载）", java);

        if (cfg.AutoConnect)
        {
            ResolveServerAsync(cfg, YggdrasilSetup.CreateModProvider(cfg)).GetAwaiter().GetResult();
            Log.Info($"[tpl] 自动进服 -> {cfg.ConnectHost}:{cfg.ConnectPort}");
        }

        var raw = LaunchCommand.Build(cfg, java, gameId, userToken);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "logs", "launch-cmdline.txt"), raw);
        var exe = java;
        Log.Info($"[tpl] 角色 = {cfg.UserName}  uuid = {cfg.Uuid}  gameId = {gameId}");
        Log.Info("[tpl] java = " + java);
        Log.Info($"[tpl] 桥接端口 = {port}  认证端口 = {cfg.AuthPort}");

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

        int parent = ParentedLauncher.EnsureLauncher(cfg.LauncherPath, cfg.LauncherProcessName);
        guard.Track(parent, "WPFLauncher(父进程)");
        Log.Info("[tpl] 父进程 pid=" + parent);

        int pid = ParentedLauncher.StartRaw(exe, raw, cfg.GameDir, parent);
        guard.Track(pid, "客户端");
        Log.Info("[tpl] 客户端 pid=" + pid);

        // 内置注入器：后台等目标进程出现再注入，不阻塞启动流程
        InjectionRunner.StartBackground(cfg);

        try
        {
            try { Process.GetProcessById(pid).WaitForExit(); } catch (ArgumentException) { }
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
