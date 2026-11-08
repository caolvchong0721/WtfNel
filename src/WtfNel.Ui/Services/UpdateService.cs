using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace WtfNel.Ui.Services;

/// <summary>A published GitHub release newer than the running build.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string Name, string Notes, string PageUrl, string? AssetUrl, string? AssetName, long AssetSize, DateTime PublishedAt);

/// <summary>Checks the GitHub Releases API of the project repository for a newer version.</summary>
public static class UpdateService
{
    public const string Owner = "caolvchong0721";
    public const string Repo = "WtfNel";

    public static string RepoUrl => $"https://github.com/{Owner}/{Repo}";
    public static string ReleasesUrl => RepoUrl + "/releases";

    public static Version Current { get; } = Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0));

    public static string CurrentText => $"{Current.Major}.{Current.Minor}.{Math.Max(Current.Build, 0)}";

    /// <summary>Returns the latest release when it is newer than <see cref="Current"/>, otherwise null.</summary>
    public static async Task<ReleaseInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"{Repo}-Updater/{CurrentText}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await http.GetAsync($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryParse(tag, out var version) || version <= Current) return null;

        string? assetUrl = null, assetName = null;
        long assetSize = 0;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (!new[] { ".7z", ".zip", ".exe" }.Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))) continue;
                assetName = name;
                assetUrl = asset.GetProperty("browser_download_url").GetString();
                assetSize = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                break;
            }
        }

        return new ReleaseInfo(
            version,
            tag,
            root.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } title ? title : tag,
            root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
            root.GetProperty("html_url").GetString() ?? ReleasesUrl,
            assetUrl,
            assetName,
            assetSize,
            root.TryGetProperty("published_at", out var p) && p.TryGetDateTime(out var at) ? at.ToLocalTime() : DateTime.Now);
    }

    public static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    private static bool TryParse(string tag, out Version version)
    {
        var text = tag.Trim().TrimStart('v', 'V');
        var cut = text.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) text = text[..cut];
        if (Version.TryParse(text.Contains('.') ? text : text + ".0", out var parsed))
        {
            version = Normalize(parsed);
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }

    private static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
}
