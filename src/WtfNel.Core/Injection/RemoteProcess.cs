using System.Buffers.Binary;
using System.Text;

namespace WtfNel.Core.Injection;

/// <summary>Target process handle with memory access and a generated x64 call stub.</summary>
internal sealed class RemoteProcess : IDisposable
{
    private IntPtr _handle;

    private RemoteProcess(IntPtr handle, int processId)
    {
        _handle = handle;
        ProcessId = processId;
    }

    public int ProcessId { get; }
    public IntPtr Handle => _handle;

    public static RemoteProcess? Open(int processId, out string error)
    {
        error = "";
        EnableDebugPrivilege();

        uint access = NativeApi.ProcessCreateThread | NativeApi.ProcessQueryInformation |
                      NativeApi.ProcessVmOperation | NativeApi.ProcessVmWrite | NativeApi.ProcessVmRead;
        IntPtr handle = NativeApi.OpenProcess(access, false, processId);
        if (handle == IntPtr.Zero)
        {
            error = $"OpenProcess({processId}) 失败，Win32 错误 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}";
            return null;
        }
        return new RemoteProcess(handle, processId);
    }

    private static void EnableDebugPrivilege()
    {
        if (!NativeApi.OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle,
                NativeApi.TokenAdjustPrivileges | NativeApi.TokenQuery, out IntPtr token))
            return;

        try
        {
            var privileges = new NativeApi.TokenPrivileges { PrivilegeCount = 1, Attributes = NativeApi.SePrivilegeEnabled };
            if (NativeApi.LookupPrivilegeValueW(null, "SeDebugPrivilege", out long luid))
            {
                privileges.Luid.Low = (uint)luid;
                privileges.Luid.High = (int)(luid >> 32);
                NativeApi.AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
            }
        }
        finally
        {
            NativeApi.CloseHandle(token);
        }
    }

    public IntPtr Allocate(uint size, uint protect = NativeApi.PageExecuteReadWrite)
        => NativeApi.VirtualAllocEx(_handle, IntPtr.Zero, size, NativeApi.MemCommit | NativeApi.MemReserve, protect);

    public void Free(IntPtr address)
    {
        if (address != IntPtr.Zero) NativeApi.VirtualFreeEx(_handle, address, 0, NativeApi.MemRelease);
    }

    public bool Write(IntPtr address, byte[] buffer, int count)
        => NativeApi.WriteProcessMemory(_handle, address, buffer, (nuint)count, out nuint written)
           && written == (nuint)count;

    public byte[]? Read(IntPtr address, int size)
    {
        var buffer = new byte[size];
        if (!NativeApi.ReadProcessMemory(_handle, address, buffer, (nuint)size, out nuint read)) return null;
        if (read == 0) return null;
        return read == (nuint)size ? buffer : buffer[..(int)read];
    }

    public ulong ReadUInt64(IntPtr address)
    {
        var bytes = Read(address, 8);
        return bytes is { Length: 8 } ? BinaryPrimitives.ReadUInt64LittleEndian(bytes) : 0;
    }

    public bool Protect(IntPtr address, int size, uint newProtect, out uint oldProtect)
        => NativeApi.VirtualProtectEx(_handle, address, (nuint)size, newProtect, out oldProtect);

    public IntPtr CreateThread(IntPtr start, IntPtr parameter, out int threadId)
        => NativeApi.CreateRemoteThread(_handle, IntPtr.Zero, 0, start, parameter, 0, out threadId);

    public bool WaitThread(IntPtr thread, int milliseconds, out uint exitCode)
    {
        exitCode = 0;
        uint wait = NativeApi.WaitForSingleObject(thread, (uint)milliseconds);
        if (wait != 0) return false;
        return NativeApi.GetExitCodeThread(thread, out exitCode);
    }

    public IntPtr GetModuleBase(string moduleName)
    {
        IntPtr snapshot = NativeApi.CreateToolhelp32Snapshot(NativeApi.Th32csSnapModule | NativeApi.Th32csSnapModule32, ProcessId);
        if (snapshot == new IntPtr(-1)) return IntPtr.Zero;
        try
        {
            var entry = new NativeApi.ModuleEntry32W { dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeApi.ModuleEntry32W>() };
            if (!NativeApi.Module32FirstW(snapshot, ref entry)) return IntPtr.Zero;
            do
            {
                if (string.Equals(entry.szModule, moduleName, StringComparison.OrdinalIgnoreCase))
                    return entry.modBaseAddr;
            } while (NativeApi.Module32NextW(snapshot, ref entry));
            return IntPtr.Zero;
        }
        finally
        {
            NativeApi.CloseHandle(snapshot);
        }
    }

    public bool TryInvoke(IntPtr function, ulong[] args, int timeoutMs, out ulong result, out string error)
    {
        result = 0;
        error = "";
        if (function == IntPtr.Zero)
        {
            error = "目标函数地址为 0";
            return false;
        }

        uint size = 0x200;
        IntPtr block = Allocate(size);
        if (block == IntPtr.Zero)
        {
            error = "远程分配调用桩失败";
            return false;
        }

        IntPtr slot = block;
        IntPtr code = block + 0x100;
        try
        {
            byte[] stub = BuildCallStub(function, slot, args);
            if (!Write(code, stub, stub.Length))
            {
                error = "写调用桩失败";
                return false;
            }

            var zero = new byte[8];
            Write(slot, zero, 8);

            IntPtr thread = CreateThread(code, IntPtr.Zero, out int tid);
            if (thread == IntPtr.Zero)
            {
                error = $"CreateRemoteThread 失败，Win32 错误 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}";
                return false;
            }

            try
            {
                uint wait = NativeApi.WaitForSingleObject(thread, (uint)timeoutMs);
                if (wait != 0)
                {
                    error = $"远程调用超时（{timeoutMs} ms，tid {tid}）";
                    return false;
                }
                result = ReadUInt64(slot);
                return true;
            }
            finally
            {
                NativeApi.CloseHandle(thread);
            }
        }
        finally
        {
            Free(block);
        }
    }

    private byte[] BuildCallStub(IntPtr function, IntPtr slot, ulong[] args)
    {
        var code = new List<byte>(96);
        code.Add(0x53);
        code.AddRange([0x48, 0xBB]); code.AddRange(BitConverter.GetBytes((ulong)slot));
        code.AddRange([0x48, 0x83, 0xEC, 0x20]);

        byte[][] movs =
        [
            [0x48, 0xB9],
            [0x48, 0xBA],
            [0x49, 0xB8],
            [0x49, 0xB9],
        ];
        for (int i = 0; i < args.Length && i < movs.Length; i++)
        {
            code.AddRange(movs[i]);
            code.AddRange(BitConverter.GetBytes(args[i]));
        }

        code.AddRange([0x48, 0xB8]);
        code.AddRange(BitConverter.GetBytes((ulong)function));
        code.AddRange([0xFF, 0xD0]);
        code.AddRange([0x48, 0x89, 0x03]);
        code.AddRange([0x48, 0x83, 0xC4, 0x20]);
        code.Add(0x5B);
        code.Add(0xC3);

        var bytes = code.ToArray();
        NativeApi.FlushInstructionCache(_handle, IntPtr.Zero, 0);
        return bytes;
    }

    public int TargetArch()
    {
        if (NativeApi.IsWow64Process2(_handle, out ushort processMachine, out ushort nativeMachine))
        {
            if (processMachine == NativeApi.ImageFileMachineUnknown)
                return nativeMachine is NativeApi.ImageFileMachineAmd64 or NativeApi.ImageFileMachineArm64 ? 2 : 1;
            return processMachine is NativeApi.ImageFileMachineAmd64 or NativeApi.ImageFileMachineArm64 ? 2 : 1;
        }
        if (NativeApi.IsWow64Process(_handle, out bool wow64)) return wow64 ? 1 : 2;
        return 0;
    }

    public string ImagePath()
    {
        var buffer = new StringBuilder(1024);
        int size = buffer.Capacity;
        return NativeApi.QueryFullProcessImageNameW(_handle, 0, buffer, ref size) ? buffer.ToString() : "";
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        NativeApi.CloseHandle(_handle);
        _handle = IntPtr.Zero;
    }
}
