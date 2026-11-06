using System.Diagnostics;
using WtfNel.Core.Logging;

namespace WtfNel.Core;

/// <summary>
/// Tracks the child processes started for this session (the client and the stub parent process),
/// and shuts them down on exit so none are left running. Covers every exit path: a normal finish
/// (explicit <see cref="KillAll"/>), Ctrl+C, an unhandled exception, and the process-exit hook.
/// <see cref="KillAll"/> is idempotent.
/// </summary>
public sealed class ProcessGuard : IDisposable
{
    private readonly List<(int Pid, string Name)> _targets = new();
    private readonly object _lock = new();
    private int _reaped;
    private bool _hooked;

    public ProcessGuard()
    {
        AppDomain.CurrentDomain.ProcessExit += OnExit;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
        Console.CancelKeyPress += OnCancel;
        _hooked = true;
    }

    /// <summary>Registers a process to be shut down on exit.</summary>
    public void Track(int pid, string name)
    {
        if (pid <= 0) return;
        lock (_lock) _targets.Add((pid, name));
    }

    private void OnExit(object? s, EventArgs e) => KillAll("进程退出");
    private void OnUnhandled(object? s, UnhandledExceptionEventArgs e) => KillAll("未处理异常");
    private void OnCancel(object? s, ConsoleCancelEventArgs e) => KillAll("Ctrl+C");

    /// <summary>Kills every tracked process still alive, along with its process tree; only runs once.</summary>
    public void KillAll(string reason = "收尾")
    {
        if (Interlocked.Exchange(ref _reaped, 1) == 1) return;
        List<(int Pid, string Name)> list;
        lock (_lock) list = new(_targets);

        foreach (var (pid, name) in list)
        {
            try
            {
                var p = Process.GetProcessById(pid);
                if (p.HasExited) continue;
                p.Kill(entireProcessTree: true);
                p.WaitForExit(3000);
                Log.Info($"[guard] ({reason}) 已关闭 {name} pid={pid}");
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (Exception ex) { Log.Warn($"[guard] 关闭 {name} pid={pid} 失败: {ex.Message}"); }
        }
    }

    public void Dispose()
    {
        if (!_hooked) return;
        AppDomain.CurrentDomain.ProcessExit -= OnExit;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
        Console.CancelKeyPress -= OnCancel;
        _hooked = false;
    }
}
