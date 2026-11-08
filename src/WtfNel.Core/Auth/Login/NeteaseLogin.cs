using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using WtfNel.Core.Auth.Yggdrasil;
using WtfNel.Core.Logging;

namespace WtfNel.Core.Auth.Login;

/// <summary>Login result: userId (entity_id) + the 16-char userToken, fed directly to Yggdrasil and the X19 APIs.</summary>
public sealed record NeteaseSession(int UserId, string UserToken, string Account, string Channel);

/// <summary>
/// Live login, ported from FandNEL (WPFLauncher.LoginWithCookieAsync / MPay.LoginWithEmailAsync):
///   NetEase email: MPay device registration → email + md5(password) login → build sauth_json
///   cookie: use an existing sauth_json directly (non-NetEase channels first go through mgbsdk uni_sauth)
///   → x19 /login-otp → /authentication-otp (encrypted body) → interconn login-start
/// </summary>
public sealed class NeteaseLogin : IDisposable
{
    private const string MPayGameId = "aecfrxodyqaaaajp-g-x19";
    private const string CoreHost = "https://x19obtcore.nie.netease.com:8443";

    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly string _launcherVersion;
    private readonly string _stateDir;
    private readonly HttpClient _http;

    public NeteaseLogin(string launcherVersion, string stateDir)
    {
        _launcherVersion = launcherVersion;
        _stateDir = stateDir;
        Directory.CreateDirectory(stateDir);
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WPFLauncher/" + launcherVersion);
    }

    public void Dispose() => _http.Dispose();

    public async Task<NeteaseSession> LoginWithEmailAsync(string email, string password, CancellationToken ct = default)
    {
        var (unique, device) = await LoadOrCreateDeviceAsync(ct);
        var user = await MPayLoginAsync(email, password, unique, device, ct);

        var sauth = new JsonObject
        {
            ["gameid"] = "x19",
            ["login_channel"] = "netease",
            ["app_channel"] = "netease",
            ["platform"] = "pc",
            ["sdkuid"] = user.GetProperty("id").GetString(),
            ["sessionid"] = user.GetProperty("token").GetString(),
            ["sdk_version"] = "4.2.0",
            ["udid"] = Guid.NewGuid().ToString("N").ToUpperInvariant(),
            ["deviceid"] = device.Id,
            ["aim_info"] = await AimInfoAsync(ct),
        }.ToJsonString(Json);
        return await LoginWithCookieAsync(sauth, ct);
    }

    /// <summary>4399 PC channel: ptlogin username + password → 4399 SDK credentials → sauth_json → cookie login.</summary>
    public async Task<NeteaseSession> LoginWith4399Async(string username, string password, ICaptchaPrompt? captcha = null, CancellationToken ct = default)
    {
        Pc4399Credentials cred;
        using (var pc = new Pc4399Login(captcha)) cred = await pc.LoginAsync(username, password, ct);

        var deviceId = LoadOrCreateUnique();
        var sauth = new JsonObject
        {
            ["timestamp"] = cred.Time,
            ["userid"] = cred.Username,
            ["realname"] = "{\"realname_type\":\"0\"}",
            ["gameid"] = "x19",
            ["login_channel"] = "4399pc",
            ["app_channel"] = "4399pc",
            ["platform"] = "pc",
            ["sdkuid"] = cred.Uid,
            ["sessionid"] = cred.Token,
            ["sdk_version"] = "1.0.0",
            ["udid"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(),
            ["deviceid"] = deviceId,
            ["aim_info"] = await AimInfoAsync(ct),
            ["client_login_sn"] = deviceId,
            ["gas_token"] = "",
            ["source_platform"] = "pc",
            ["ip"] = await GatewayIpAsync(ct),
        }.ToJsonString(Json);
        return await LoginWithCookieAsync(sauth, ct);
    }

    /// <summary>4399com channel: 4399 mobile SDK OAuth (captchas go to <paramref name="captcha"/>) → sauth_json → cookie login.</summary>
    public async Task<NeteaseSession> LoginWith4399ComAsync(string username, string password, ICaptchaPrompt? captcha = null, CancellationToken ct = default)
    {
        Com4399Credentials cred;
        using (var com = new Com4399Login(_stateDir, captcha)) cred = await com.LoginAsync(username, password, ct);

        var deviceId = Guid.NewGuid().ToString("N").ToUpperInvariant();
        var sauth = new JsonObject
        {
            ["timestamp"] = "",
            ["userid"] = "",
            ["realname"] = "{\"realname_type\":\"0\"}",
            ["gameid"] = "x19",
            ["login_channel"] = "4399com",
            ["app_channel"] = "4399com",
            ["platform"] = "ad",
            ["sdkuid"] = cred.Uid,
            ["sessionid"] = cred.State,
            ["sdk_version"] = "1.0.0",
            ["udid"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(),
            ["deviceid"] = deviceId,
            ["aim_info"] = await AimInfoAsync(ct),
            ["client_login_sn"] = deviceId,
            ["gas_token"] = "",
            ["source_platform"] = "ad",
            ["ip"] = await GatewayIpAsync(ct),
        }.ToJsonString(Json);
        return await LoginWithCookieAsync(sauth, ct);
    }

    /// <summary>The cookie may be the sauth_json itself or a {"sauth_json":"..."} wrapper.</summary>
    public async Task<NeteaseSession> LoginWithCookieAsync(string cookie, CancellationToken ct = default)
    {
        var sauthJson = UnwrapCookie(cookie);
        using var sauthDoc = JsonDocument.Parse(sauthJson);
        var channel = sauthDoc.RootElement.TryGetProperty("login_channel", out var lc) ? lc.GetString() ?? "netease" : "netease";
        var appChannel = sauthDoc.RootElement.TryGetProperty("app_channel", out var ac) ? ac.GetString() ?? channel : channel;

        if (channel != "netease") await UniSauthAsync(sauthJson, ct);

        var request = JsonSerializer.Serialize(new { sauth_json = sauthJson }, Json);

        using var otpDoc = await PostJsonAsync(CoreHost + "/login-otp", request, ct);
        var otp = EntityOf(otpDoc, "login-otp");
        var aid = otp.GetProperty("aid").GetRawText().Trim('"');
        var otpToken = otp.GetProperty("otp_token").GetString() ?? "";

        var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(2));
        var saData = new JsonObject
        {
            ["os_name"] = "windows",
            ["os_ver"] = "Microsoft Windows 11 专业版",
            ["mac_addr"] = RandomMac(),
            ["udid"] = "0000000000000000" + suffix,
            ["app_ver"] = _launcherVersion,
            ["sdk_ver"] = "",
            ["network"] = "",
            ["disk"] = suffix,
            ["is64bit"] = "1",
            ["video_card1"] = "Microsoft Hyper-V 视频",
            ["video_card2"] = "Microsoft Remote Display Adapter",
            ["video_card3"] = "",
            ["video_card4"] = "",
            ["launcher_type"] = "PC_java",
            ["pay_channel"] = appChannel,
            ["dotnet_ver"] = "4.8.0",
            ["cpu_type"] = "Intel(R) Core(TM) i9-14900KF",
            ["ram_size"] = "8589934592",
            ["device_width"] = "1920",
            ["device_height"] = "1080",
            ["os_detail"] = "10.0.26100",
        }.ToJsonString(Json);
        var authBody = new JsonObject
        {
            ["sa_data"] = saData,
            ["sauth_json"] = sauthJson,
            ["version"] = new JsonObject { ["version"] = _launcherVersion, ["launcher_md5"] = "", ["updater_md5"] = "" },
            ["sdkuid"] = null,
            ["aid"] = aid,
            ["hasMessage"] = false,
            ["hasGmail"] = false,
            ["otp_token"] = otpToken,
            ["otp_pwd"] = null,
            ["lock_time"] = 0,
            ["env"] = null,
            ["min_engine_version"] = null,
            ["min_patch_version"] = null,
            ["verify_status"] = 0,
            ["unisdk_login_json"] = null,
            ["token"] = null,
            ["is_register"] = true,
            ["entity_id"] = null,
        }.ToJsonString(Json);

        using var req = new HttpRequestMessage(HttpMethod.Post, CoreHost + "/authentication-otp")
        {
            Content = new ByteArrayContent(X19HttpCipher.Encrypt(Encoding.UTF8.GetBytes(authBody))),
        };
        using var resp = await _http.SendAsync(req, ct);
        var plain = X19HttpCipher.Decrypt(await resp.Content.ReadAsByteArrayAsync(ct))
                    ?? throw new InvalidDataException($"authentication-otp 响应无法解密 http={(int)resp.StatusCode}");
        using var authDoc = JsonDocument.Parse(plain);
        var auth = EntityOf(authDoc, "authentication-otp");
        var entityId = auth.GetProperty("entity_id").GetString()!;
        var token = auth.GetProperty("token").GetString()!;
        var account = auth.TryGetProperty("account", out var a) ? a.GetString() ?? "" : "";

        try
        {
            await PostSignedAsync(CoreHost + "/interconn/web/game-play-v2/login-start", "{\"strict_mode\":true}", entityId, token, ct);
        }
        catch (Exception ex) { Log.Warn("[login] login-start 失败（忽略）: " + ex.Message); }

        Log.Info($"[login] 登录成功 userId={entityId} channel={channel} token={token[..Math.Min(4, token.Length)]}…");
        return new NeteaseSession(int.Parse(entityId), token, account, channel);
    }

    private sealed record Device(string Id, string Key);

    private string LoadOrCreateUnique()
    {
        var uniqueFile = Path.Combine(_stateDir, MPayGameId + "-guid.cds");
        if (!File.Exists(uniqueFile)) File.WriteAllText(uniqueFile, Guid.NewGuid().ToString("N"));
        return File.ReadAllText(uniqueFile).Trim();
    }

    private async Task<(string Unique, Device Device)> LoadOrCreateDeviceAsync(CancellationToken ct)
    {
        var deviceFile = Path.Combine(_stateDir, MPayGameId + "-device.cds");
        var unique = LoadOrCreateUnique();

        if (!File.Exists(deviceFile))
        {
            var form = BaseParams()
                .Concat(new Dictionary<string, string>
                {
                    ["brand"] = "Microsoft",
                    ["device_model"] = "pc_mode",
                    ["device_name"] = "PC-" + RandomLetters(12),
                    ["device_type"] = "Computer",
                    ["init_urs_device"] = "0",
                    ["mac"] = RandomMac(":"),
                    ["resolution"] = "1920x1080",
                    ["system_name"] = "windows",
                    ["system_version"] = "10.0.22621",
                    ["unique_id"] = unique,
                });
            using var resp = await _http.PostAsync($"https://service.mkey.163.com/mpay/games/{MPayGameId}/devices",
                new FormUrlEncodedContent(form), ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("MPay 设备注册失败: " + text);
            File.WriteAllText(deviceFile, text);
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(deviceFile));
        var d = doc.RootElement.GetProperty("device");
        return (unique, new Device(d.GetProperty("id").GetString()!, d.GetProperty("key").GetString()!));
    }

    private async Task<JsonElement> MPayLoginAsync(string email, string password, string unique, Device device, CancellationToken ct)
    {
        var parameters = JsonSerializer.Serialize(new
        {
            password = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(password))).ToLowerInvariant(),
            unique_id = unique,
            username = email,
        }, Json);
        using var aes = Aes.Create();
        aes.Key = Convert.FromHexString(device.Key);
        var encrypted = Convert.ToHexString(aes.EncryptEcb(Encoding.UTF8.GetBytes(parameters), PaddingMode.PKCS7)).ToLowerInvariant();

        var form = BaseParams().Concat(new Dictionary<string, string>
        {
            ["opt_fields"] = "nickname,avatar,realname_status,mobile_bind_status,mask_related_mobile,related_login_status",
            ["params"] = encrypted,
            ["un"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(email)),
        });
        using var resp = await _http.PostAsync($"https://service.mkey.163.com/mpay/games/{MPayGameId}/devices/{device.Id}/users",
            new FormUrlEncodedContent(form), ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            if (text.Contains("\"code\":1351") || text.Contains("\"code\": 1351"))
                throw new InvalidOperationException("网易账号需要安全验证（code 1351），请先在官方启动器完成验证，或改用 cookie 登录: " + text);
            throw new InvalidOperationException("网易邮箱登录失败: " + text);
        }
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("user").Clone();
    }

    private IEnumerable<KeyValuePair<string, string>> BaseParams() => new Dictionary<string, string>
    {
        ["app_channel"] = "netease",
        ["app_mode"] = "2",
        ["app_type"] = "games",
        ["arch"] = "win_x64",
        ["cv"] = "c4.2.0",
        ["mcount_app_key"] = "EEkEEXLymcNjM42yLY3Bn6AO15aGy4yq",
        ["mcount_transaction_id"] = "0",
        ["process_id"] = Environment.ProcessId.ToString(),
        ["sv"] = "10.0.22621",
        ["updater_cv"] = "c1.0.0",
        ["game_id"] = MPayGameId,
        ["gv"] = _launcherVersion,
    };

    private async Task UniSauthAsync(string sauthJson, CancellationToken ct)
    {
        using var resp = await _http.PostAsync("https://mgbsdk.matrix.netease.com/x19/sdk/uni_sauth",
            new StringContent(sauthJson, Encoding.UTF8, "application/json"), ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(text);
        var code = doc.RootElement.TryGetProperty("code", out var c) ? c.ToString() : "";
        if (code == "200") return;
        Log.Debug("[login] uni_sauth 被拒 code=" + code + " " + text);
        var reason = UniSauthReason(doc.RootElement);
        if (reason.Contains("10204"))
            throw new InvalidOperationException("渠道凭证已失效（10204 验证失败）：这个账号在别处重新登录过，旧的 sauth_json 已作废。Cookie 账号请重新获取 sauth_json，4399 账号请重新登录。");
        throw new InvalidOperationException($"渠道校验未通过（code={code}）：{reason}");
    }

    /// <summary>Extracts a readable reason from a rejected uni_sauth response: msg without ntsdk markup, else the base64-decoded debug_message.</summary>
    private static string UniSauthReason(JsonElement root)
    {
        var msg = root.TryGetProperty("msg", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
        msg = System.Text.RegularExpressions.Regex.Replace(msg, @"</?ntsdk[^>]*>", "").Trim();
        if (msg.Length > 0) return msg;
        if (root.TryGetProperty("debug_message", out var d) && d.ValueKind == JsonValueKind.String && d.GetString() is { Length: > 0 } raw)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(raw)).Trim(); }
            catch (FormatException) { return raw; }
        }
        return root.GetRawText();
    }

    /// <summary>aim_info: whoami geo data (same as FandNEL's InternalQuery.ToAimInfo); falls back to defaults on failure.</summary>
    private async Task<string> AimInfoAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://whoami.nie.netease.com/v6");
            req.Headers.Add("X-AUTH-PRODUCT", "g0");
            req.Headers.Add("X-AUTH-TOKEN", "token.efa8zUW6sxjR");
            req.Headers.Add("X-IPDB-LOCALE", "en");
            req.Headers.Add("X-PROJECT_CODE", "x19");
            using var resp = await _http.SendAsync(req, ct);
            using var who = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var geo = JsonNode.Parse(Convert.FromBase64String(who.RootElement.GetProperty("payload").GetString()!))!.AsObject();
            var aim = new JsonObject();
            foreach (var k in new[] { "code_1", "code_2", "code_3", "code_4", "isp" })
                aim[k] = geo[k]?.DeepClone();
            aim["aim"] = geo["ip"]?.DeepClone();
            aim["country"] = "CN";
            aim["tz"] = "+0800";
            aim["tzid"] = "";
            return aim.ToJsonString(Json);
        }
        catch (Exception ex)
        {
            Log.Warn("[login] whoami 失败，aim_info 用默认值: " + ex.Message);
            return "{\"aim\":\"127.0.0.1\",\"country\":\"CN\",\"tz\":\"+0800\",\"tzid\":\"\"}";
        }
    }

    private async Task<string> GatewayIpAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "http://nstool.netease.com/internalquery");
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            using var resp = await _http.SendAsync(req, ct);
            foreach (var line in (await resp.Content.ReadAsStringAsync(ct)).Split('\n'))
            {
                var parts = line.Split('=');
                if (parts.Length == 2 && parts[0].Trim() == "gw" && parts[1].Trim().Length > 0) return parts[1].Trim();
            }
        }
        catch (Exception ex) { Log.Warn("[login] internalquery 失败，ip 用默认值: " + ex.Message); }
        return "127.0.0.1";
    }

    private async Task<JsonDocument> PostJsonAsync(string url, string body, CancellationToken ct)
    {
        using var resp = await _http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"), ct);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
    }

    private async Task PostSignedAsync(string url, string body, string userId, string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        foreach (var kv in HttpToken.Compute(new Uri(url).AbsolutePath, body, userId, token)) req.Headers.Add(kv.Key, kv.Value);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }

    private static JsonElement EntityOf(JsonDocument doc, string api)
    {
        var root = doc.RootElement;
        var code = root.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
        if (code != 0 || !root.TryGetProperty("entity", out var e) || e.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"{api} 失败 code={code}: {(root.TryGetProperty("message", out var m) ? m.GetString() : root.GetRawText())}");
        return e;
    }

    private static string UnwrapCookie(string cookie)
    {
        cookie = cookie.Trim();
        try
        {
            using var doc = JsonDocument.Parse(cookie);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("cookie 应为 sauth_json 对象（{...}），当前是 " + doc.RootElement.ValueKind);
            if (doc.RootElement.TryGetProperty("sauth_json", out var inner) && inner.ValueKind == JsonValueKind.String)
                return inner.GetString()!;
        }
        catch (JsonException) { throw new InvalidDataException("cookie 不是合法 JSON（应为 sauth_json）"); }
        return cookie;
    }

    private static string RandomMac(string sep = "")
    {
        var b = RandomNumberGenerator.GetBytes(6);
        b[0] = (byte)((b[0] & 0xFE) | 0x02);
        return string.Join(sep, b.Select(x => x.ToString("X2")));
    }

    private static string RandomLetters(int n) =>
        RandomNumberGenerator.GetString("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", n);
}

/// <summary>x19 core encrypted request body (same as FandNEL's HttpUtil): AES-CBC no padding, with the IV and key index appended.</summary>
internal static class X19HttpCipher
{
    private static readonly byte[][] Keys =
        "MK6mipwmOUedplb6,OtEylfId6dyhrfdn,VNbhn5mvUaQaeOo9,bIEoQGQYjKd02U0J,fuaJrPwaH2cfXXLP,LEkdyiroouKQ4XN1,jM1h27H4UROu427W,DhReQada7gZybTDk,ZGXfpSTYUvcdKqdY,AZwKf7MWZrJpGR5W,amuvbcHw38TcSyPU,SI4QotspbjhyFdT0,VP4dhjKnDGlSJtbB,UXDZx4KhZywQ2tcn,NIK73ZNvNqzva4kd,WeiW7qU766Q1YQZI"
            .Split(',').Select(Encoding.ASCII.GetBytes).ToArray();

    public static byte[] Encrypt(byte[] body)
    {
        var padded = new byte[(body.Length + 16 + 15) / 16 * 16];
        body.CopyTo(padded, 0);
        var iv = Encoding.ASCII.GetBytes(RandomNumberGenerator.GetString("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz", 16));
        iv.CopyTo(padded, body.Length);
        byte flags = (byte)((Random.Shared.Next(0, Keys.Length - 1) << 4) | 2);
        using var aes = Aes.Create();
        aes.Key = Keys[(flags >> 4) & 0xF];
        var enc = aes.EncryptCbc(padded, iv, PaddingMode.None);
        var outp = new byte[16 + enc.Length + 1];
        iv.CopyTo(outp, 0);
        enc.CopyTo(outp, 16);
        outp[^1] = flags;
        return outp;
    }

    public static byte[]? Decrypt(byte[] body)
    {
        if (body.Length < 18) return null;
        using var aes = Aes.Create();
        aes.Key = Keys[(body[^1] >> 4) & 0xF];
        var dec = aes.DecryptCbc(body.AsSpan(16, body.Length - 17), body.AsSpan(0, 16), PaddingMode.None);
        int nonZero = 0, end = dec.Length - 1;
        while (nonZero < 16 && end >= 0) { if (dec[end--] != 0) nonZero++; }
        return dec[..(end + 1)];
    }
}
