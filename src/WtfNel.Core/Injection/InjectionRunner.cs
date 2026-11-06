using WtfNel.Core.Logging;

namespace WtfNel.Core.Injection;

/// <summary>Turns shadow.json injection settings into one injection run.</summary>
public static class InjectionRunner
{
    public static GameTarget ParseTarget(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "scpsl" or "scp:sl" or "scp" => GameTarget.ScpSl,
        _ => GameTarget.Minecraft,
    };

    public static InjectionStrategy ParseStrategy(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "reflective" or "反射" => InjectionStrategy.Reflective,
        "manualmap" or "manual" or "手动映射" => InjectionStrategy.ManualMap,
        "loadlibrary" or "load" or "落盘" => InjectionStrategy.LoadLibrary,
        _ => InjectionStrategy.Auto,
    };

    public static string? InitExportFor(string payloadPath)
    {
        string name = Path.GetFileNameWithoutExtension(payloadPath).ToLowerInvariant();
        if (name.Contains("hwidspoof")) return "WtfNelHwidInit";
        if (name.Contains("payload.proxy") || name.EndsWith(".proxy")) return "WtfNelProxyInit";
        return null;
    }

    public static IReadOnlyList<string> DefaultPayloads(ShadowConfig cfg)
    {
        var payloads = new List<string>(2);
        if (cfg.EnableProxy) payloads.Add(InjectionService.ProxyPayload);
        if (cfg.EnableHwidSpoof) payloads.Add(InjectionService.HwidPayload);
        return payloads;
    }

    public static int ProxyPort { get; private set; }

    public static IReadOnlyList<InjectionResult> InjectAll(ShadowConfig cfg, int? processId = null)
    {
        GameTarget target = ParseTarget(cfg.InjectTarget);
        InjectionStrategy strategy = ParseStrategy(cfg.InjectStrategy);
        List<string> payloads = cfg.InjectPayloads.Count > 0
            ? cfg.InjectPayloads
            : [.. DefaultPayloads(cfg)];

        int pid;
        if (processId is { } explicitPid)
        {
            pid = explicitPid;
        }
        else
        {
            InjectionProfile profile = InjectionProfile.For(target);
            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(5, cfg.InjectWaitSeconds));
            ProcessCandidate? candidate = null;
            while (DateTime.UtcNow < deadline)
            {
                candidate = ProcessTarget.Newest(profile.Find());
                if (candidate is not null) break;
                Thread.Sleep(250);
            }

            if (candidate is null)
            {
                var timeout = InjectionResult.Fail(strategy,
                    $"等待 {cfg.InjectWaitSeconds} 秒没找到 {target} 进程", TimeSpan.Zero);
                Log.Warn("[inject] " + timeout.Detail);
                return [timeout];
            }

            pid = candidate.ProcessId;
            Log.Info($"[inject] 目标 {target}：pid {pid}「{candidate.Title}」{Path.GetFileName(candidate.Executable)}");
        }

        var results = new List<InjectionResult>(payloads.Count);
        foreach (string name in payloads)
        {
            string? path = InjectionService.ResolvePayload(name);
            if (path is null)
            {
                var missing = InjectionResult.Fail(strategy, $"payload 不存在：{name}（放到 {InjectionService.PayloadDirectory}）", TimeSpan.Zero);
                Log.Warn("[inject] " + missing.Detail);
                results.Add(missing);
                continue;
            }

            var options = new InjectionOptions
            {
                Strategy = strategy,
                InitExport = string.IsNullOrWhiteSpace(cfg.InjectInitExport) ? InitExportFor(path) : cfg.InjectInitExport,
                FallbackToLoadLibrary = true,
                Log = Log.Info,
            };

            InjectionResult result = InjectionService.Inject(pid, path, options);
            results.Add(result);
            if (result.Success) Log.Info("[inject] " + result);
            else Log.Warn("[inject] " + result);

            if (result.Success && Path.GetFileName(path).Contains("Payload.Proxy", StringComparison.OrdinalIgnoreCase))
                AnnounceProxyPort();
        }

        return results;
    }

    private static string PortFile => Path.Combine(Path.GetTempPath(), "WtfNel", "proxy-port.txt");

    private static void TryDeletePortFile()
    {
        try { if (File.Exists(PortFile)) File.Delete(PortFile); }
        catch { }
    }

    private static void AnnounceProxyPort()
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(PortFile) && int.TryParse(File.ReadAllText(PortFile).Trim(), out int port))
                {
                    ProxyPort = port;
                    Log.Info($"[inject] 代理端口已开启：127.0.0.1:{port}");
                    return;
                }
            }
            catch { }
            Thread.Sleep(250);
        }
        Log.Warn("[inject] 没等到代理端口文件：%TEMP%\\WtfNel\\proxy-port.txt");
    }

    public static Thread StartBackground(ShadowConfig cfg)
    {
        var thread = new Thread(() =>
        {
            try
            {
                InjectAll(cfg);
            }
            catch (Exception ex)
            {
                Log.Warn("[inject] 注入线程异常：" + ex.GetBaseException().Message);
                Log.Debug(ex.ToString());
            }
        })
        {
            IsBackground = true,
            Name = "wtfnel-inject",
        };
        thread.Start();
        return thread;
    }
}
