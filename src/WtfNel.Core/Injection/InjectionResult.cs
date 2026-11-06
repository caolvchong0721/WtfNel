namespace WtfNel.Core.Injection;

/// <summary>Injection strategies, options and results.</summary>
public enum InjectionStrategy
{
    Auto,
    Reflective,
    ManualMap,
    LoadLibrary,
}

public sealed class InjectionOptions
{
    public InjectionStrategy Strategy { get; init; } = InjectionStrategy.Auto;

    public int WaitMs { get; init; } = 15000;

    public IntPtr Parameter { get; init; }

    public bool FallbackToLoadLibrary { get; init; } = true;

    public string? InitExport { get; init; }

    public Action<string>? Log { get; init; }
}

public sealed record InjectionResult(
    bool Success,
    InjectionStrategy Strategy,
    string Detail,
    TimeSpan Elapsed,
    ulong RemoteModuleBase = 0,
    Exception? Error = null)
{
    internal static InjectionResult Fail(InjectionStrategy strategy, string detail, TimeSpan elapsed, ulong module = 0, Exception? error = null)
        => new(false, strategy, detail, elapsed, module, error);

    internal static InjectionResult Ok(InjectionStrategy strategy, string detail, TimeSpan elapsed, ulong module = 0)
        => new(true, strategy, detail, elapsed, module);

    public override string ToString()
        => $"{(Success ? "成功" : "失败")} [{Strategy}] {Detail} ({Elapsed.TotalMilliseconds:F0} ms)";
}
