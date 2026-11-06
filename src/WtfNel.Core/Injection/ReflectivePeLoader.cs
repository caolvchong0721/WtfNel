using System.Diagnostics;

namespace WtfNel.Core.Injection;

/// <summary>Classic reflective injection: write the file and start a remote thread at ReflectiveLoader.</summary>
internal static class ReflectivePeLoader
{
    private const string ReflectiveLoaderExport = "ReflectiveLoader";

    public static bool HasReflectiveLoader(PeImage pe) =>
        pe.TryGetExport(ReflectiveLoaderExport, out _, out _);

    public static InjectionResult Inject(RemoteProcess process, PeImage pe, InjectionOptions options, Stopwatch clock)
    {
        if (!pe.TryGetExport(ReflectiveLoaderExport, out uint exportRva, out _))
            return InjectionResult.Fail(InjectionStrategy.Reflective, "payload 没有导出 ReflectiveLoader，无法走反射注入", clock.Elapsed);

        uint loaderOffset = pe.RvaToOffset(exportRva);
        if (loaderOffset == 0)
            return InjectionResult.Fail(InjectionStrategy.Reflective, "ReflectiveLoader 导出的 RVA 无法映射回文件偏移", clock.Elapsed);

        int targetArch = process.TargetArch();
        int payloadArch = pe.Is64Bit ? 2 : 1;
        if (targetArch != 0 && targetArch != payloadArch)
        {
            return InjectionResult.Fail(InjectionStrategy.Reflective,
                $"位宽不匹配：目标 pid {process.ProcessId} 是 {ArchName(targetArch)}，payload 是 {ArchName(payloadArch)}", clock.Elapsed);
        }

        IntPtr remote = process.Allocate(pe.SizeOfImage == 0 ? (uint)pe.Bytes.Length : pe.SizeOfImage);
        if (remote == IntPtr.Zero)
            return InjectionResult.Fail(InjectionStrategy.Reflective, "VirtualAllocEx 失败", clock.Elapsed);

        bool keepBuffer = false;
        try
        {
            if (!process.Write(remote, pe.Bytes, pe.Bytes.Length))
                return InjectionResult.Fail(InjectionStrategy.Reflective, "WriteProcessMemory 失败", clock.Elapsed);

            IntPtr start = remote + (int)loaderOffset;
            IntPtr thread = process.CreateThread(start, options.Parameter, out int tid);
            if (thread == IntPtr.Zero)
                return InjectionResult.Fail(InjectionStrategy.Reflective,
                    $"CreateRemoteThread 失败，Win32 错误 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}", clock.Elapsed);

            try
            {
                bool finished = process.WaitThread(thread, options.WaitMs, out uint exitCode);
                if (!finished)
                    return InjectionResult.Ok(InjectionStrategy.Reflective,
                        $"已注入 pid {process.ProcessId}，loader 仍在运行（tid {tid}，等满 {options.WaitMs} ms）", clock.Elapsed, (ulong)remote);

                if (exitCode == 0)
                    return InjectionResult.Fail(InjectionStrategy.Reflective,
                        $"远程线程已结束但 ReflectiveLoader 返回 0（目标内部映射失败：导入/TLS/重定位），tid {tid}", clock.Elapsed, (ulong)remote);

                keepBuffer = true;
                return InjectionResult.Ok(InjectionStrategy.Reflective,
                    $"已注入 pid {process.ProcessId}，loader 退出码 {exitCode}，镜像基址 0x{remote:x}", clock.Elapsed, (ulong)remote);
            }
            finally
            {
                NativeApi.CloseHandle(thread);
            }
        }
        finally
        {
            if (!keepBuffer) process.Free(remote);
        }
    }

    public static IntPtr LoadInCurrentProcess(PeImage pe, out string detail)
    {
        detail = "";
        if (!pe.TryGetExport(ReflectiveLoaderExport, out uint exportRva, out _))
        {
            detail = "没有 ReflectiveLoader 导出";
            return IntPtr.Zero;
        }

        uint offset = pe.RvaToOffset(exportRva);
        if (offset == 0)
        {
            detail = "无法映射导出偏移";
            return IntPtr.Zero;
        }

        IntPtr buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(pe.Bytes.Length);
        System.Runtime.InteropServices.Marshal.Copy(pe.Bytes, 0, buffer, pe.Bytes.Length);
        try
        {
            if (!NativeApi.VirtualProtectRaw(buffer, (nuint)pe.Bytes.Length, NativeApi.PageExecuteReadWrite, out uint old))
            {
                detail = "VirtualProtect 失败";
                return IntPtr.Zero;
            }

            var loader = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<ReflectiveLoaderFn>(buffer + (int)offset);
            loader();
            detail = "本进程反射加载完成";
            _ = old;
            return buffer;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return IntPtr.Zero;
        }
    }

    private delegate void ReflectiveLoaderFn();

    private static string ArchName(int arch) => arch switch
    {
        2 => "x64",
        1 => "x86",
        _ => "未知",
    };
}
