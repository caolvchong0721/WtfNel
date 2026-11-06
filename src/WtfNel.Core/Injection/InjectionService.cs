using System.Diagnostics;
using WtfNel.Core.Logging;

namespace WtfNel.Core.Injection;

/// <summary>Injection entry point: pick the target, choose a strategy, inject, fall back.</summary>
public enum GameTarget
{
    Minecraft,
    ScpSl,
}

public sealed record InjectionProfile(GameTarget Target, string[] WindowKeywords, string[] ProcessNames, bool RequireJava)
{
    public static readonly InjectionProfile Minecraft = new(GameTarget.Minecraft, ["布吉岛"], [], true);
    public static readonly InjectionProfile ScpSl = new(GameTarget.ScpSl, [], ["SCPSL"], false);

    public static InjectionProfile For(GameTarget target) => target switch
    {
        GameTarget.ScpSl => ScpSl,
        _ => Minecraft,
    };

    internal IReadOnlyList<ProcessCandidate> Find() =>
        ProcessNames.Length > 0
            ? ProcessTarget.FindByProcessName(ProcessNames)
            : ProcessTarget.FindByWindow(WindowKeywords, RequireJava);
}

public static class InjectionService
{
    public const string HwidPayload = "WtfNel.Payload.HwidSpoof.dll";

    public const string ProxyPayload = "WtfNel.Payload.Proxy.dll";

    public static string PayloadDirectory => Path.Combine(AppContext.BaseDirectory, "payloads");

    public static string? ResolvePayload(string name)
    {
        if (Path.IsPathRooted(name)) return File.Exists(name) ? name : null;
        string direct = Path.Combine(PayloadDirectory, name);
        if (File.Exists(direct)) return direct;
        string beside = Path.Combine(AppContext.BaseDirectory, name);
        return File.Exists(beside) ? beside : null;
    }

    internal static bool TryLoadPayload(string path, out PeImage pe, out string error)
    {
        pe = null!;
        error = "";
        if (!File.Exists(path))
        {
            error = "payload 不存在: " + path;
            return false;
        }
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            return PeImage.TryParse(bytes, out pe, out error);
        }
        catch (Exception ex)
        {
            error = "读取 payload 失败: " + ex.Message;
            return false;
        }
    }

    internal static InjectionStrategy ChooseStrategy(PeImage pe, InjectionStrategy requested) => requested switch
    {
        InjectionStrategy.Auto => ReflectivePeLoader.HasReflectiveLoader(pe) ? InjectionStrategy.Reflective : InjectionStrategy.LoadLibrary,
        _ => requested,
    };

    public static InjectionResult Inject(int processId, string payloadPath, InjectionOptions? options = null)
    {
        options ??= new InjectionOptions();
        var clock = Stopwatch.StartNew();
        Action<string> log = options.Log ?? Log.Info;

        if (!TryLoadPayload(payloadPath, out PeImage pe, out string loadError))
            return InjectionResult.Fail(options.Strategy, loadError, clock.Elapsed);

        InjectionStrategy strategy = ChooseStrategy(pe, options.Strategy);
        log($"注入：pid {processId}，payload {Path.GetFileName(payloadPath)}（{pe.ArchName}，{pe.SizeOfImage / 1024} KB），策略 {strategy}");

        using var process = RemoteProcess.Open(processId, out string openError);
        if (process is null)
            return InjectionResult.Fail(strategy, openError, clock.Elapsed);

        InjectionResult result = strategy switch
        {
            InjectionStrategy.Reflective => ReflectivePeLoader.Inject(process, pe, options, clock),
            InjectionStrategy.ManualMap => ManualMapper.Inject(process, pe, options, clock),
            _ => LoadFromDisk(process, pe, payloadPath, options, clock),
        };

        if (!result.Success && options.FallbackToLoadLibrary)
        {
            log($"策略 {strategy} 失败（{result.Detail}），改用 LoadLibrary");
            InjectionResult fallback = LoadFromDisk(process, pe, payloadPath, options, clock);
            if (fallback.Success) return fallback;
            return result with { Detail = result.Detail + "；兜底 LoadLibrary 也失败：" + fallback.Detail };
        }

        return result;
    }

    private static InjectionResult LoadFromDisk(RemoteProcess process, PeImage pe, string payloadPath, InjectionOptions options, Stopwatch clock)
    {
        if (TryRemoteLoadLibrary(process, payloadPath, options, out ulong module, out string error))
            return FinishLoad(process, pe, module, payloadPath, options, clock);

        try
        {
            string temp = Path.Combine(Path.GetTempPath(), "WtfNel", "payloads");
            Directory.CreateDirectory(temp);
            string copy = Path.Combine(temp, Path.GetFileName(payloadPath));
            File.Copy(payloadPath, copy, overwrite: true);

            if (TryRemoteLoadLibrary(process, copy, options, out module, out string copyError))
                return FinishLoad(process, pe, module, copy, options, clock);

            return InjectionResult.Fail(InjectionStrategy.LoadLibrary,
                $"远程 LoadLibraryW 失败：{error}；临时副本也失败：{copyError}", clock.Elapsed);
        }
        catch (Exception ex)
        {
            return InjectionResult.Fail(InjectionStrategy.LoadLibrary,
                "LoadLibrary 异常：" + ex.Message + "；原路径失败原因：" + error, clock.Elapsed, 0, ex);
        }
    }

    private static bool TryRemoteLoadLibrary(RemoteProcess process, string path, InjectionOptions options,
        out ulong module, out string error)
    {
        module = 0;
        error = "";

        byte[] pathBytes = System.Text.Encoding.Unicode.GetBytes(path + "\0");
        IntPtr buffer = process.Allocate((uint)pathBytes.Length, NativeApi.PageReadWrite);
        if (buffer == IntPtr.Zero)
        {
            error = "远程分配路径缓冲失败";
            return false;
        }

        try
        {
            if (!process.Write(buffer, pathBytes, pathBytes.Length))
            {
                error = "写路径缓冲失败";
                return false;
            }

            IntPtr remoteKernel32 = process.GetModuleBase("kernel32.dll");
            IntPtr localKernel32 = NativeApi.GetModuleHandleW("kernel32.dll");
            if (remoteKernel32 == IntPtr.Zero || localKernel32 == IntPtr.Zero)
            {
                error = "定位 kernel32 失败";
                return false;
            }

            IntPtr localLoadLibrary = NativeApi.GetProcAddress(localKernel32, "LoadLibraryW");
            if (localLoadLibrary == IntPtr.Zero)
            {
                error = "找不到 LoadLibraryW";
                return false;
            }

            IntPtr remoteLoadLibrary = remoteKernel32 + (int)((ulong)localLoadLibrary - (ulong)localKernel32);
            if (!process.TryInvoke(remoteLoadLibrary, [(ulong)buffer], options.WaitMs, out module, out error))
                return false;

            if (module == 0)
            {
                error = "LoadLibraryW 返回 0（模块被拒或依赖缺失）";
                return false;
            }

            return true;
        }
        finally
        {
            process.Free(buffer);
        }
    }

    private static InjectionResult FinishLoad(RemoteProcess process, PeImage pe, ulong module, string path,
        InjectionOptions options, Stopwatch clock)
    {
        if (!string.IsNullOrWhiteSpace(options.InitExport))
        {
            if (!pe.TryGetExport(options.InitExport!, out uint initRva, out _))
                return InjectionResult.Fail(InjectionStrategy.LoadLibrary,
                    $"模块里没有导出 {options.InitExport}", clock.Elapsed, module);

            IntPtr init = (IntPtr)(module + initRva);
            if (!process.TryInvoke(init, [], options.WaitMs, out ulong initResult, out string initError))
                return InjectionResult.Fail(InjectionStrategy.LoadLibrary,
                    $"调用 {options.InitExport} 失败：{initError}", clock.Elapsed, module);

            (options.Log ?? Log.Info)($"{options.InitExport} 返回 0x{initResult:x}");
            if (initResult == 0)
                return InjectionResult.Fail(InjectionStrategy.LoadLibrary,
                    $"{options.InitExport} 返回 0（payload 内部初始化失败）", clock.Elapsed, module);
        }

        return InjectionResult.Ok(InjectionStrategy.LoadLibrary,
            $"已加载：{path}（模块 0x{module:x}）", clock.Elapsed, module);
    }
    public static InjectionResult InjectIntoGame(
        InjectionProfile profile, string payloadPath, InjectionOptions? options = null,
        int waitMs = 120_000, int pollMs = 100, CancellationToken cancellation = default)
    {
        options ??= new InjectionOptions();
        var clock = Stopwatch.StartNew();
        Action<string> log = options.Log ?? Log.Info;

        ProcessCandidate? target = null;
        while (clock.ElapsedMilliseconds < waitMs)
        {
            cancellation.ThrowIfCancellationRequested();
            target = ProcessTarget.Newest(profile.Find());
            if (target is not null) break;
            Thread.Sleep(pollMs);
        }

        if (target is null)
        {
            return InjectionResult.Fail(options.Strategy,
                $"等了 {clock.Elapsed.TotalSeconds:F0} 秒没找到目标（{profile.Target}）", clock.Elapsed);
        }

        log($"命中目标：pid {target.ProcessId}「{target.Title}」{Path.GetFileName(target.Executable)}");
        InjectionResult result = Inject(target.ProcessId, payloadPath, options);
        return result with { Elapsed = clock.Elapsed };
    }
}
