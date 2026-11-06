using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using WtfNel.Core.Logging;
using SharpCompress.Archives.SevenZip;

namespace WtfNel.Core.Auth.Yggdrasil;

public sealed class ModEntry
{
    [JsonPropertyName("modPath")] public string ModPath { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("iid")] public string Iid { get; set; } = "";
    [JsonPropertyName("md5")] public string Md5 { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
}

public sealed class ModList
{
    [JsonPropertyName("mods")] public List<ModEntry> Mods { get; set; } = new();
}

/// <summary>
/// The modInfo used in join-server authentication, fetched from X19 APIs the way AlaneNEL's
/// InstallerService.InstallGameMods does:
/// 1) core mods: search-by-game for the iid list → user-item-download-v2/get-list for jar_md5;
/// 2) server mods: user-item-download-v2 for the server resource pack → md5 of .minecraft/mods/*.jar inside it.
/// The resource pack is cached in cache/mods by res_md5 and not re-downloaded while unchanged.
/// </summary>
public sealed class ModInfoProvider(string launcherVersion, int mcVersionId, int gameType, string cacheDir)
{
    internal const string GatewayHost = "https://x19apigatewayobt.nie.netease.com";
    internal const string MclHost = "https://x19mclobt.nie.netease.com";

    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly HttpClient _http = CreateHttp(launcherVersion);
    private readonly ConcurrentDictionary<string, Task<string>> _cache = new();

    private static HttpClient CreateHttp(string version)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WPFLauncher/" + version);
        return http;
    }

    /// <summary>The real network server address (X19 /item-address/get, same as FandNEL's GetNetGameServerAddress).</summary>
    public async Task<(string Host, int Port)> GetServerAddressAsync(string gameId, int userId, string userToken, CancellationToken ct = default)
    {
        using var doc = await PostAsync(GatewayHost, "/item-address/get", new { item_id = gameId }, userId.ToString(), userToken, ct);
        var e = doc.RootElement.GetProperty("entity");
        var host = (e.TryGetProperty("ip", out var ip) ? ip.GetString() : e.TryGetProperty("host", out var h) ? h.GetString() : null) ?? "";
        var port = e.TryGetProperty("port", out var p) && p.TryGetInt32(out var n) ? n : 0;
        if (string.IsNullOrWhiteSpace(host)) throw new InvalidDataException("X19 未返回服务器地址: " + e.GetRawText());
        if (e.TryGetProperty("announcement", out var a) && a.GetString() is { Length: > 0 } notice)
            Log.Warn("[服务器公告] " + notice);
        return (host, port > 0 ? port : 25565);
    }

    /// <summary>The account's character (sub-account) names for this server.</summary>
    public async Task<List<string>> QueryRolesAsync(string gameId, int userId, string userToken, CancellationToken ct = default)
    {
        using var doc = await PostAsync(GatewayHost, "/game-character/query/user-game-characters",
            new { offset = 0, length = 20, user_id = userId.ToString(), game_id = gameId, game_type = "2" }, userId.ToString(), userToken, ct);
        return doc.RootElement.TryGetProperty("entities", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(e => e.GetProperty("name").GetString() ?? "").Where(n => n.Length > 0).ToList()
            : new List<string>();
    }

    /// <summary>Creates a character; X19 returns a non-zero code (e.g. name taken) and the reason is in the exception.</summary>
    public async Task CreateRoleAsync(string gameId, int userId, string userToken, string name, CancellationToken ct = default)
    {
        using var _ = await PostAsync(GatewayHost, "/game-character",
            new { game_id = gameId, game_type = 2, user_id = userId.ToString(), name, create_time = 555555, expire_time = 0 },
            userId.ToString(), userToken, ct);
    }

    /// <summary>Computed once per gameId; a failed result is not cached and is retried next time.</summary>
    public Task<string> GetAsync(string gameId, int userId, string userToken, CancellationToken ct = default)
    {
        var task = _cache.GetOrAdd(gameId, _ => BuildAsync(gameId, userId.ToString(), userToken, CancellationToken.None));
        if (task.IsFaulted) { _cache.TryRemove(gameId, out _); task = _cache.GetOrAdd(gameId, _ => BuildAsync(gameId, userId.ToString(), userToken, CancellationToken.None)); }
        return task.WaitAsync(ct);
    }

    private async Task<string> BuildAsync(string gameId, string userId, string userToken, CancellationToken ct)
    {
        var list = new ModList();

        using (var doc = await PostAsync(GatewayHost, "/game-auth-item-list/query/search-by-game",
                   new { mc_version_id = mcVersionId, game_type = gameType }, userId, userToken, ct))
        {
            var iids = doc.RootElement.GetProperty("entity").GetProperty("iid_list").EnumerateArray()
                .Select(e => e.GetUInt64()).ToList();
            using var details = await PostAsync(GatewayHost, "/user-item-download-v2/get-list",
                new { item_id_list = iids }, userId, userToken, ct);
            foreach (var comp in details.RootElement.GetProperty("entities").EnumerateArray())
            {
                var itemId = comp.GetProperty("item_id").GetString()!;
                var mtype = comp.GetProperty("mtypeid").GetInt32();
                foreach (var sub in comp.GetProperty("sub_entities").EnumerateArray())
                {
                    var name = $"{itemId}@{mtype}@0.jar";
                    list.Mods.Add(new ModEntry
                    {
                        ModPath = name, Id = name, Iid = itemId,
                        Md5 = sub.GetProperty("jar_md5").GetString()!.ToUpperInvariant(),
                    });
                }
            }
            Log.Info($"[mods] 核心 mods {list.Mods.Count} 个");
        }

        using (var doc = await PostAsync(MclHost, "/user-item-download-v2",
                   new { item_id = gameId, length = 0, offset = 0 }, userId, userToken, ct))
        {
            var entity = doc.RootElement.TryGetProperty("entity", out var e) ? e : default;
            if (entity.ValueKind == JsonValueKind.Object &&
                entity.TryGetProperty("sub_entities", out var subs) && subs.GetArrayLength() > 0)
            {
                var sub = subs[0];
                var serverMods = await GetServerModsAsync(gameId, sub.GetProperty("res_url").GetString()!,
                    sub.GetProperty("res_md5").GetString()!, ct);
                list.Mods.AddRange(serverMods);
                Log.Info($"[mods] 服务器 mods {serverMods.Count} 个");
            }
            else
            {
                Log.Warn("[mods] 服务器资源包为空，只带核心 mods: " + doc.RootElement.GetRawText());
            }
        }

        return JsonSerializer.Serialize(list);
    }

    private async Task<List<ModEntry>> GetServerModsAsync(string gameId, string resUrl, string resMd5, CancellationToken ct)
    {
        Directory.CreateDirectory(cacheDir);
        var md5File = Path.Combine(cacheDir, gameId + ".MD5");
        var jsonFile = Path.Combine(cacheDir, gameId + ".json");
        if (File.Exists(md5File) && File.Exists(jsonFile) &&
            string.Equals(File.ReadAllText(md5File).Trim(), resMd5, StringComparison.OrdinalIgnoreCase))
        {
            return JsonSerializer.Deserialize<ModList>(File.ReadAllText(jsonFile))!.Mods;
        }

        var archive = Path.Combine(cacheDir, gameId + ".7z");
        Log.Info($"[mods] 下载服务器资源包 {resUrl}");
        await using (var src = await _http.GetStreamAsync(resUrl, ct))
        await using (var dst = File.Create(archive))
            await src.CopyToAsync(dst, ct);

        var mods = new List<ModEntry>();
        using (var sevenZip = SevenZipArchive.Open(archive))
        using (var reader = sevenZip.ExtractAllEntries())
        {
            while (reader.MoveToNextEntry())
            {
                var entry = reader.Entry;
                if (entry.IsDirectory || entry.Key is null) continue;
                var key = entry.Key.Replace('\\', '/');
                if (!key.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                    !key.Contains(".minecraft/mods/", StringComparison.OrdinalIgnoreCase)) continue;
                using var es = reader.OpenEntryStream();
                var md5 = Convert.ToHexString(await MD5.HashDataAsync(es, ct));
                var name = Path.GetFileName(key);
                mods.Add(new ModEntry { ModPath = name, Id = name, Iid = name.Split('@')[0], Md5 = md5 });
            }
        }
        mods.Sort((a, b) => string.CompareOrdinal(a.ModPath, b.ModPath));

        File.WriteAllText(jsonFile, JsonSerializer.Serialize(new ModList { Mods = mods }));
        File.WriteAllText(md5File, resMd5);
        try { File.Delete(archive); } catch { }
        return mods;
    }

    internal async Task<JsonDocument> PostAsync(string host, string path, object body, string userId, string userToken, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body, Json);
        using var req = new HttpRequestMessage(HttpMethod.Post, host + path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        foreach (var kv in HttpToken.Compute(path, json, userId, userToken)) req.Headers.Add(kv.Key, kv.Value);
        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(text);
        var code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
        if (!resp.IsSuccessStatusCode || code != 0)
        {
            var msg = doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            doc.Dispose();
            Log.Debug($"X19 {path} http={(int)resp.StatusCode} body={text}");
            throw new InvalidOperationException($"X19 {path} 失败（code={code}）{(string.IsNullOrEmpty(msg) ? text : msg)}");
        }
        return doc;
    }
}
