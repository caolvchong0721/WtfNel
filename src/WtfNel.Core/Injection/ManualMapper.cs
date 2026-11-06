using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WtfNel.Core.Injection;

/// <summary>Manual mapping for payloads that have no native reflective loader.</summary>
internal static class ManualMapper
{
    public static InjectionResult Inject(RemoteProcess process, PeImage pe, InjectionOptions options, Stopwatch clock)
    {
        var log = options.Log ?? (_ => { });

        if (!pe.Is64Bit)
            return InjectionResult.Fail(InjectionStrategy.ManualMap, "手动映射只实现了 x64 路径（payload 是 32 位）", clock.Elapsed);

        int arch = process.TargetArch();
        if (arch == 1)
            return InjectionResult.Fail(InjectionStrategy.ManualMap, "目标进程是 32 位，x64 注入器无法映射", clock.Elapsed);

        IntPtr remoteBase = process.Allocate(pe.SizeOfImage, NativeApi.PageExecuteReadWrite);
        if (remoteBase == IntPtr.Zero)
            return InjectionResult.Fail(InjectionStrategy.ManualMap, "VirtualAllocEx(SizeOfImage) 失败", clock.Elapsed);

        log($"手动映射：远程基址 0x{(ulong)remoteBase:x}，SizeOfImage 0x{pe.SizeOfImage:x}");

        try
        {
            byte[] image = BuildImage(pe, remoteBase, log);
            var resolver = new ImportResolver(process, log);
            if (!resolver.Apply(pe, image))
                return InjectionResult.Fail(InjectionStrategy.ManualMap, "导入表解析失败", clock.Elapsed, (ulong)remoteBase);

            if (!process.Write(remoteBase, image, image.Length))
                return InjectionResult.Fail(InjectionStrategy.ManualMap, "WriteProcessMemory(镜像) 失败", clock.Elapsed, (ulong)remoteBase);

            ApplyProtections(process, pe, remoteBase, log);
            RegisterExceptionTable(process, pe, remoteBase, log);
            RunTlsCallbacks(process, pe, image, remoteBase, log);

            if (pe.EntryPointRva != 0)
            {
                IntPtr entry = remoteBase + (int)pe.EntryPointRva;
                if (!process.TryInvoke(entry, [(ulong)remoteBase, NativeApi.DllProcessAttach, 0], options.WaitMs, out ulong reason, out string entryError))
                {
                    log($"DllMain 调用失败：{entryError}");
                    return InjectionResult.Fail(InjectionStrategy.ManualMap, "远程 DllMain 调用失败：" + entryError, clock.Elapsed, (ulong)remoteBase);
                }
                log($"DllMain 返回 0x{reason:x}");
            }

            if (!string.IsNullOrWhiteSpace(options.InitExport))
            {
                if (!pe.TryGetExport(options.InitExport!, out uint initRva, out _))
                {
                    return InjectionResult.Fail(InjectionStrategy.ManualMap,
                        $"镜像里没有导出 {options.InitExport}", clock.Elapsed, (ulong)remoteBase);
                }

                IntPtr init = remoteBase + (int)initRva;
                if (!process.TryInvoke(init, [], options.WaitMs, out ulong initResult, out string initError))
                {
                    return InjectionResult.Fail(InjectionStrategy.ManualMap,
                        $"调用 {options.InitExport} 失败：{initError}", clock.Elapsed, (ulong)remoteBase);
                }

                log($"{options.InitExport} 返回 0x{initResult:x}");
                if (initResult == 0)
                {
                    return InjectionResult.Fail(InjectionStrategy.ManualMap,
                        $"{options.InitExport} 返回 0（payload 内部初始化失败）", clock.Elapsed, (ulong)remoteBase);
                }
            }

            return InjectionResult.Ok(InjectionStrategy.ManualMap,
                $"已手动映射到 pid {process.ProcessId}，基址 0x{(ulong)remoteBase:x}", clock.Elapsed, (ulong)remoteBase);
        }
        catch (Exception ex)
        {
            process.Free(remoteBase);
            return InjectionResult.Fail(InjectionStrategy.ManualMap, "手动映射异常：" + ex.Message, clock.Elapsed, 0, ex);
        }
    }

    private static byte[] BuildImage(PeImage pe, IntPtr remoteBase, Action<string> log)
    {
        var image = new byte[pe.SizeOfImage];
        byte[] file = pe.Bytes;

        int headerLength = (int)Math.Min(pe.SizeOfHeaders, (uint)file.Length);
        Array.Copy(file, image, headerLength);

        foreach (var section in pe.Sections)
        {
            if (section.RawSize == 0) continue;
            long available = Math.Min(section.RawSize, (long)file.Length - section.RawOffset);
            if (available <= 0) continue;
            long room = (long)image.Length - section.VirtualAddress;
            int copy = (int)Math.Min(available, Math.Max(0, room));
            if (copy <= 0) continue;
            Array.Copy(file, section.RawOffset, image, section.VirtualAddress, copy);
        }

        ulong delta = (ulong)remoteBase - pe.ImageBase;
        if (delta == 0)
        {
            log("重定位：远程基址与首选基址相同，跳过");
            return image;
        }

        int applied = 0;
        foreach (var block in pe.ReadRelocations())
        {
            foreach (ushort raw in block.Entries)
            {
                int type = raw >> 12;
                int offset = raw & 0x0FFF;
                long at = (long)block.PageRva + offset;
                if (at < 0 || at + 8 > image.Length) continue;

                switch (type)
                {
                    case 0:
                        break;
                    case 10:
                    {
                        ulong value = BitConverter.ToUInt64(image, (int)at);
                        if (value == 0) break;
                        BitConverter.GetBytes(value + delta).CopyTo(image, at);
                        applied++;
                        break;
                    }
                    case 3:
                    {
                        uint value = BitConverter.ToUInt32(image, (int)at);
                        if (value == 0) break;
                        BitConverter.GetBytes(value + (uint)delta).CopyTo(image, at);
                        applied++;
                        break;
                    }
                    default:
                        log($"重定位：跳过不支持的类型 {type}（page 0x{block.PageRva:x}）");
                        break;
                }
            }
        }
        log($"重定位：应用 {applied} 项，delta 0x{delta:x}");
        return image;
    }

    private static void ApplyProtections(RemoteProcess process, PeImage pe, IntPtr remoteBase, Action<string> log)
    {
        if (!process.Protect(remoteBase, (int)pe.SizeOfHeaders, NativeApi.PageReadOnly, out _))
            log("保护：头部改只读失败（继续）");

        foreach (var section in pe.Sections)
        {
            if (section.MappedSpan == 0) continue;
            uint protect = PeImage.ProtectionFor(section);
            if (!process.Protect(remoteBase + (int)section.VirtualAddress, (int)section.MappedSpan, protect, out _))
                log($"保护：节 {section.Name} 设置 0x{protect:x} 失败（继续）");
        }
    }

    private static void RegisterExceptionTable(RemoteProcess process, PeImage pe, IntPtr remoteBase, Action<string> log)
    {
        var (tableRva, tableSize) = pe.Directories[3];
        if (tableRva == 0 || tableSize < 12) return;

        IntPtr remoteNtdll = process.GetModuleBase("ntdll.dll");
        IntPtr localNtdll = NativeApi.GetModuleHandleW("ntdll.dll");
        if (remoteNtdll == IntPtr.Zero || localNtdll == IntPtr.Zero)
        {
            log("异常表：定位 ntdll 失败");
            return;
        }

        IntPtr localAdd = NativeApi.GetProcAddress(localNtdll, "RtlAddFunctionTable");
        if (localAdd == IntPtr.Zero) return;
        IntPtr remoteAdd = remoteNtdll + (int)((ulong)localAdd - (ulong)localNtdll);

        uint count = tableSize / 12;
        IntPtr table = remoteBase + (int)tableRva;
        if (!process.TryInvoke(remoteAdd, [(ulong)table, count, (ulong)remoteBase], 10000, out ulong accepted, out string error))
        {
            log("异常表：RtlAddFunctionTable 调用失败：" + error);
            return;
        }

        log($"异常表：注册 {count} 项，返回 {accepted}");
    }

    private static void RunTlsCallbacks(RemoteProcess process, PeImage pe, byte[] image, IntPtr remoteBase, Action<string> log)    {
        if (pe.ReadTls() is not { } tls || tls.AddressOfCallbacksRva == 0) return;

        long at = tls.AddressOfCallbacksRva;
        int count = 0;
        while (at + 8 <= image.Length)
        {
            ulong callback = BitConverter.ToUInt64(image, (int)at);
            if (callback == 0) break;

            ulong remoteCallback = callback - pe.ImageBase + (ulong)remoteBase;
            if (!process.TryInvoke((IntPtr)remoteCallback, [(ulong)remoteBase, NativeApi.DllProcessAttach, 0], 10000, out _, out string error))
            {
                log($"TLS 回调 0x{remoteCallback:x} 失败：{error}");
                break;
            }
            count++;
            at += 8;
        }
        log($"TLS 回调：执行 {count} 个");
    }

    private sealed class ImportResolver(RemoteProcess process, Action<string> log)
    {
        private readonly Dictionary<string, IntPtr> _remoteModules = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IntPtr> _localModules = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<LocalModule> _localList = ListLocalModules();

        public bool Apply(PeImage pe, byte[] image)
        {
            var imports = pe.ReadImports();
            if (imports.Count == 0)
            {
                log("导入表为空");
                return true;
            }

            int resolved = 0, failed = 0;
            foreach (var import in imports)
            {
                foreach (var symbol in import.Symbols)
                {
                    ulong target = Resolve(import.Module, symbol);
                    if (target == 0)
                    {
                        failed++;
                        if (failed <= 8) log($"导入未解析：{import.Module}!{symbol.Name ?? $"#{symbol.Ordinal}"}");
                        continue;
                    }
                    long at = symbol.IatRva;
                    if (at + 8 > image.Length) continue;
                    BitConverter.GetBytes(target).CopyTo(image, at);
                    resolved++;
                }
            }

            log($"导入：解析 {resolved} 项，失败 {failed} 项");
            return failed == 0;
        }

        private ulong Resolve(string moduleName, PeImportSymbol symbol)
        {
            try
            {
                IntPtr local = LoadLocal(moduleName);
                if (local == IntPtr.Zero)
                {
                    IntPtr remote = RemoteLoadLibrary(moduleName);
                    return remote == IntPtr.Zero ? 0 : RemoteGetProcAddress((ulong)remote, symbol);
                }

                IntPtr address = symbol.ByOrdinal
                    ? NativeApi.GetProcAddressOrdinal(local, (IntPtr)symbol.Ordinal)
                    : NativeApi.GetProcAddress(local, symbol.Name!);
                if (address == IntPtr.Zero) return 0;

                var owner = LocalModuleOf(address);
                if (owner is null)
                {
                    uint rvaFallback = (uint)(address - local);
                    return (ulong)RemoteModule(moduleName) + rvaFallback;
                }

                IntPtr remoteOwner = RemoteModule(owner.Name);
                if (remoteOwner == IntPtr.Zero) return 0;
                return (ulong)remoteOwner + (ulong)(address - owner.Base);
            }
            catch (Exception ex)
            {
                log($"导入 {moduleName} 异常：{ex.Message}");
                return 0;
            }
        }

        private IntPtr LoadLocal(string moduleName)
        {
            if (_localModules.TryGetValue(moduleName, out IntPtr cached)) return cached;

            IntPtr handle = NativeApi.LoadLibraryW(moduleName);
            if (handle == IntPtr.Zero) handle = NativeApi.LoadLibraryExW(moduleName, IntPtr.Zero, NativeApi.LoadLibrarySearchDefaultDirs | NativeApi.LoadLibrarySearchDllLoadDir);

            if (handle != IntPtr.Zero) _localModules[moduleName] = handle;
            return handle;
        }

        private IntPtr RemoteModule(string moduleName)
        {
            if (_remoteModules.TryGetValue(moduleName, out IntPtr cached)) return cached;

            IntPtr remote = process.GetModuleBase(moduleName);
            if (remote == IntPtr.Zero) remote = RemoteLoadLibrary(moduleName);
            if (remote != IntPtr.Zero)
            {
                _remoteModules[moduleName] = remote;
                log($"远程模块 {moduleName} = 0x{(ulong)remote:x}");
            }
            return remote;
        }

        private IntPtr RemoteLoadLibrary(string moduleName)
        {
            IntPtr remoteKernel32 = process.GetModuleBase("kernel32.dll");
            IntPtr localKernel32 = NativeApi.GetModuleHandleW("kernel32.dll");
            if (remoteKernel32 == IntPtr.Zero || localKernel32 == IntPtr.Zero) return IntPtr.Zero;

            IntPtr localLoadLibraryA = NativeApi.GetProcAddress(localKernel32, "LoadLibraryA");
            if (localLoadLibraryA == IntPtr.Zero) return IntPtr.Zero;
            IntPtr remoteLoadLibraryA = remoteKernel32 + (int)((ulong)localLoadLibraryA - (ulong)localKernel32);

            byte[] name = Encoding.ASCII.GetBytes(moduleName + "\0");
            IntPtr buffer = process.Allocate((uint)name.Length, NativeApi.PageReadWrite);
            if (buffer == IntPtr.Zero) return IntPtr.Zero;

            IntPtr result = IntPtr.Zero;
            try
            {
                if (!process.Write(buffer, name, name.Length)) return IntPtr.Zero;
                if (!process.TryInvoke(remoteLoadLibraryA, [(ulong)buffer], 10000, out ulong value, out string error))
                {
                    log($"远程 LoadLibrary({moduleName}) 失败：{error}");
                    return IntPtr.Zero;
                }
                result = (IntPtr)(long)value;
            }
            finally
            {
                process.Free(buffer);
            }

            if (result != IntPtr.Zero) log($"远程 LoadLibrary({moduleName}) = 0x{(ulong)result:x}");
            return result;
        }

        private ulong RemoteGetProcAddress(ulong remoteModule, PeImportSymbol symbol)
        {
            IntPtr remoteKernel32 = process.GetModuleBase("kernel32.dll");
            IntPtr localKernel32 = NativeApi.GetModuleHandleW("kernel32.dll");
            if (remoteKernel32 == IntPtr.Zero || localKernel32 == IntPtr.Zero) return 0;

            IntPtr localGetProcAddress = NativeApi.GetProcAddress(localKernel32, "GetProcAddress");
            if (localGetProcAddress == IntPtr.Zero) return 0;
            IntPtr remoteGetProcAddress = remoteKernel32 + (int)((ulong)localGetProcAddress - (ulong)localKernel32);

            if (symbol.ByOrdinal)
            {
                return process.TryInvoke(remoteGetProcAddress, [remoteModule, symbol.Ordinal], 10000, out ulong ordinalResult, out _)
                    ? ordinalResult : 0;
            }

            byte[] name = Encoding.ASCII.GetBytes(symbol.Name + "\0");
            IntPtr buffer = process.Allocate((uint)name.Length, NativeApi.PageReadWrite);
            if (buffer == IntPtr.Zero) return 0;
            try
            {
                if (!process.Write(buffer, name, name.Length)) return 0;
                return process.TryInvoke(remoteGetProcAddress, [remoteModule, (ulong)buffer], 10000, out ulong value, out _)
                    ? value : 0;
            }
            finally
            {
                process.Free(buffer);
            }
        }

        private sealed record LocalModule(IntPtr Base, string Name, uint Size);

        private LocalModule? LocalModuleOf(IntPtr address)
        {
            ulong value = (ulong)address;
            foreach (var module in _localList)
            {
                ulong start = (ulong)module.Base;
                if (value >= start && value < start + Math.Max(module.Size, 0x1000u)) return module;
            }
            return null;
        }

        private static List<LocalModule> ListLocalModules()
        {
            var result = new List<LocalModule>();
            IntPtr snapshot = NativeApi.CreateToolhelp32Snapshot(NativeApi.Th32csSnapModule | NativeApi.Th32csSnapModule32, Environment.ProcessId);
            if (snapshot == new IntPtr(-1)) return result;

            try
            {
                var entry = new NativeApi.ModuleEntry32W { dwSize = (uint)Marshal.SizeOf<NativeApi.ModuleEntry32W>() };
                if (!NativeApi.Module32FirstW(snapshot, ref entry)) return result;
                do
                {
                    result.Add(new LocalModule(entry.modBaseAddr, entry.szModule, entry.modBaseSize));
                } while (NativeApi.Module32NextW(snapshot, ref entry));
            }
            finally
            {
                NativeApi.CloseHandle(snapshot);
            }
            return result;
        }
    }
}
