using System.Text.Json;
using WtfNel.Core.Auth.Yggdrasil;
using WtfNel.Core.Logging;
using SharpCompress.Archives;

namespace WtfNel.Core.Install;

/// <summary>
/// Fetches the files the client needs to run (same flow as FandNEL's JavaRuntimeInstaller / MinecraftInstaller):
/// <list type="number">
/// <item>Java: x19.gdl.netease.com/jre-v64-220420.7z → &lt;MCL root&gt;\ext\jre-v64-220420 (jre8 / jdk17)</item>
/// <item>The game itself: /game-patch-info base + version packages → the parent of GameDir; the library package → the version jar/json, forge, modlauncher into .minecraft</item>
/// <item>Core mods: search-by-game → user-item-download-v2/get-list → the jar picked by jar_md5 into mods</item>
/// <item>Server resource pack: user-item-download-v2 → the pack's .minecraft overlaid onto GameDir</item>
/// </list>
/// After each package extracts, a &lt;package&gt;.installed marker (its md5) is written; an unchanged md5 is skipped.
/// </summary>
public sealed class GameInstaller(ShadowConfig cfg, ModInfoProvider x19)
{
    private const string JreUrl = "https://x19.gdl.netease.com/jre-v64-220420.7z";
    private const string JreDirName = "jre-v64-220420";

    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>The parent of .minecraft (the extraction root for the base/version packages, e.g. D:\MCLDownload\Game).</summary>
    private string GameRoot => Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(cfg.GameDir)))!;

    /// <summary>The MCL root (e.g. D:\MCLDownload); Java lives under ext, and the client requires Java to be under MCLDownload.</summary>
    public string MclRoot => Path.GetDirectoryName(GameRoot)!;

    private string Cache => Path.Combine(MclRoot, "cache", "shadow-install");

    public async Task InstallAllAsync(string? gameId, string userToken, CancellationToken ct = default)
    {
        Log.Info($"[install] GameDir={cfg.GameDir}  缓存={Cache}");
        await EnsureJavaAsync(ct);
        await EnsureClientAsync(userToken, ct);
        await EnsureCoreModsAsync(userToken, ct);
        if (!string.IsNullOrEmpty(gameId)) await EnsureServerPackAsync(gameId, userToken, ct);
        else Log.Warn("[install] 没有 gameId，跳过服务器资源包");
        EnsureLocalOnlyFiles();
        Log.Info("[install] 完成");
    }

    /// <summary>
    /// Files the CDN does not serve: the whole natives\runtime directory and &lt;version&gt;.dat (only the official
    /// launcher places them). Sources, in priority order: ① the bundled LocalRuntime\&lt;version&gt;\ shipped next to
    /// the exe; ② the existing .minecraft pointed to by LocalFallbackDir.
    /// </summary>
    private void EnsureLocalOnlyFiles()
    {
        var runtimeRel = Path.Combine("versions", cfg.VersionId, "natives", "runtime");
        var datRel = Path.Combine("versions", cfg.VersionId, cfg.VersionId + ".dat");
        var needRuntime = !File.Exists(Path.Combine(cfg.GameDir, runtimeRel, "libenvsdk.dll"));
        var needDat = !File.Exists(Path.Combine(cfg.GameDir, datRel));
        if (!needRuntime && !needDat) return;

        var bundled = Path.Combine(AppContext.BaseDirectory, "LocalRuntime", cfg.VersionId);
        (needRuntime, needDat) = TryFill("内置 LocalRuntime",
            Path.Combine(bundled, "natives", "runtime"), Path.Combine(bundled, cfg.VersionId + ".dat"),
            needRuntime, needDat);
        if (!needRuntime && !needDat) return;

        var src = cfg.LocalFallbackDir;
        if (!string.IsNullOrWhiteSpace(src) && Directory.Exists(src))
            (needRuntime, needDat) = TryFill(src!,
                Path.Combine(src!, runtimeRel), Path.Combine(src!, datRel),
                needRuntime, needDat);

        if (needRuntime || needDat)
            Log.Warn($"[install] 仍缺 {(needRuntime ? runtimeRel + " " : "")}{(needDat ? datRel : "")}" +
                     "（内置 LocalRuntime 里没有此版本，也没配 LocalFallbackDir）");
    }

    /// <summary>Copies natives\runtime / &lt;version&gt;.dat from one source into GameDir; returns what is still missing.</summary>
    private (bool needRuntime, bool needDat) TryFill(string label, string runtimeSrc, string datSrc, bool needRuntime, bool needDat)
    {
        if (needRuntime && Directory.Exists(runtimeSrc))
        {
            var dst = Path.Combine(cfg.GameDir, "versions", cfg.VersionId, "natives", "runtime");
            foreach (var f in Directory.EnumerateFiles(runtimeSrc, "*", SearchOption.AllDirectories))
                CopyFile(f, Path.Combine(dst, Path.GetRelativePath(runtimeSrc, f)));
            Log.Info($"[install] 从 {label} 拷贝 natives\\runtime");
            needRuntime = false;
        }
        if (needDat && File.Exists(datSrc))
        {
            CopyFile(datSrc, Path.Combine(cfg.GameDir, "versions", cfg.VersionId, cfg.VersionId + ".dat"));
            Log.Info($"[install] 从 {label} 拷贝 {cfg.VersionId}.dat");
            needDat = false;
        }
        return (needRuntime, needDat);
    }

    /// <summary>The NetEase Java javaw.exe (jre8 for &lt;1.16, otherwise jdk17).</summary>
    public string JavawPath => Path.Combine(MclRoot, "ext", JreDirName, cfg.McVersionId >= 1016000 ? "jdk17" : "jre8", "bin", "javaw.exe");

    /// <summary>
    /// The Java used to launch: JavaExe if it is set, exists and lives under the MCL root (the client requires Java
    /// under MCLDownload); otherwise the NetEase Java.
    /// </summary>
    public string LaunchJava =>
        !string.IsNullOrWhiteSpace(cfg.JavaExe) && File.Exists(cfg.JavaExe) &&
        Path.GetFullPath(cfg.JavaExe).StartsWith(Path.GetFullPath(MclRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(cfg.JavaExe)
            : JavawPath;

    /// <summary>Selects Java by McVersionId (jre8 for &lt;1.16, otherwise jdk17) and returns javaw.exe.</summary>
    public async Task<string> EnsureJavaAsync(CancellationToken ct = default)
    {
        if (cfg.McVersionId >= 1020006)
            throw new NotSupportedException("1.20.6+ 需要 Java 21，网易运行时包里没有；请在 JavaExe 指定");
        var dir = Path.Combine(MclRoot, "ext", JreDirName);
        var javaw = JavawPath;
        if (File.Exists(javaw)) { Log.Info("[install] Java 已存在: " + javaw); return javaw; }

        var archive = Path.Combine(Cache, "jre-v64-220420.7z");
        await FastDownloader.DownloadAsync(_http, JreUrl, archive, null, ct);
        Extract(archive, dir);
        TryDelete(archive);
        if (!File.Exists(javaw)) throw new FileNotFoundException("运行时包里没有对应的 Java", javaw);
        Log.Info("[install] Java 就绪: " + javaw);
        return javaw;
    }

    public async Task EnsureClientAsync(string userToken, CancellationToken ct = default)
    {
        var basePkg = await PatchInfoAsync(0, userToken, ct);
        var verPkg = await PatchInfoAsync(cfg.McVersionId, userToken, ct);

        await InstallArchiveAsync("base", Str(basePkg, "url"), Str(basePkg, "md5"), GameRoot, ct);
        await InstallArchiveAsync("ver-" + cfg.McVersionId, Str(verPkg, "url"), Str(verPkg, "md5"), GameRoot, ct);

        var libDir = Path.Combine(Cache, "corelib-" + cfg.McVersionId);
        if (await InstallArchiveAsync("corelib-" + cfg.McVersionId, Str(verPkg, "core_lib_url"), Str(verPkg, "core_lib_md5"), libDir, ct))
            InstallCoreLibraries(libDir);
    }

    private async Task<JsonElement> PatchInfoAsync(int mcVersion, string userToken, CancellationToken ct)
    {
        using var doc = await x19.PostAsync(ModInfoProvider.MclHost, "/game-patch-info",
            new { mc_version = mcVersion }, cfg.Uid.ToString(), userToken, ct);
        return doc.RootElement.GetProperty("entity").Clone();
    }

    /// <summary>Only a few files from the library package go into .minecraft; the rest (per-version _libs) are picked by name.</summary>
    private void InstallCoreLibraries(string sourceDir)
    {
        var version = cfg.VersionId;
        var count = 0;
        foreach (var src in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(src);
            string? target = null;
            if (name == version + ".jar" || name == version + ".json")
                target = Path.Combine(cfg.VersionDir, name);
            else if (name.StartsWith("forge-" + version + "-", StringComparison.Ordinal))
                target = Library("net/minecraftforge/forge", "forge-");
            else if (name.StartsWith("launchwrapper-", StringComparison.Ordinal))
                target = Library("net/minecraft/launchwrapper", "launchwrapper-");
            else if (name.StartsWith("MercuriusUpdater-", StringComparison.Ordinal))
                target = Library("net/minecraftforge/MercuriusUpdater", "MercuriusUpdater-");
            else if (name.StartsWith("modlauncher-", StringComparison.Ordinal))
                target = Library(name.StartsWith("modlauncher-10.2.", StringComparison.Ordinal) ? "net/minecraftforge/modlauncher" : "cpw/mods/modlauncher", "modlauncher-");
            if (target is null) continue;
            CopyFile(src, target);
            count++;

            string Library(string group, string prefix) =>
                Path.Combine(cfg.LibraryDir, group, Path.GetFileNameWithoutExtension(name)[prefix.Length..], name);
        }
        Log.Info($"[install] 库包落地 {count} 个文件");
    }

    public async Task EnsureCoreModsAsync(string userToken, CancellationToken ct = default)
    {
        var uid = cfg.Uid.ToString();
        List<ulong> iids;
        using (var doc = await x19.PostAsync(ModInfoProvider.GatewayHost, "/game-auth-item-list/query/search-by-game",
                   new { mc_version_id = cfg.McVersionId, game_type = cfg.GameType }, uid, userToken, ct))
            iids = doc.RootElement.GetProperty("entity").GetProperty("iid_list").EnumerateArray().Select(e => e.GetUInt64()).ToList();

        using var details = await x19.PostAsync(ModInfoProvider.GatewayHost, "/user-item-download-v2/get-list",
            new { item_id_list = iids }, uid, userToken, ct);
        var modsDir = Path.Combine(cfg.GameDir, "mods");
        int fresh = 0, total = 0;
        foreach (var comp in details.RootElement.GetProperty("entities").EnumerateArray())
        {
            var subs = comp.GetProperty("sub_entities");
            if (subs.GetArrayLength() == 0) throw new InvalidDataException("核心 mod 缺少下载资源: " + comp.GetRawText());
            var res = subs[0];
            var jarMd5 = Str(res, "jar_md5");
            var jarName = $"{Path.GetFileNameWithoutExtension(Str(res, "res_name"))}@{comp.GetProperty("mtypeid").GetInt32()}@{Str(comp, "entity_id")}.jar";
            var jarPath = Path.Combine(modsDir, jarName);
            total++;
            if (File.Exists(jarPath) && await FastDownloader.Md5MatchesAsync(jarPath, jarMd5, ct)) continue;

            var key = Str(res, "res_md5");
            var archive = Path.Combine(Cache, "mods", key + ".archive");
            var extracted = Path.Combine(Cache, "mods", key);
            await FastDownloader.DownloadAsync(_http, Str(res, "res_url"), archive, key, ct);
            Extract(archive, extracted);

            string? source = null;
            foreach (var jar in Directory.EnumerateFiles(extracted, "*.jar", SearchOption.AllDirectories))
                if (await FastDownloader.Md5MatchesAsync(jar, jarMd5, ct)) { source = jar; break; }
            if (source is null) throw new InvalidDataException($"核心 mod 包里没有 md5={jarMd5} 的 jar: {Str(res, "res_name")}");
            CopyFile(source, jarPath);
            TryDelete(archive);
            TryDeleteDir(extracted);
            fresh++;
            Log.Info($"[install] 核心 mod {jarName}");
        }
        Log.Info($"[install] 核心 mods {total} 个（本次更新 {fresh}）");
    }

    public async Task EnsureServerPackAsync(string gameId, string userToken, CancellationToken ct = default)
    {
        using var doc = await x19.PostAsync(ModInfoProvider.MclHost, "/user-item-download-v2",
            new { item_id = gameId, length = 0, offset = 0 }, cfg.Uid.ToString(), userToken, ct);
        if (!doc.RootElement.TryGetProperty("entity", out var e) || e.ValueKind != JsonValueKind.Object ||
            !e.TryGetProperty("sub_entities", out var subs) || subs.GetArrayLength() == 0)
        {
            Log.Warn("[install] 服务器资源包为空");
            return;
        }
        var res = subs[0];
        var dir = Path.Combine(Cache, "server-" + gameId);
        if (!await InstallArchiveAsync("server-" + gameId, Str(res, "res_url"), Str(res, "res_md5"), dir, ct)
            && File.Exists(Path.Combine(dir, ".synced"))) return;

        var mc = Path.Combine(dir, ".minecraft");
        if (!Directory.Exists(mc)) { Log.Warn("[install] 服务器资源包里没有 .minecraft 目录"); return; }
        var n = 0;
        foreach (var f in Directory.EnumerateFiles(mc, "*", SearchOption.AllDirectories))
        {
            CopyFile(f, Path.Combine(cfg.GameDir, Path.GetRelativePath(mc, f)));
            n++;
        }
        File.WriteAllText(Path.Combine(dir, ".synced"), "");
        Log.Info($"[install] 服务器资源包同步 {n} 个文件到 GameDir");
    }

    /// <summary>Downloads and extracts; skips when the md5 marker matches. Returns whether it actually installed.</summary>
    private async Task<bool> InstallArchiveAsync(string key, string url, string md5, string destination, CancellationToken ct)
    {
        var marker = Path.Combine(Cache, key + ".installed");
        if (!string.IsNullOrEmpty(md5) && File.Exists(marker) && File.ReadAllText(marker).Trim().Equals(md5, StringComparison.OrdinalIgnoreCase))
        {
            Log.Info($"[install] {key} 已是最新（md5={md5}）");
            return false;
        }
        var archive = Path.Combine(Cache, key + ".7z");
        await FastDownloader.DownloadAsync(_http, url, archive, md5, ct);
        if (File.Exists(Path.Combine(destination, ".synced"))) File.Delete(Path.Combine(destination, ".synced"));
        Extract(archive, destination);
        Directory.CreateDirectory(Cache);
        File.WriteAllText(marker, md5);
        TryDelete(archive);
        return true;
    }

    /// <summary>Sequential SharpCompress extraction (a solid 7z is read only once), rejecting entries that escape the target directory.</summary>
    private static void Extract(string archivePath, string destination)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        Log.Info($"[install] 解压 {Path.GetFileName(archivePath)} -> {root}");
        var count = 0;
        using var archive = ArchiveFactory.Open(archivePath);
        using var reader = archive.ExtractAllEntries();
        while (reader.MoveToNextEntry())
        {
            var entry = reader.Entry;
            if (entry.IsDirectory || string.IsNullOrEmpty(entry.Key)) continue;
            var target = Path.GetFullPath(Path.Combine(root, entry.Key.Replace('\\', '/').TrimStart('/')));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("压缩包条目越界: " + entry.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
            using (var output = File.Create(target)) reader.WriteEntryTo(output);
            count++;
        }
        Log.Info($"[install] 解压完成 {count} 个文件");
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText()) : "";

    private static void CopyFile(string src, string dst)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        if (File.Exists(dst)) File.SetAttributes(dst, FileAttributes.Normal);
        File.Copy(src, dst, overwrite: true);
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } }
    private static void TryDeleteDir(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (IOException) { } }
}
