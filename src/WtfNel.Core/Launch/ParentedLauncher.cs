using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WtfNel.Core;

/// <summary>
/// Starts a child process under a chosen parent process. Plain Process.Start cannot set a parent;
/// this uses CreateProcess with extended startup info so the child's parent PID and parent image,
/// as seen by the system, are the given (signed) process.
/// </summary>
public static class ParentedLauncher
{
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NEW_CONSOLE = 0x00000010;
    private const int PROC_THREAD_ATTRIBUTE_PARENT_PROCESS = 0x00020000;
    private const uint PROCESS_CREATE_PROCESS = 0x0080;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_DUP_HANDLE = 0x0040;
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private const uint HANDLE_FLAG_INHERIT = 0x1;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr h, uint mask, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute,
        IntPtr value, IntPtr size, IntPtr prev, IntPtr retSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? app, string commandLine, IntPtr procAttr,
        IntPtr threadAttr, bool inherit, uint flags, IntPtr env, string currentDir,
        ref STARTUPINFOEX startup, out PROCESS_INFORMATION info);

    /// <summary>
    /// Starts an executable with parentPid as its parent. <paramref name="env"/> is the full override
    /// environment block (null inherits the parent's). Returns the new PID; throws Win32Exception on failure.
    /// </summary>
    public static int Start(string exePath, IReadOnlyList<string> args, string workingDirectory,
        int parentPid, IDictionary<string, string>? env = null, bool newConsole = false, bool inheritStdio = true)
    {
        IntPtr parent = OpenProcess(PROCESS_CREATE_PROCESS | PROCESS_QUERY_INFORMATION | PROCESS_DUP_HANDLE, false, parentPid);
        if (parent == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "打开父进程句柄失败");

        IntPtr attrList = IntPtr.Zero;
        IntPtr parentValue = IntPtr.Zero;
        IntPtr envBlock = IntPtr.Zero;
        try
        {
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attrList = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref size))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "初始化属性列表失败");
            parentValue = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(parentValue, parent);
            if (!UpdateProcThreadAttribute(attrList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PARENT_PROCESS,
                    parentValue, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "设置父进程属性失败");

            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            si.lpAttributeList = attrList;

            if (inheritStdio)
            {
                si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
                si.StartupInfo.hStdInput = MakeInheritable(GetStdHandle(-10));
                si.StartupInfo.hStdOutput = MakeInheritable(GetStdHandle(-11));
                si.StartupInfo.hStdError = MakeInheritable(GetStdHandle(-12));
            }

            var cmd = BuildCommandLine(exePath, args);
            uint flags = EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT;
            if (newConsole) flags |= CREATE_NEW_CONSOLE;

            if (env is not null) envBlock = BuildEnvironmentBlock(env);

            var cmdBuf = new StringBuilder(cmd, cmd.Length + 1);
            if (!CreateProcessW(exePath, cmdBuf.ToString(), IntPtr.Zero, IntPtr.Zero, inheritStdio, flags,
                    envBlock, workingDirectory, ref si, out var info))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "创建子进程失败");

            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);
            return info.dwProcessId;
        }
        finally
        {
            if (attrList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
            }
            if (envBlock != IntPtr.Zero) Marshal.FreeHGlobal(envBlock);
            if (parentValue != IntPtr.Zero) Marshal.FreeHGlobal(parentValue);
            CloseHandle(parent);
        }
    }

    /// <summary>Replays a full command line verbatim, avoiding any corruption from tokenizing and rebuilding it.</summary>
    public static int StartRaw(string exePath, string commandLine, string workingDirectory, int parentPid,
        bool newConsole = false, bool inheritStdio = false)
    {
        IntPtr parent = OpenProcess(PROCESS_CREATE_PROCESS | PROCESS_QUERY_INFORMATION | PROCESS_DUP_HANDLE, false, parentPid);
        if (parent == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "打开父进程句柄失败");

        IntPtr attrList = IntPtr.Zero;
        IntPtr parentValue = IntPtr.Zero;
        try
        {
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attrList = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref size))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "初始化属性列表失败");

            parentValue = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(parentValue, parent);
            if (!UpdateProcThreadAttribute(attrList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PARENT_PROCESS,
                    parentValue, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "设置父进程属性失败");

            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            si.lpAttributeList = attrList;
            if (inheritStdio)
            {
                si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
                si.StartupInfo.hStdInput = MakeInheritable(GetStdHandle(-10));
                si.StartupInfo.hStdOutput = MakeInheritable(GetStdHandle(-11));
                si.StartupInfo.hStdError = MakeInheritable(GetStdHandle(-12));
            }

            uint flags = EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT;
            if (newConsole) flags |= CREATE_NEW_CONSOLE;

            var cmdBuf = new StringBuilder(commandLine, commandLine.Length + 1);
            if (!CreateProcessW(exePath, cmdBuf.ToString(), IntPtr.Zero, IntPtr.Zero, inheritStdio, flags,
                    IntPtr.Zero, workingDirectory, ref si, out var info))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "创建子进程失败");

            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);
            return info.dwProcessId;
        }
        finally
        {
            if (attrList != IntPtr.Zero) { DeleteProcThreadAttributeList(attrList); Marshal.FreeHGlobal(attrList); }
            if (parentValue != IntPtr.Zero) Marshal.FreeHGlobal(parentValue);
            CloseHandle(parent);
        }
    }

    private static IntPtr MakeInheritable(IntPtr h)
    {
        if (h == IntPtr.Zero || h == new IntPtr(-1)) return h;
        SetHandleInformation(h, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT);
        return h;
    }

    public static string BuildCommandLine(string exe, IReadOnlyList<string> args)
    {
        var sb = new StringBuilder();
        sb.Append(Quote(exe));
        foreach (var a in args) sb.Append(' ').Append(Quote(a));
        return sb.ToString();
    }

    private static string Quote(string s)
    {
        if (s.Length > 0 && s.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return s;
        var sb = new StringBuilder("\"");
        for (int i = 0; i < s.Length; i++)
        {
            int backslashes = 0;
            while (i < s.Length && s[i] == '\\') { backslashes++; i++; }
            if (i == s.Length) { sb.Append('\\', backslashes * 2); break; }
            if (s[i] == '"') sb.Append('\\', backslashes * 2 + 1).Append('"');
            else sb.Append('\\', backslashes).Append(s[i]);
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static IntPtr BuildEnvironmentBlock(IDictionary<string, string> env)
    {
        var sb = new StringBuilder();
        foreach (var kv in env) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        sb.Append('\0');
        var bytes = Encoding.Unicode.GetBytes(sb.ToString());
        var block = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, block, bytes.Length);
        return block;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr h, out uint code);

    /// <summary>Returns the child's exit code, used to diagnose why it disappeared immediately.</summary>
    public static uint? TryGetExitCode(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            if (!GetExitCodeProcess(h, out uint code)) return null;
            return code;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Copies the current process environment into a dictionary so individual keys can be overridden.</summary>
    public static Dictionary<string, string> CurrentEnvironment()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            if (e.Key is string k && e.Value is string v) dict[k] = v;
        }
        return dict;
    }

    /// <summary>
    /// Resolves the parent process: reuses a running instance whose image path equals launcherPath,
    /// otherwise starts launcherPath. Matching by process name alone would catch a running real
    /// WPFLauncher (same name as the stub), so the image path must be compared. launcherPath may be
    /// relative to the application directory.
    /// </summary>
    public static int EnsureLauncher(string launcherPath, string? processName = "WPFLauncher", int waitMs = 30000)
    {
        var full = Path.GetFullPath(launcherPath, AppContext.BaseDirectory);
        if (!File.Exists(full))
        {
            var sideBySide = Path.Combine(AppContext.BaseDirectory, (processName ?? "WPFLauncher") + ".exe");
            if (File.Exists(sideBySide)) full = sideBySide;
            else throw new FileNotFoundException("启动器/替身不存在: " + full);
        }
        var name = processName ?? Path.GetFileNameWithoutExtension(full);

        int? FindRunning()
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    if (p.HasExited) continue;
                    if (string.Equals(ImagePath(p.Id), full, StringComparison.OrdinalIgnoreCase)) return p.Id;
                    Logging.Log.Debug($"[parent] 跳过同名进程 pid={p.Id} path={ImagePath(p.Id) ?? "?"}");
                }
                catch { }
            }
            return null;
        }

        if (FindRunning() is { } running)
        {
            Logging.Log.Info($"[parent] 复用 {full} pid={running}");
            return running;
        }

        Logging.Log.Info("[parent] 启动 " + full);
        var started = Process.Start(new ProcessStartInfo
        {
            FileName = full,
            WorkingDirectory = Path.GetDirectoryName(full)!,
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
        while (DateTime.UtcNow < deadline)
        {
            if (started is { HasExited: false }) return started.Id;
            if (FindRunning() is { } pid) return pid;
            Thread.Sleep(200);
        }
        throw new TimeoutException("等待启动器进程超时: " + full);
    }

    /// <summary>Full image path of a process, via QueryFullProcessImageName (MainModule is denied for elevated or foreign processes).</summary>
    private static string? ImagePath(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally { CloseHandle(h); }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, int flags, StringBuilder exeName, ref int size);
}
