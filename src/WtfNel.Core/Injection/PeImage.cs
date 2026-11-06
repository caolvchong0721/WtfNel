using System.Buffers.Binary;
using System.Text;

namespace WtfNel.Core.Injection;

internal readonly record struct PeSection(
    string Name, uint VirtualAddress, uint VirtualSize, uint RawOffset, uint RawSize, uint Characteristics)
{
    public uint FileSpan => RawSize != 0 ? RawSize : VirtualSize;

    public uint MappedSpan => VirtualSize != 0 ? VirtualSize : RawSize;

    public bool IsExecutable => (Characteristics & PeImage.ScnMemExecute) != 0;
    public bool IsWritable => (Characteristics & PeImage.ScnMemWrite) != 0;
    public bool IsReadable => (Characteristics & PeImage.ScnMemRead) != 0;
}

internal readonly record struct PeImportSymbol(string? Name, ushort Ordinal, bool ByOrdinal, uint IatRva);

internal sealed record PeImport(string Module, PeImportSymbol[] Symbols);

internal readonly record struct PeRelocationBlock(uint PageRva, ushort[] Entries);

internal readonly record struct PeTlsInfo(ulong AddressOfCallbacks, uint AddressOfCallbacksRva, uint SizeOfZeroFill, uint Characteristics);

/// <summary>Managed PE parser: headers, sections, exports, imports, relocations and TLS. Read only.</summary>
internal sealed class PeImage
{
    internal const uint ScnMemExecute = 0x2000_0000;
    internal const uint ScnMemRead = 0x4000_0000;
    internal const uint ScnMemWrite = 0x8000_0000;

    private const ushort DosSignature = 0x5A4D;
    private const uint NtSignature = 0x0000_4550;
    private const ushort Pe32Magic = 0x010B;
    private const ushort Pe32PlusMagic = 0x020B;

    private PeImage(byte[] bytes)
    {
        Bytes = bytes;
        NtOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x3C));

        int optional = checked((int)NtOffset + 0x18);
        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(optional));
        Is64Bit = magic == Pe32PlusMagic;
        Machine = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(checked((int)NtOffset + 4)));
        Characteristics = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(checked((int)NtOffset + 0x16)));
        NumberOfSections = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(checked((int)NtOffset + 6)));
        SizeOfOptionalHeader = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(checked((int)NtOffset + 0x14)));

        int opt = optional;
        EntryPointRva = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(opt + 0x10));
        ImageBase = Is64Bit
            ? BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(opt + 0x18))
            : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(opt + 0x1C));
        SectionAlignment = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(opt + 0x20));
        SizeOfImage = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(opt + 0x38));
        SizeOfHeaders = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(opt + 0x3C));

        int dirOffset = opt + (Is64Bit ? 112 : 96);
        var dirs = new (uint Rva, uint Size)[16];
        for (int i = 0; i < 16; i++)
        {
            int p = dirOffset + i * 8;
            if (p + 8 > bytes.Length) break;
            dirs[i] = (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p)),
                       BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p + 4)));
        }
        Directories = dirs;

        int sectionTable = opt + SizeOfOptionalHeader;
        var sections = new PeSection[NumberOfSections];
        for (int i = 0; i < NumberOfSections; i++)
        {
            int p = sectionTable + i * 40;
            if (p + 40 > bytes.Length) break;
            sections[i] = new PeSection(
                ReadName(bytes, p, 8),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p + 12)),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p + 8)),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p + 20)),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p + 16)),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p + 36)));
        }
        Sections = sections;
    }

    public byte[] Bytes { get; }
    public uint NtOffset { get; }
    public ushort Machine { get; }
    public ushort Characteristics { get; }
    public ushort NumberOfSections { get; }
    public ushort SizeOfOptionalHeader { get; }
    public bool Is64Bit { get; }
    public bool IsDll => (Characteristics & 0x2000) != 0;
    public ulong ImageBase { get; }
    public uint EntryPointRva { get; }
    public uint SectionAlignment { get; }
    public uint SizeOfImage { get; }
    public uint SizeOfHeaders { get; }
    public PeSection[] Sections { get; }
    public (uint Rva, uint Size)[] Directories { get; }

    public string ArchName => Machine switch
    {
        0x8664 => "x64 (64-bit)",
        0x014C => "x86 (32-bit)",
        0xAA64 => "arm64",
        0x01C4 => "arm",
        _ => $"unknown(0x{Machine:x4})",
    };

    public static bool TryParse(byte[] file, out PeImage image, out string error)
    {
        image = null!;
        error = "";
        try
        {
            if (file.Length < 0x40)
            {
                error = "文件太小，不是 PE";
                return false;
            }
            if (BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(0)) != DosSignature)
            {
                error = "缺少 MZ 头";
                return false;
            }

            uint nt = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(0x3C));
            if (nt == 0 || nt + 0x18 + 2 > file.Length)
            {
                error = "e_lfanew 越界";
                return false;
            }
            if (BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan((int)nt)) != NtSignature)
            {
                error = "缺少 PE 头";
                return false;
            }

            ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan((int)nt + 0x18));
            if (magic != Pe32Magic && magic != Pe32PlusMagic)
            {
                error = $"可选头 Magic 0x{magic:x4} 不受支持";
                return false;
            }

            var parsed = new PeImage(file);
            if (parsed.SizeOfImage == 0 || parsed.Sections.Length == 0)
            {
                error = "SizeOfImage/节表为空";
                return false;
            }

            image = parsed;
            return true;
        }
        catch (Exception ex)
        {
            error = "PE 解析异常: " + ex.Message;
            return false;
        }
    }

    public uint RvaToOffset(uint rva)
    {
        if (rva < SizeOfHeaders) return rva;
        foreach (var s in Sections)
        {
            if (rva >= s.VirtualAddress && rva < s.VirtualAddress + s.FileSpan)
                return rva - s.VirtualAddress + s.RawOffset;
        }
        return 0;
    }

    public PeSection? SectionForRva(uint rva)
    {
        foreach (var s in Sections)
            if (rva >= s.VirtualAddress && rva < s.VirtualAddress + Math.Max(s.MappedSpan, s.FileSpan))
                return s;
        return null;
    }

    public bool TryGetExport(string name, out uint rva, out bool forwarder)
    {
        rva = 0;
        forwarder = false;
        var (exportRva, exportSize) = Directories[0];
        if (exportRva == 0) return false;

        uint dirOff = RvaToOffset(exportRva);
        if (dirOff == 0) return false;

        uint numberOfNames = ReadU32(dirOff + 0x18);
        uint addressOfFunctions = ReadU32(dirOff + 0x1C);
        uint addressOfNames = ReadU32(dirOff + 0x20);
        uint addressOfOrdinals = ReadU32(dirOff + 0x24);

        uint namesOff = RvaToOffset(addressOfNames);
        uint ordinalsOff = RvaToOffset(addressOfOrdinals);
        uint funcsOff = RvaToOffset(addressOfFunctions);
        if (namesOff == 0 || ordinalsOff == 0 || funcsOff == 0) return false;

        for (uint i = 0; i < numberOfNames; i++)
        {
            uint nameRva = ReadU32(namesOff + i * 4);
            uint nameOff = RvaToOffset(nameRva);
            if (nameOff == 0) continue;
            if (!string.Equals(ReadCString(nameOff), name, StringComparison.Ordinal)) continue;

            int index = ReadU16(ordinalsOff + i * 2);
            rva = ReadU32(funcsOff + (uint)index * 4);
            forwarder = rva >= exportRva && rva < exportRva + exportSize;
            return rva != 0;
        }
        return false;
    }

    public IReadOnlyList<PeImport> ReadImports()
    {
        var list = new List<PeImport>();
        var (importRva, _) = Directories[1];
        if (importRva == 0) return list;

        uint descOff = RvaToOffset(importRva);
        if (descOff == 0) return list;

        int thunkSize = Is64Bit ? 8 : 4;
        ulong ordinalFlag = Is64Bit ? 0x8000_0000_0000_0000UL : 0x8000_0000UL;

        for (uint i = 0; ; i++)
        {
            uint d = descOff + i * 20;
            if (d + 20 > Bytes.Length) break;
            uint nameRva = ReadU32(d + 12);
            uint firstThunk = ReadU32(d + 16);
            uint originalThunk = ReadU32(d + 0);
            if (nameRva == 0 && firstThunk == 0) break;

            uint nameOff = RvaToOffset(nameRva);
            string module = nameOff != 0 ? ReadCString(nameOff) : $"<rva 0x{nameRva:x}>";
            uint thunkRva = originalThunk != 0 ? originalThunk : firstThunk;
            uint thunkOff = RvaToOffset(thunkRva);
            uint iatOff = RvaToOffset(firstThunk);

            var symbols = new List<PeImportSymbol>();
            if (thunkOff != 0)
            {
                for (uint k = 0; ; k++)
                {
                    ulong value = Is64Bit ? ReadU64(thunkOff + k * 8) : ReadU32(thunkOff + k * 4);
                    if (value == 0) break;

                    uint iatRva = firstThunk + k * (uint)thunkSize;
                    if ((value & ordinalFlag) != 0)
                    {
                        symbols.Add(new PeImportSymbol(null, (ushort)(value & 0xFFFF), true, iatRva));
                    }
                    else
                    {
                        uint hintNameOff = RvaToOffset((uint)value);
                        string symbol = hintNameOff != 0 ? ReadCString(hintNameOff + 2) : $"<rva 0x{(uint)value:x}>";
                        symbols.Add(new PeImportSymbol(symbol, 0, false, iatRva));
                    }
                    if (k > 0x10000) break;
                }
            }
            _ = iatOff;
            list.Add(new PeImport(module, symbols.ToArray()));
            if (i > 0x1000) break;
        }
        return list;
    }

    public IReadOnlyList<PeRelocationBlock> ReadRelocations()
    {
        var list = new List<PeRelocationBlock>();
        var (relocRva, relocSize) = Directories[5];
        if (relocRva == 0 || relocSize == 0) return list;

        uint off = RvaToOffset(relocRva);
        if (off == 0) return list;

        uint end = off + relocSize;
        while (off + 8 <= end && off + 8 <= Bytes.Length)
        {
            uint pageRva = ReadU32(off);
            uint blockSize = ReadU32(off + 4);
            if (blockSize < 8 || off + blockSize > Bytes.Length) break;

            int count = (int)((blockSize - 8) / 2);
            var entries = new ushort[count];
            for (int i = 0; i < count; i++) entries[i] = ReadU16(off + 8 + (uint)i * 2);
            list.Add(new PeRelocationBlock(pageRva, entries));
            off += blockSize;
        }
        return list;
    }

    public PeTlsInfo? ReadTls()
    {
        var (tlsRva, tlsSize) = Directories[9];
        if (tlsRva == 0 || tlsSize == 0) return null;

        uint off = RvaToOffset(tlsRva);
        if (off == 0) return null;

        ulong callbacks = Is64Bit ? ReadU64(off + 24) : ReadU32(off + 12);
        uint callbacksRva = callbacks != 0 && callbacks >= ImageBase ? (uint)(callbacks - ImageBase) : 0;
        uint zeroFill = ReadU32(off + (Is64Bit ? 32u : 16u));
        uint characteristics = ReadU32(off + (Is64Bit ? 36u : 20u));
        return new PeTlsInfo(callbacks, callbacksRva, zeroFill, characteristics);
    }

    public static uint ProtectionFor(PeSection section)
    {
        return section.IsExecutable
            ? (section.IsWritable ? NativeApi.PageExecuteReadWrite : section.IsReadable ? NativeApi.PageExecuteRead : NativeApi.PageExecuteReadWrite)
            : (section.IsWritable ? NativeApi.PageReadWrite : NativeApi.PageReadOnly);
    }

    private static string ReadName(byte[] buffer, int offset, int max)
    {
        int end = offset;
        while (end < offset + max && end < buffer.Length && buffer[end] != 0) end++;
        return Encoding.ASCII.GetString(buffer, offset, end - offset);
    }

    private string ReadCString(uint offset)
    {
        if (offset >= Bytes.Length) return "";
        int end = (int)offset;
        while (end < Bytes.Length && Bytes[end] != 0) end++;
        return Encoding.ASCII.GetString(Bytes, (int)offset, end - (int)offset);
    }

    private ushort ReadU16(uint offset) =>
        offset + 2 <= Bytes.Length ? BinaryPrimitives.ReadUInt16LittleEndian(Bytes.AsSpan((int)offset)) : (ushort)0;

    private uint ReadU32(uint offset) =>
        offset + 4 <= Bytes.Length ? BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan((int)offset)) : 0;

    private ulong ReadU64(uint offset) =>
        offset + 8 <= Bytes.Length ? BinaryPrimitives.ReadUInt64LittleEndian(Bytes.AsSpan((int)offset)) : 0;
}
