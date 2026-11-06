using System.Text;

namespace WtfNel.Core.Injection;

/// <summary>Target discovery by window title or process name.</summary>
internal sealed record ProcessCandidate(int ProcessId, string Title, string Executable, long CreationTime);

internal static class ProcessTarget
{
    public static IReadOnlyList<ProcessCandidate> FindByWindow(IEnumerable<string> keywords, bool requireJava)
    {
        var wanted = keywords.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
        if (wanted.Length == 0) return [];

        var found = new List<ProcessCandidate>();
        NativeApi.EnumWindows((window, _) =>
        {
            if (!NativeApi.IsWindowVisible(window)) return true;

            int length = NativeApi.GetWindowTextLengthW(window);
            if (length <= 0) return true;

            var text = new StringBuilder(length + 1);
            if (NativeApi.GetWindowTextW(window, text, text.Capacity) <= 0) return true;

            string title = text.ToString();
            if (!wanted.Any(k => title.Contains(k, StringComparison.OrdinalIgnoreCase))) return true;

            NativeApi.GetWindowThreadProcessId(window, out int pid);
            if (pid == 0) return true;

            string exe = ProcessImageName(pid);
            if (requireJava && !IsJava(exe)) return true;

            found.Add(new ProcessCandidate(pid, title, exe, GetCreationTime(pid)));
            return true;
        }, IntPtr.Zero);

        return found;
    }

    public static IReadOnlyList<ProcessCandidate> FindByProcessName(IEnumerable<string> names)
    {
        var wanted = names.Where(n => !string.IsNullOrWhiteSpace(n))
                          .Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n : n + ".exe")
                          .ToArray();
        if (wanted.Length == 0) return [];

        var found = new List<ProcessCandidate>();
        IntPtr snapshot = NativeApi.CreateToolhelp32Snapshot(NativeApi.Th32csSnapProcess, 0);
        if (snapshot == new IntPtr(-1)) return found;

        try
        {
            var entry = new NativeApi.ProcessEntry32W
            {
                dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeApi.ProcessEntry32W>(),
            };
            if (!NativeApi.Process32FirstW(snapshot, ref entry)) return found;

            do
            {
                if (!wanted.Any(n => string.Equals(n, entry.szExeFile, StringComparison.OrdinalIgnoreCase))) continue;
                int pid = (int)entry.th32ProcessID;
                if (pid == Environment.ProcessId) continue;
                found.Add(new ProcessCandidate(pid, entry.szExeFile, ProcessImageName(pid), GetCreationTime(pid)));
            } while (NativeApi.Process32NextW(snapshot, ref entry));
        }
        finally
        {
            NativeApi.CloseHandle(snapshot);
        }
        return found;
    }

    public static ProcessCandidate? Newest(IEnumerable<ProcessCandidate> candidates)
        => candidates.OrderByDescending(c => c.CreationTime).FirstOrDefault();

    public static bool IsJava(string executable)
    {
        string name = Path.GetFileName(executable);
        return name.Equals("java.exe", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("javaw.exe", StringComparison.OrdinalIgnoreCase);
    }

    public static long GetCreationTime(int pid)
    {
        using var process = TryOpen(pid);
        if (process is null) return 0;
        return process.StartTime.ToUniversalTime().Ticks;
    }

    public static string ProcessImageName(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.MainModule?.FileName ?? process.ProcessName + ".exe";
        }
        catch
        {
            return $"pid {pid}";
        }
    }

    public static int Arch(int pid)
    {
        using var remote = RemoteProcess.Open(pid, out _);
        return remote?.TargetArch() ?? 0;
    }

    private static System.Diagnostics.Process? TryOpen(int pid)
    {
        try { return System.Diagnostics.Process.GetProcessById(pid); }
        catch { return null; }
    }
}
