using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WtfNel.Core.Auth.Login;

/// <summary>4399 PC channel credentials returned by the 4399 SDK, used to build the sauth_json.</summary>
public sealed record Pc4399Credentials(string Username, string Uid, string Token, string Time);

/// <summary>4399 PC login (ptlogin.4399.com username + password), ported from FandNEL's Pc4399.</summary>
public sealed partial class Pc4399Login : IDisposable
{
    private const string AppId = "kid_wdsj";
    private const string GameUrl = "https://cdn.h5wan.4399sj.com/microterminal-h5-frame?game_id=500352";
    private const string LoginHost = "https://ptlogin.4399.com";
    private const string ServiceHost = "https://microgame.5054399.net";

    private readonly HttpClient _http;
    private readonly ICaptchaPrompt? _captcha;

    public Pc4399Login(ICaptchaPrompt? captcha = null)
    {
        _captcha = captcha;
        _http = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true, AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    }

    public void Dispose() => _http.Dispose();

    public async Task<Pc4399Credentials> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        var form = LoginParams();
        form["username"] = username;
        form["password"] = password;

        using (var frame = await _http.PostAsync(LoginHost + "/ptlogin/loginFrame.do?v=1", new FormUrlEncodedContent(form), ct))
        {
            var text = await frame.Content.ReadAsStringAsync(ct);
            if (text.Contains("账号异常，请输入验证码"))
            {
                var id = CaptchaIdRegex().Match(text);
                if (_captcha == null || !id.Success)
                    throw new InvalidOperationException("4399 账号需要验证码，请先在 4399 网页登录一次后再试");
                var image = await _http.GetByteArrayAsync($"{LoginHost}/ptlogin/captcha.do?captchaId={id.Groups[1].Value}", ct);
                form["sessionId"] = id.Groups[1].Value;
                form["inputCaptcha"] = _captcha.Image(image).Trim();
            }
        }

        using (var login = await _http.PostAsync(LoginHost + "/ptlogin/login.do?v=1", new FormUrlEncodedContent(form), ct))
        {
            if (!login.IsSuccessStatusCode) throw new InvalidOperationException($"4399 登录失败 http={(int)login.StatusCode}");
        }

        var query = await CheckKidLoginAsync(ct);
        var data = Parse(await SdkLoginDataAsync(query, ct));
        string Get(string key) => data.TryGetValue(key, out var v) ? v : "";
        if (Get("uid") == "" || Get("token") == "")
            throw new InvalidOperationException("4399 登录失败：未拿到 uid/token，请检查账号密码");
        return new Pc4399Credentials(Get("username"), Get("uid"), Get("token"), Get("time"));
    }

    private async Task<string> CheckKidLoginAsync(CancellationToken ct)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var query = Encode(new Dictionary<string, string>
        {
            ["appId"] = AppId,
            ["gameUrl"] = GameUrl,
            ["isCrossDomain"] = "1",
            ["nick"] = "null",
            ["onLineStart"] = "false",
            ["ptLogin"] = "true",
            ["rand_time"] = "$randTime",
            ["retUrl"] = $"https://ptlogin.4399.com/resource/ucenter.html?action=login&appId=kid_wdsj&loginLevel=8&regLevel=8&bizId=2201001794&externalLogin=qq&qrLogin=true&layout=vertical&level=101&css=https://microgame.5054399.net/v2/resource/cssSdk/default/login.css&v=2018_11_26_16&postLoginHandler=redirect&checkLoginUserCookie=true&redirectUrl=http%3A%2F%2Fcdn.h5wan.4399sj.com%2Fmicroterminal-h5-frame%3Fgame_id%3D500352%26rand_time%3D{timestamp}",
            ["show"] = "1",
        });
        using var resp = await _http.GetAsync(LoginHost + "/ptlogin/checkKidLoginUserCookie.do?" + query, ct);
        var uri = resp.RequestMessage?.RequestUri?.ToString() ?? throw new InvalidOperationException("4399 登录失败：没有跳转地址");
        var start = uri.LastIndexOf('?');
        if (start < 0) throw new InvalidOperationException("4399 登录失败：跳转地址没有参数 " + uri);
        return uri[(start + 1)..];
    }

    private async Task<string> SdkLoginDataAsync(string query, CancellationToken ct)
    {
        var callback = $"jQuery1830{RandomNumberGenerator.GetString("0123456789", 16)}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        var url = ServiceHost + "/v2/service/sdk/info?" + Encode(new Dictionary<string, string> { ["callback"] = callback, ["queryStr"] = query });
        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"4399 uni-auth 失败 http={(int)resp.StatusCode}");
        var text = await resp.Content.ReadAsStringAsync(ct);
        var open = text.IndexOf(callback + "(", StringComparison.Ordinal);
        var close = text.LastIndexOf(')');
        if (open < 0 || close <= open) throw new InvalidOperationException("4399 uni-auth 响应格式异常: " + text[..Math.Min(text.Length, 200)]);
        var json = text[(open + callback.Length + 1)..close];
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object &&
            d.TryGetProperty("sdk_login_data", out var s) && s.GetString() is { Length: > 0 } data)
            return data;
        var msg = root.TryGetProperty("msg", out var m) ? m.GetString() : json;
        throw new InvalidOperationException("4399 uni-auth 失败: " + msg);
    }

    private static Dictionary<string, string> Parse(string query)
    {
        var map = new Dictionary<string, string>();
        foreach (var item in query[(query.IndexOf('?') + 1)..].Split('&'))
        {
            var parts = item.Split('=');
            if (parts.Length == 2) map[parts[0]] = parts[1];
        }
        return map;
    }

    private static string Encode(Dictionary<string, string> values) =>
        string.Join("&", values.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));

    [GeneratedRegex(@"captcha\.do\?captchaId=([A-Za-z0-9_\-]+)")]
    private static partial Regex CaptchaIdRegex();

    private static Dictionary<string, string> LoginParams() => new()
    {
        ["appId"] = AppId,
        ["autoLogin"] = "on",
        ["bizId"] = "2201001794",
        ["css"] = "https://microgame.5054399.net/v2/resource/cssSdk/default/login.css",
        ["displayMode"] = "popup",
        ["externalLogin"] = "qq",
        ["gameId"] = "wd",
        ["iframeId"] = "popup_login_frame",
        ["includeFcmInfo"] = "false",
        ["layout"] = "vertical",
        ["layoutSelfAdapting"] = "true",
        ["level"] = "8",
        ["loginFrom"] = "uframe",
        ["mainDivId"] = "popup_login_div",
        ["postLoginHandler"] = "default",
        ["redirectUrl"] = "",
        ["regLevel"] = "8",
        ["sec"] = "1",
        ["sessionId"] = "",
        ["userNameLabel"] = "4399用户名",
        ["userNameTip"] = "请输入4399用户名",
        ["welcomeTip"] = "欢迎回到4399",
    };
}
