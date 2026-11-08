using WtfNel.Core.Logging;

namespace WtfNel.Core;

/// <summary>
/// Resolves the .minecraft root at runtime so nothing is hard-coded to one machine's drive layout.
/// An explicit GameDir in shadow.json always wins; otherwise existing installs are probed across every
/// fixed drive, and if none is found a fresh tree is created on a writable drive (the exe's drive first,
/// then C:). The result is written back into <see cref="ShadowConfig.GameDir"/>.
/// </summary>
public static class GameDirLocator
{
    private static readonly string[] RelativeLayouts =
    {
        @"MCLDownload\Game\.minecraft",
        @".minecraft",
        @"Game\.minecraft",
    };

    public static string Resolve(ShadowConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.GameDir))
        {
            var pinned = Path.GetFullPath(cfg.GameDir);
            Directory.CreateDirectory(pinned);
            cfg.GameDir = pinned;
            Log.Info("[gamedir] 使用配置目录 " + pinned);
            return pinned;
        }

        var candidates = EnumerateCandidates().ToList();
        var withVersion = candidates.FirstOrDefault(d =>
            Directory.Exists(Path.Combine(d, "versions", cfg.VersionId)));
        if (withVersion is not null)
        {
            cfg.GameDir = withVersion;
            Log.Info($"[gamedir] 探测到已有安装（含 {cfg.VersionId}）{withVersion}");
            return withVersion;
        }

        var anyExisting = candidates.FirstOrDefault(d => Directory.Exists(Path.Combine(d, "versions"))
                                                         || Directory.Exists(d));
        if (anyExisting is not null)
        {
            cfg.GameDir = anyExisting;
            Log.Info("[gamedir] 探测到已有目录 " + anyExisting);
            return anyExisting;
        }

        var created = CreateFresh();
        cfg.GameDir = created;
        Log.Info("[gamedir] 未找到已有安装，新建 " + created);
        return created;
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        foreach (var layout in RelativeLayouts)
            yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, layout));

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
            yield return Path.Combine(appData, ".minecraft");

        foreach (var drive in ReadyDrives())
            foreach (var layout in RelativeLayouts)
                yield return Path.Combine(drive.Name, layout);
    }

    private static IEnumerable<DriveInfo> ReadyDrives()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch { yield break; }

        foreach (var d in drives)
        {
            bool ok;
            try { ok = d.IsReady && d.DriveType == DriveType.Fixed; }
            catch { ok = false; }
            if (ok) yield return d;
        }
    }

    private static string CreateFresh()
    {
        var targets = new List<string>();
        var exeRoot = Path.GetPathRoot(AppContext.BaseDirectory);
        if (!string.IsNullOrEmpty(exeRoot)) targets.Add(exeRoot);
        targets.Add(@"C:\");
        foreach (var d in ReadyDrives()) targets.Add(d.Name);

        foreach (var root in targets.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var dir = Path.Combine(root, "MCLDownload", "Game", ".minecraft");
            try
            {
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch (Exception ex)
            {
                Log.Debug($"[gamedir] 不能在 {root} 创建: {ex.Message}");
            }
        }

        var fallback = Path.Combine(AppContext.BaseDirectory, "MCLDownload", "Game", ".minecraft");
        Directory.CreateDirectory(fallback);
        return fallback;
    }
}
