using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using WtfNel.Core.Logging;

namespace WtfNel.Core;

/// <summary>
/// Makes the placeholder launcher pass the native launcher check of the NetEase client: the version
/// resource of WPFLauncher.exe, the MCLauncher registry key and the filter path are all aligned with
/// the newest official launcher build published in the x19 patch list.
/// </summary>
public static class LauncherIdentity
{
    public const string PatchListUrl = "https://x19.update.netease.com/pl/x19_java_patchlist";

    private const string FallbackVersion = "1.15.34.21425";

    private static readonly Regex PatchEntry =
        new(@"(?m)^\s*""(\d+\.\d+\.\d+\.\d+)""\s*:", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Ensure(ShadowConfig cfg, string mclRoot)
    {
        string version = ResolveVersion(cfg);
        cfg.LauncherFileVersion = version;

        string stub = ResolveStub(cfg);
        PrepareStub(stub, version);
        cfg.FilterPath = EnsureFilterPath();

        if (cfg.WriteLauncherRegistry)
            EnsureRegistry(mclRoot, version, Path.GetDirectoryName(stub)!);

        Log.Info($"[launcher-id] 版本 {version}  替身 {FileVersionOf(stub) ?? "无版本资源"}  路径 {stub}");
        return version;
    }

    public static string ResolveVersion(ShadowConfig cfg)
    {
        string? fetched = FetchLatestVersion();
        if (!string.IsNullOrWhiteSpace(fetched))
        {
            if (!string.Equals(fetched, cfg.LauncherFileVersion, StringComparison.Ordinal))
                Log.Info($"[launcher-id] 补丁表最新版本 {fetched}");
            return fetched!;
        }

        if (!string.IsNullOrWhiteSpace(cfg.LauncherFileVersion))
        {
            Log.Warn("[launcher-id] 拉取补丁表失败，沿用缓存版本 " + cfg.LauncherFileVersion);
            return cfg.LauncherFileVersion.Trim();
        }

        Log.Warn("[launcher-id] 拉取补丁表失败且没有缓存，使用内置版本 " + FallbackVersion);
        return FallbackVersion;
    }

    public static string? FetchLatestVersion()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var req = new HttpRequestMessage(HttpMethod.Get, PatchListUrl);
            req.Headers.UserAgent.ParseAdd("WPFLauncher/1.0");
            using var resp = http.Send(req, HttpCompletionOption.ResponseContentRead);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn($"[launcher-id] 补丁表 HTTP {(int)resp.StatusCode}");
                return null;
            }

            using var stream = resp.Content.ReadAsStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string text = reader.ReadToEnd();
            MatchCollection matches = PatchEntry.Matches(text);
            if (matches.Count == 0) return null;
            return matches[^1].Groups[1].Value;
        }
        catch (Exception ex)
        {
            Log.Debug("[launcher-id] 补丁表请求异常：" + ex.GetBaseException().Message);
            return null;
        }
    }

    public static string ResolveStub(ShadowConfig cfg)
    {
        string full = Path.GetFullPath(cfg.LauncherPath, AppContext.BaseDirectory);
        if (File.Exists(full)) return full;
        string side = Path.Combine(AppContext.BaseDirectory, "WPFLauncher.exe");
        return File.Exists(side) ? side : full;
    }

    private static void PrepareStub(string stub, string version)
    {
        if (!File.Exists(stub))
        {
            Log.Warn("[launcher-id] 替身不存在：" + stub);
            return;
        }

        if (IsAligned(stub, version)) return;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                PatchBinary(stub, version);
                Log.Info($"[launcher-id] 已写入版本资源 {version}");
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 0)
                {
                    Log.Info("[launcher-id] 替身被占用，结束同名进程后重试");
                    KillByImagePath(stub);
                    Thread.Sleep(200);
                    continue;
                }

                Log.Warn("[launcher-id] 写版本资源失败：" + ex.GetBaseException().Message);
                return;
            }
            catch (Exception ex)
            {
                Log.Warn("[launcher-id] 写版本资源失败：" + ex.GetBaseException().Message);
                return;
            }
        }
    }

    private static bool IsAligned(string stub, string version)
    {
        try
        {
            FileVersionInfo vi = FileVersionInfo.GetVersionInfo(stub);
            return string.Equals(vi.CompanyName, "NetEase", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(vi.ProductName, "我的世界启动器", StringComparison.Ordinal)
                   && string.Equals(vi.OriginalFilename, "WPFLauncher.exe", StringComparison.OrdinalIgnoreCase)
                   && SameVersion(vi.FileVersion, version);
        }
        catch
        {
            return false;
        }
    }

    private static bool SameVersion(string? left, string right)
    {
        if (!TryParse(left, out _, out _, out _, out _)) return false;
        if (!TryParse(right, out _, out _, out _, out _)) return false;
        return string.Equals(Normalize(left!), Normalize(right), StringComparison.Ordinal);
    }

    private static string Normalize(string v)
    {
        TryParse(v, out ushort a, out ushort b, out ushort c, out ushort d);
        return $"{a}.{b}.{c}.{d}";
    }

    private static bool TryParse(string? text, out ushort a, out ushort b, out ushort c, out ushort d)
    {
        a = b = c = d = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string[] parts = text!.Trim().Split('.');
        if (parts.Length < 2) return false;
        if (!ushort.TryParse(parts[0], out a)) return false;
        if (!ushort.TryParse(parts[1], out b)) return false;
        if (parts.Length > 2 && !ushort.TryParse(parts[2], out c)) return false;
        if (parts.Length > 3 && !ushort.TryParse(parts[3], out d)) return false;
        return true;
    }

    public static void PatchBinary(string path, string version)
    {
        byte[] blob = BuildBlob(version, true);
        byte[] pe = File.ReadAllBytes(path);
        (int dataOffset, int slotSize, int sizeFieldOffset) = LocateVersion(pe);

        if (blob.Length > slotSize) blob = BuildBlob(version, false);
        if (blob.Length > slotSize)
            throw new InvalidOperationException($"版本资源槽不足：需要 {blob.Length} 字节，可用 {slotSize} 字节");

        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        fs.Position = dataOffset;
        fs.Write(blob, 0, blob.Length);
        int padding = slotSize - blob.Length;
        if (padding > 0) fs.Write(new byte[padding], 0, padding);
        fs.Position = sizeFieldOffset;
        fs.Write(BitConverter.GetBytes((uint)blob.Length), 0, 4);
        fs.Flush(true);
    }

    private static string? FileVersionOf(string path)
    {
        try
        {
            FileVersionInfo vi = FileVersionInfo.GetVersionInfo(path);
            return $"{vi.CompanyName}/{vi.ProductName}/{vi.FileVersion}";
        }
        catch
        {
            return null;
        }
    }

    private static void KillByImagePath(string path)
    {
        string full = Path.GetFullPath(path);
        foreach (Process p in Process.GetProcesses())
        {
            try
            {
                if (p.HasExited) continue;
                string? image = p.MainModule?.FileName;
                if (image is not null && string.Equals(image, full, StringComparison.OrdinalIgnoreCase))
                    p.Kill();
            }
            catch
            {
            }
            finally
            {
                p.Dispose();
            }
        }
    }

    private static string EnsureFilterPath()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Netease", "MCLauncher", "config", "fltmp");
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            Log.Debug("[launcher-id] 创建 filter 目录失败：" + ex.GetBaseException().Message);
        }

        return dir;
    }

    private static void EnsureRegistry(string mclRoot, string version, string stubDir)
    {
        if (!OperatingSystem.IsWindows()) return;

        const string sub = @"Software\Netease\MCLauncher";
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(sub, true);
            string? install = key.GetValue("InstallLocation") as string;
            if (InstalledLauncherOk(install))
            {
                Log.Info("[launcher-id] 系统里已有官方启动器，注册表保持原样：" + install);
                return;
            }

            key.SetValue("InstallLocation", stubDir, RegistryValueKind.String);
            key.SetValue("DisplayVersion", version, RegistryValueKind.String);
            key.SetValue("DownloadPath", mclRoot, RegistryValueKind.String);
            key.SetValue("MinecraftBENeteasePath", Path.Combine(mclRoot, "MinecraftBENetease"), RegistryValueKind.String);
            Log.Info($"[launcher-id] 注册表 InstallLocation -> {stubDir}  DisplayVersion -> {version}");
        }
        catch (Exception ex)
        {
            Log.Warn("[launcher-id] 写注册表失败：" + ex.GetBaseException().Message);
        }
    }

    private static bool InstalledLauncherOk(string? installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation)) return false;
        string exe = Path.Combine(installLocation!, "WPFLauncher.exe");
        if (!File.Exists(exe)) return false;

        try
        {
            FileVersionInfo vi = FileVersionInfo.GetVersionInfo(exe);
            return string.Equals(vi.CompanyName, "NetEase", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(vi.ProductName, "我的世界启动器", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] BuildBlob(string version, bool withExtras)
    {
        TryParse(version, out ushort v1, out ushort v2, out ushort v3, out ushort v4);

        var pairs = new List<KeyValuePair<string, string>>
        {
            new("CompanyName", "NetEase"),
            new("FileDescription", "我的世界启动器"),
            new("FileVersion", version),
            new("OriginalFilename", "WPFLauncher.exe"),
            new("ProductName", "我的世界启动器"),
            new("ProductVersion", version),
        };

        if (withExtras)
        {
            pairs.Insert(3, new KeyValuePair<string, string>("InternalName", "WPFLauncher.exe"));
            pairs.Insert(4, new KeyValuePair<string, string>("LegalCopyright", "Copyright © NetEase"));
        }

        var table = new List<byte>();
        foreach (KeyValuePair<string, string> kv in pairs)
        {
            byte[] value = Encoding.Unicode.GetBytes(kv.Value + "\0");
            table.AddRange(Block(kv.Key, (ushort)(kv.Value.Length + 1), 1, value, Array.Empty<byte>()));
        }

        byte[] stringTable = Block("000004b0", 0, 1, Array.Empty<byte>(), table.ToArray());
        byte[] stringFileInfo = Block("StringFileInfo", 0, 1, Array.Empty<byte>(), stringTable);
        byte[] translation = Block("Translation", 4, 0, BitConverter.GetBytes(0x04B00000u), Array.Empty<byte>());
        byte[] varFileInfo = Block("VarFileInfo", 0, 1, Array.Empty<byte>(), translation);

        byte[] children = new byte[stringFileInfo.Length + varFileInfo.Length];
        Buffer.BlockCopy(stringFileInfo, 0, children, 0, stringFileInfo.Length);
        Buffer.BlockCopy(varFileInfo, 0, children, stringFileInfo.Length, varFileInfo.Length);

        return Block("VS_VERSION_INFO", 52, 0, FixedInfo(v1, v2, v3, v4), children);
    }

    private static byte[] FixedInfo(ushort v1, ushort v2, ushort v3, ushort v4)
    {
        var list = new List<byte>(52);
        void Add(uint value) => list.AddRange(BitConverter.GetBytes(value));

        Add(0xFEEF04BD);
        Add(0x00010000);
        Add(((uint)v1 << 16) | v2);
        Add(((uint)v3 << 16) | v4);
        Add(((uint)v1 << 16) | v2);
        Add(((uint)v3 << 16) | v4);
        Add(0x3F);
        Add(0);
        Add(0x40004);
        Add(1);
        Add(0);
        Add(0);
        Add(0);
        return list.ToArray();
    }

    private static byte[] Block(string key, ushort valueLength, ushort type, byte[] value, byte[] children)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[6], 0, 6);

        byte[] keyBytes = Encoding.Unicode.GetBytes(key + "\0");
        ms.Write(keyBytes, 0, keyBytes.Length);
        Align(ms);

        if (value.Length > 0) ms.Write(value, 0, value.Length);
        Align(ms);

        if (children.Length > 0) ms.Write(children, 0, children.Length);

        byte[] arr = ms.ToArray();
        if (arr.Length > ushort.MaxValue) throw new InvalidOperationException("版本资源块过大");
        arr[0] = (byte)(arr.Length & 0xFF);
        arr[1] = (byte)(arr.Length >> 8);
        arr[2] = (byte)(valueLength & 0xFF);
        arr[3] = (byte)(valueLength >> 8);
        arr[4] = (byte)(type & 0xFF);
        arr[5] = (byte)(type >> 8);
        return arr;
    }

    private static void Align(Stream s)
    {
        while (s.Position % 4 != 0) s.WriteByte(0);
    }

    private static (int dataOffset, int slotSize, int sizeFieldOffset) LocateVersion(byte[] pe)
    {
        int lfanew = BitConverter.ToInt32(pe, 0x3C);
        if (lfanew <= 0 || lfanew + 24 > pe.Length) throw new InvalidDataException("PE 头无效");
        if (BitConverter.ToUInt32(pe, lfanew) != 0x00004550) throw new InvalidDataException("PE 签名无效");

        int coff = lfanew + 4;
        int sectionCount = BitConverter.ToUInt16(pe, coff + 2);
        int optionalSize = BitConverter.ToUInt16(pe, coff + 16);
        int optional = coff + 20;
        ushort magic = BitConverter.ToUInt16(pe, optional);
        int dirOffset = magic == 0x20B ? optional + 112 : optional + 96;
        if (dirOffset + 16 > pe.Length) throw new InvalidDataException("数据目录越界");

        uint resourceRva = BitConverter.ToUInt32(pe, dirOffset + 16);
        if (resourceRva == 0) throw new InvalidDataException("没有资源节");

        int sectionTable = optional + optionalSize;
        int resourceBase = RvaToOffset(pe, sectionTable, sectionCount, resourceRva);

        int versionDir = FindEntry(pe, resourceBase, resourceBase, 16, true);
        if (versionDir < 0) throw new InvalidDataException("没有 RT_VERSION");

        int nameDir = FindEntry(pe, versionDir, resourceBase, 1, true);
        if (nameDir < 0) throw new InvalidDataException("没有 VS_VERSION_INFO 资源");

        int leafOffset = FirstLeaf(pe, nameDir);
        if (leafOffset < 0) throw new InvalidDataException("版本资源没有语言项");

        int dataEntry = RvaToOffset(pe, sectionTable, sectionCount, resourceRva + (uint)leafOffset);
        if (dataEntry + 16 > pe.Length) throw new InvalidDataException("资源数据项越界");

        uint dataRva = BitConverter.ToUInt32(pe, dataEntry);
        uint dataSize = BitConverter.ToUInt32(pe, dataEntry + 4);
        int dataOffset = RvaToOffset(pe, sectionTable, sectionCount, dataRva);
        if (dataSize == 0 || dataSize > int.MaxValue) throw new InvalidDataException("版本资源长度异常");
        if (dataOffset + dataSize > pe.Length) throw new InvalidDataException("版本资源越界");

        return (dataOffset, (int)dataSize, dataEntry + 4);
    }

    private static int FindEntry(byte[] pe, int dirOffset, int resourceBase, int wanted, bool directory)
    {
        int count = BitConverter.ToUInt16(pe, dirOffset + 12) + BitConverter.ToUInt16(pe, dirOffset + 14);
        for (int i = 0; i < count; i++)
        {
            int entry = dirOffset + 16 + i * 8;
            if (entry + 8 > pe.Length) return -1;
            uint name = BitConverter.ToUInt32(pe, entry);
            if (name != (uint)wanted) continue;
            uint target = BitConverter.ToUInt32(pe, entry + 4);
            bool isDir = (target & 0x80000000) != 0;
            if (isDir != directory) return -1;
            return resourceBase + (int)(target & 0x7FFFFFFF);
        }

        return -1;
    }

    private static int FirstLeaf(byte[] pe, int dirOffset)
    {
        int count = BitConverter.ToUInt16(pe, dirOffset + 12) + BitConverter.ToUInt16(pe, dirOffset + 14);
        if (count == 0) return -1;
        int entry = dirOffset + 16;
        if (entry + 8 > pe.Length) return -1;
        return (int)(BitConverter.ToUInt32(pe, entry + 4) & 0x7FFFFFFF);
    }

    private static int RvaToOffset(byte[] pe, int sectionTable, int sectionCount, uint rva)
    {
        for (int i = 0; i < sectionCount; i++)
        {
            int s = sectionTable + i * 40;
            if (s + 40 > pe.Length) break;
            uint virtualSize = BitConverter.ToUInt32(pe, s + 8);
            uint virtualAddress = BitConverter.ToUInt32(pe, s + 12);
            uint rawSize = BitConverter.ToUInt32(pe, s + 16);
            uint rawPointer = BitConverter.ToUInt32(pe, s + 20);
            uint span = Math.Max(virtualSize, rawSize);
            if (rva < virtualAddress || rva >= virtualAddress + span) continue;
            uint delta = rva - virtualAddress;
            if (delta >= rawSize) break;
            return (int)(rawPointer + delta);
        }

        throw new InvalidDataException($"RVA 0x{rva:x} 无法映射到文件偏移");
    }
}
