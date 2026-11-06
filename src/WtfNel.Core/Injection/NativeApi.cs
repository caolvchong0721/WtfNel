using System.Runtime.InteropServices;
using System.Text;

namespace WtfNel.Core.Injection;

/// <summary>Win32 declarations used by the injector.</summary>
internal static class NativeApi
{
    internal const uint PageNoAccess = 0x01;
    internal const uint PageReadOnly = 0x02;
    internal const uint PageReadWrite = 0x04;
    internal const uint PageExecuteRead = 0x20;
    internal const uint PageExecuteReadWrite = 0x40;
    internal const uint PageGuard = 0x100;

    internal const uint MemCommit = 0x1000;
    internal const uint MemReserve = 0x2000;
    internal const uint MemRelease = 0x8000;

    internal const uint ProcessCreateThread = 0x0002;
    internal const uint ProcessQueryInformation = 0x0400;
    internal const uint ProcessVmOperation = 0x0008;
    internal const uint ProcessVmWrite = 0x0020;
    internal const uint ProcessVmRead = 0x0010;

    internal const uint TokenAdjustPrivileges = 0x0020;
    internal const uint TokenQuery = 0x0008;
    internal const uint SePrivilegeEnabled = 0x0002;

    internal const uint Th32csSnapProcess = 0x00000002;
    internal const uint Th32csSnapModule = 0x00000008;
    internal const uint Th32csSnapModule32 = 0x00000010;

    internal const uint Infinite = 0xFFFFFFFF;
    internal const uint DllProcessAttach = 1;
    internal const uint DllProcessDetach = 0;

    internal const ushort ImageFileMachineAmd64 = 0x8664;
    internal const ushort ImageFileMachineArm64 = 0xAA64;
    internal const ushort ImageFileMachineUnknown = 0x0000;

    [DllImport("kernel32", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32", SetLastError = true)]
    internal static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, nuint size, uint type, uint protect);

    [DllImport("kernel32", SetLastError = true)]
    internal static extern bool VirtualFreeEx(IntPtr process, IntPtr address, nuint size, uint type);

    [DllImport("kernel32", SetLastError = true)]
    internal static extern bool VirtualProtectEx(IntPtr process, IntPtr address, nuint size, uint newProtect, out uint oldProtect);

    [DllImport("kernel32", SetLastError = true)]
    internal static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, nuint size, out nuint read);

    [DllImport("kernel32", SetLastError = true)]
    internal static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, nuint size, out nuint written);

    [DllImport("kernel32", SetLastError = true)]
    internal static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, nuint stackSize, IntPtr start, IntPtr parameter, uint flags, out int threadId);

    [DllImport("kernel32", SetLastError = true)]
    internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32", SetLastError = true)]
    internal static extern bool GetExitCodeThread(IntPtr thread, out uint exitCode);

    [DllImport("kernel32", SetLastError = true)]
    internal static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandleW(string moduleName);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false)]
    internal static extern IntPtr GetProcAddress(IntPtr module, string procName);

    [DllImport("kernel32", EntryPoint = "GetProcAddress", SetLastError = true)]
    internal static extern IntPtr GetProcAddressOrdinal(IntPtr module, IntPtr ordinal);

    [DllImport("kernel32", SetLastError = true)]
    internal static extern bool VirtualProtectRaw(IntPtr address, nuint size, uint newProtect, out uint oldProtect);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr LoadLibraryW(string path);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);

    internal const uint LoadLibrarySearchDllLoadDir = 0x00000100;
    internal const uint LoadLibrarySearchDefaultDirs = 0x00001000;

    [DllImport("kernel32", SetLastError = true)]
    internal static extern bool FreeLibrary(IntPtr module);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool GetModuleFileNameW(IntPtr module, StringBuilder buffer, int size);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder buffer, ref int size);

    [DllImport("kernel32", SetLastError = true)]
    internal static extern IntPtr CreateToolhelp32Snapshot(uint flags, int processId);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32W entry);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32W entry);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool Module32FirstW(IntPtr snapshot, ref ModuleEntry32W entry);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool Module32NextW(IntPtr snapshot, ref ModuleEntry32W entry);

    [DllImport("kernel32", SetLastError = true)]
    internal static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32", SetLastError = true)]
    internal static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

    [DllImport("kernel32", SetLastError = true)]
    internal static extern bool IsWow64Process(IntPtr process, out bool wow64);

    [DllImport("kernel32", SetLastError = true)]
    internal static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("kernel32", SetLastError = true)]
    internal static extern bool FlushInstructionCache(IntPtr process, IntPtr address, nuint size);

    [DllImport("advapi32", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);

    [DllImport("advapi32", SetLastError = true)]
    internal static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges newState, uint bufferLength, IntPtr previous, IntPtr returnLength);

    [DllImport("user32", CharSet = CharSet.Unicode)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

    [DllImport("user32", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextW(IntPtr window, StringBuilder text, int max);

    [DllImport("user32", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextLengthW(IntPtr window);

    [DllImport("user32")]
    internal static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32")]
    internal static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32", CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindowW(string? className, string? windowName);

    internal delegate bool EnumWindowsProc(IntPtr window, IntPtr param);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ProcessEntry32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ModuleEntry32W
    {
        public uint dwSize;
        public uint th32ModuleID;
        public uint th32ProcessID;
        public uint GlblcntUsage;
        public uint ProccntUsage;
        public IntPtr modBaseAddr;
        public uint modBaseSize;
        public IntPtr hModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid
    {
        public uint Low;
        public int High;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }
}
