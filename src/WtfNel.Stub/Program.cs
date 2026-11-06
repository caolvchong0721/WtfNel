using System.Diagnostics;

namespace WtfNel.Stub;

/// <summary>
/// Stub host: it only stays resident (optionally spawning one child process). Built as WPFLauncher.exe,
/// it serves as a placeholder parent process for the client without starting the real launcher.
/// </summary>
public static class Program
{
    public static int Main(string[] argv)
    {
        string? spawnExe = null, spawnArgs = null, cwd = null, pidFile = null;
        for (int i = 0; i < argv.Length; i++)
        {
            switch (argv[i])
            {
                case "--spawn": spawnExe = i + 1 < argv.Length ? argv[++i] : null; break;
                case "--args": spawnArgs = i + 1 < argv.Length ? argv[++i] : null; break;
                case "--cwd": cwd = i + 1 < argv.Length ? argv[++i] : null; break;
                case "--pid-file": pidFile = i + 1 < argv.Length ? argv[++i] : null; break;
            }
        }

        if (pidFile is not null)
        {
            try { File.WriteAllText(pidFile, Environment.ProcessId.ToString()); } catch { }
        }

        Process? child = null;
        if (spawnExe is not null && File.Exists(spawnExe))
        {
            child = Process.Start(new ProcessStartInfo
            {
                FileName = spawnExe,
                Arguments = spawnArgs ?? string.Empty,
                WorkingDirectory = cwd ?? Environment.CurrentDirectory,
                UseShellExecute = false,
            });
        }

        var exit = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; exit.Set(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => exit.Set();
        exit.Wait();

        try { if (child is { HasExited: false }) child.Kill(false); } catch { }
        return 0;
    }
}
