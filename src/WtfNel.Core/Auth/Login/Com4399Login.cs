using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WtfNel.Core.Auth.Login;

/// <summary>4399 mobile SDK credentials (uid + OAuth state), used to build the 4399com sauth_json.</summary>
public sealed record Com4399Credentials(string Uid, string State);

/// <summary>4399com login (4399 mobile SDK OAuth), ported from FandNEL's Com4399; captchas are handed to <see cref="ICaptchaPrompt"/>.</summary>
public sealed partial class Com4399Login : IDisposable
{
    private const string ApiHost = "https://m.4399api.com";
    private const string LoginHost = "https://ptlogin.4399.com";
    private const string CallbackPath = "/openapi/oauth-callback.html?gamekey=44770&game_key=115716";
    private const int MaxRetry = 5;
    private const string UserAgent = "Mozilla/5.0 (Linux; Android 14; PFJM10 Build/UKQ1.230924.001; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/120.0.6099.230 Mobile Safari/537.36";

    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly string _deviceFile;
    private readonly ICaptchaPrompt? _captcha;
    private readonly HttpClient _http;
    private readonly HttpClient _noRedirect;

    private string _deviceId = "";
    private string _deviceIdSm = "";
    private string _udid = "";

    public Com4399Login(string stateDir, ICaptchaPrompt? captcha)
    {
        Directory.CreateDirectory(stateDir);
        _deviceFile = Path.Combine(stateDir, "4399com.cds");
        _captcha = captcha;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _noRedirect = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        foreach (var client in new[] { _http, _noRedirect })
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            client.DefaultRequestHeaders.Add("X-Requested-With", "com.netease.mc.m4399");
        }
        _noRedirect.DefaultRequestHeaders.Referrer = new Uri(LoginHost + "/oauth2/authorize.do");
    }

    public void Dispose()
    {
        _http.Dispose();
        _noRedirect.Dispose();
    }

    public async Task<Com4399Credentials> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        await LoadOrCreateDeviceAsync(ct);
        return await LoginAndAuthorizeAsync(username, password, null, null, 0, ct);
    }

    private async Task<Com4399Credentials> LoginAndAuthorizeAsync(string username, string password, string? captcha, string? captchaId, int retry, CancellationToken ct)
    {
        if (retry > MaxRetry) throw new InvalidOperationException("4399 登录重试次数过多");

        var form = new Dictionary<string, string>
        {
            ["isInputRealname"] = "false",
            ["isValidRealname"] = "false",
            ["sec"] = "1",
            ["password"] = password,
            ["username"] = username.ToLowerInvariant(),
            ["css"] = "",
            ["show_close_button"] = "",
            ["response_type"] = "TOKEN",
            ["client_id"] = "40f9e9b95d6c71ba5c6e0bd14c0abeff",
            ["show_4399"] = "",
            ["username_history"] = "",
            ["uid"] = "",
            ["expand_ext_login_list"] = "",
            ["ref"] = "{\"game\":\"115716\",\"channel\":\"\"}",
            ["autoCreateAccount"] = "",
            ["scope"] = "basic",
            ["bizId"] = "2100001792",
            ["state"] = await RequestStateAsync(ct),
            ["show_ext_login"] = "",
            ["reg_mode"] = "reg_phone",
            ["_d"] = _deviceId,
            ["show_back_button"] = "",
            ["auto_scroll"] = "",
            ["access_token"] = "",
            ["show_forget_password"] = "",
            ["auth_action"] = "ORILOGIN",
            ["redirect_uri"] = "https://m.4399api.com/openapi/oauth-callback.html?gamekey=44770&game_key=115716",
            ["show_topbar"] = "false",
            ["aid"] = "",
            ["cid"] = "",
        };
        if (captcha != null && captchaId != null)
        {
            form["captcha"] = captcha;
            form["captcha_id"] = captchaId;
        }

        using var login = await _noRedirect.PostAsync(LoginHost + "/oauth2/loginAndAuthorize.do?channel=&sdk=op&sdk_version=3.12.2.503",
            new FormUrlEncodedContent(form), ct);
        var loginText = await login.Content.ReadAsStringAsync(ct);
        if (loginText.Contains("请稍后再试"))
            throw new InvalidOperationException($"4399 限制了登录频率（http={(int)login.StatusCode} 请稍后再试），等几分钟再登录，别连续重试");

        if (loginText.Contains("验证码"))
        {
            var id = CaptchaIdRegex().Match(loginText);
            if (!id.Success) throw new InvalidOperationException("4399 需要验证码，但页面里找不到 captcha_id");
            var image = await _noRedirect.GetByteArrayAsync($"{LoginHost}/ptlogin/captcha.do?captchaId={id.Groups[1].Value}&xx=1", ct);
            var text = RequirePrompt().Image(image).Trim();
            return await LoginAndAuthorizeAsync(username, password, text, id.Groups[1].Value, retry + 1, ct);
        }

        var location = login.Headers.Location;
        if (location == null)
        {
            if (loginText.Contains("用户名或密码错误") || loginText.Contains("密码错误")) throw new InvalidOperationException("4399 用户名或密码错误");
            throw new InvalidOperationException("4399 登录失败：没有跳转地址 " + loginText[..Math.Min(loginText.Length, 200)]);
        }

        var redirect = location.IsAbsoluteUri ? location.AbsoluteUri : new Uri(new Uri(LoginHost), location).AbsoluteUri;
        if (captcha != null && captchaId == null)
            redirect += (redirect.Contains('?') ? "&" : "?") + "captcha=" + Uri.EscapeDataString(captcha);

        using var auth = await _http.GetAsync(redirect, ct);
        auth.EnsureSuccessStatusCode();
        var authText = await auth.Content.ReadAsStringAsync(ct);

        if (authText.Contains("登录状态已失效，请重新登录"))
        {
            await RefreshDeviceStateAsync(ct);
            return await LoginAndAuthorizeAsync(username, password, null, null, retry + 1, ct);
        }
        if (authText.Contains("登录成功，但账号存在异常，需要验证"))
        {
            var token = await SolveVerifyCaptchaAsync(authText, ct);
            return await LoginAndAuthorizeAsync(username, password, token, null, retry + 1, ct);
        }

        using var doc = JsonDocument.Parse(authText);
        var root = doc.RootElement;
        var code = root.TryGetProperty("code", out var c) ? c.ToString() : "";
        if (code != "100" || !root.TryGetProperty("result", out var r) || r.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("4399 授权失败: " + (root.TryGetProperty("message", out var m) ? m.GetString() : authText));
        var uid = r.GetProperty("uid").ToString();
        var state = r.GetProperty("state").GetString() ?? "";
        return new Com4399Credentials(uid, state);
    }

    private async Task<string> SolveVerifyCaptchaAsync(string resultJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(resultJson);
        var root = doc.RootElement;
        var url = root.TryGetProperty("result", out var r) && r.TryGetProperty("url", out var u) ? u.GetString() : null;
        if (string.IsNullOrEmpty(url)) throw new InvalidOperationException("4399 账号需要验证，但没有验证码地址");

        for (var attempt = 0; attempt <= MaxRetry; attempt++)
        {
            using var captchaDoc = JsonDocument.Parse(await _http.GetStringAsync(url, ct));
            var res = captchaDoc.RootElement.GetProperty("result");
            var captchaId = res.TryGetProperty("captchaId", out var id) ? id.GetString() ?? "" : "";
            var image = Convert.FromBase64String(StripDataUrl(res.GetProperty("img").GetString() ?? ""));
            var size = ImageSize.Read(image) ?? (544, 306);

            string checkUrl;
            if (url.Contains("jigsaw"))
            {
                var piece = Convert.FromBase64String(StripDataUrl(res.GetProperty("img2").GetString() ?? ""));
                var x = (int)Math.Round(RequirePrompt().Jigsaw(image, piece) * 544.0 / size.Item1);
                checkUrl = "https://m.4399api.com/captcha/jigsaw-check.html?refer=sdk&v=" + Uri.EscapeDataString(EncryptJigsaw(Math.Clamp(x, 0, 494)))
                           + "&captchaId=" + Uri.EscapeDataString(captchaId);
            }
            else if (url.Contains("click"))
            {
                var text = res.GetProperty("text").GetString() ?? "";
                var points = RequirePrompt().Click(image, text);
                var payload = new JsonObject
                {
                    ["w"] = 544,
                    ["h"] = 306,
                    ["c"] = new JsonArray(points.Select(p => (JsonNode)new JsonObject
                    {
                        ["x"] = Math.Round(p.X * 544.0 / size.Item1, 2),
                        ["y"] = Math.Round(p.Y * 306.0 / size.Item2, 2),
                    }).ToArray()),
                }.ToJsonString(Json);
                checkUrl = "https://m.4399api.com/captcha/click-check.html?refer=sdk&v=" + Uri.EscapeDataString(payload)
                           + "&captchaId=" + Uri.EscapeDataString(captchaId);
            }
            else throw new InvalidOperationException("未知的 4399 验证码类型: " + url);

            using var check = JsonDocument.Parse(await _http.GetStringAsync(checkUrl, ct));
            var checkRoot = check.RootElement;
            if (checkRoot.TryGetProperty("code", out var cc) && cc.ToString() == "100")
            {
                var token = checkRoot.TryGetProperty("result", out var cr) && cr.TryGetProperty("token", out var t) ? t.GetString() ?? "" : "";
                return JsonSerializer.Serialize(new Dictionary<string, string> { ["v_token"] = token, ["captcha_id"] = captchaId, ["type"] = "0" });
            }
            Logging.Log.Warn("[login] 4399 验证码校验失败，重新获取: " + checkRoot.GetRawText());
        }
        throw new InvalidOperationException("4399 验证码多次校验失败");
    }

    private async Task<string> RequestStateAsync(CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await _http.GetStringAsync(ApiHost + CallbackPath, ct));
        var result = doc.RootElement.TryGetProperty("result", out var r) ? r.GetString() ?? "" : "";
        var state = QueryValue(result, "state");
        if (state == "") throw new InvalidOperationException("4399 OAuth 回调里没有 state: " + doc.RootElement.GetRawText());
        return state;
    }

    private async Task LoadOrCreateDeviceAsync(CancellationToken ct)
    {
        if (File.Exists(_deviceFile))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_deviceFile));
            var root = doc.RootElement;
            _deviceId = root.GetProperty("device-id").GetString() ?? "";
            _deviceIdSm = root.GetProperty("device-id-sm").GetString() ?? "";
            _udid = root.GetProperty("device-udid").GetString() ?? "";
            if (root.TryGetProperty("device-state", out var s) && s.ValueKind == JsonValueKind.String && _deviceId != "") return;
        }
        _deviceId = GenerateIdentifier();
        _deviceIdSm = GenerateIdentifier();
        _udid = Guid.NewGuid().ToString();
        await RefreshDeviceStateAsync(ct);
    }

    private async Task RefreshDeviceStateAsync(CancellationToken ct)
    {
        var device = new JsonObject
        {
            ["DEVICE_IDENTIFIER"] = _deviceId,
            ["SCREEN_RESOLUTION"] = "3200*1384",
            ["DEVICE_MODEL"] = "Oppo A5 2022",
            ["DEVICE_MODEL_VERSION"] = "14",
            ["SYSTEM_VERSION"] = "14",
            ["PLATFORM_TYPE"] = "Android",
            ["SDK_VERSION"] = "3.12.2.503",
            ["GAME_KEY"] = "115716",
            ["GAME_VERSION"] = "3.1.5.260925",
            ["BID"] = "com.netease.mc.m4399",
            ["RUNTIME"] = "Origin",
            ["CANAL_IDENTIFIER"] = "",
            ["UDID"] = _udid,
            ["DEBUG"] = "false",
            ["NETWORK_TYPE"] = "WIFI",
            ["GAME_BOX_VERSION"] = "",
            ["VIP_INFO"] = "",
            ["TEAM"] = 2,
            ["DEVICE_IDENTIFIER_SM"] = _deviceIdSm,
            ["UID"] = "",
        }.ToJsonString(Json);
        var form = new Dictionary<string, string> { ["usernames"] = "", ["top_bar"] = "1", ["state"] = "", ["device"] = device };
        using var resp = await _http.PostAsync(ApiHost + "/openapiv2/oauth.html", new FormUrlEncodedContent(form), ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var loginUrl = doc.RootElement.GetProperty("result").GetProperty("login_url").GetString() ?? "";
        var saved = new JsonObject
        {
            ["device-id"] = _deviceId,
            ["device-id-sm"] = _deviceIdSm,
            ["device-udid"] = _udid,
            ["device-state"] = QueryValue(loginUrl, "state"),
        };
        File.WriteAllText(_deviceFile, saved.ToJsonString(Json));
    }

    private ICaptchaPrompt RequirePrompt() =>
        _captcha ?? throw new InvalidOperationException("4399 需要验证码，当前环境无法输入");

    private static string EncryptJigsaw(int x)
    {
        using var aes = Aes.Create();
        aes.Key = MD5.HashData(Encoding.UTF8.GetBytes("df0a5f98c337de97"));
        var enc = aes.EncryptCbc(Encoding.UTF8.GetBytes("{\"x\":" + x + "}"), Encoding.UTF8.GetBytes("79c83220d9974edf"), PaddingMode.PKCS7);
        return Convert.ToBase64String(enc);
    }

    private static string StripDataUrl(string s)
    {
        var comma = s.IndexOf(',');
        return s.StartsWith("data:", StringComparison.Ordinal) && comma >= 0 ? s[(comma + 1)..] : s;
    }

    private static string QueryValue(string url, string key)
    {
        foreach (var item in url[(url.IndexOf('?') + 1)..].Split('&'))
        {
            var parts = item.Split('=');
            if (parts.Length == 2 && parts[0] == key) return parts[1];
        }
        return "";
    }

    private static string GenerateIdentifier() =>
        DateTime.Now.ToString("yyyyMMddHHmm") +
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString() + DateTime.Now.Ticks)))[..50];

    [GeneratedRegex("name\\s*=\\s*[\"']captcha_id[\"']\\s+value\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase)]
    private static partial Regex CaptchaIdRegex();
}
