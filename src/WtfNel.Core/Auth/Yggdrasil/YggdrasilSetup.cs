using System.Text.RegularExpressions;
using WtfNel.Core;
using WtfNel.Core.Logging;

namespace WtfNel.Core.Auth.Yggdrasil;

/// <summary>Assembles the scattered config/template values into the objects Yggdrasil authentication needs.</summary>
public static class YggdrasilSetup
{
    /// <summary>bootstrap / dat file md5 by game version (from AlaneNEL's Md5Mapping).</summary>
    private static readonly Dictionary<string, (string Bootstrap, string Dat)> Md5Map = new()
    {
        ["1.7.10"] = ("A895FE657915D58F55919CEACD30209D", "538D33D5F35EF01736EDA30F94C61DF6"),
        ["1.8.9"] = ("A895FE657915D58F55919CEACD30209D", "0CF2074AA7D4B543E35A3D6BB57AF861"),
        ["1.12.2"] = ("A895FE657915D58F55919CEACD30209D", "51581ADD89B8AC5A0D8CCDD0E33EE1DE"),
        ["1.16"] = ("7B101583C3965371B89A3C9115B27526", "B0712F34B0A584D05D9D29FA68759E29"),
        ["1.18"] = ("C3BD2115F23F6FE4B2ADCC7FC4DEFFEA", "56677A2BB31E18246FA241FB02E16D0E"),
        ["1.20"] = ("2A7A476411A1687A56DC6848829C1AE4", "D285CBF97D9BA30D3C445DBF1C342634"),
        ["1.21"] = ("684528BF492A84489F825F5599B3E1C6", "574033E7E4841D8AC4C14D7FA5E05337"),
    };

    public static string LauncherVersion(ShadowConfig cfg) =>
        string.IsNullOrWhiteSpace(cfg.YggdrasilLauncherVersion) ? cfg.LauncherVersion : cfg.YggdrasilLauncherVersion;

    private static Login.NeteaseSession? _session;

    /// <summary>
    /// userToken source priority: live login (when LoginChannel is set) → config UserToken → command-line -DToken.
    /// A successful login also overwrites cfg.Uid / cfg.UserToken; a process logs in at most once.
    /// </summary>
    public static string ResolveUserToken(ShadowConfig cfg, string? commandLine)
    {
        if (_session is not null || !string.IsNullOrWhiteSpace(cfg.LoginChannel))
        {
            _session ??= LoginAsync(cfg).GetAwaiter().GetResult();
            cfg.Uid = _session.UserId;
            cfg.UserToken = _session.UserToken;
            return _session.UserToken;
        }
        if (!string.IsNullOrWhiteSpace(cfg.UserToken)) return cfg.UserToken.Trim();
        var m = commandLine is null ? null : Regex.Match(commandLine, @"-DToken=([0-9A-Fa-f]+)");
        if (m is { Success: true }) return LaunchToken.Decrypt(m.Groups[1].Value);
        throw new InvalidOperationException("没有 userToken：在 shadow.json 填 UserToken，或保证模板里带 -DToken");
    }

    /// <summary>Whether live login was used (tpl rewrites userId/uid in the template accordingly).</summary>
    public static bool LoggedIn => _session is not null;

    /// <summary>A session already logged in from the TUI; later ResolveUserToken reuses it without logging in again.</summary>
    public static void UseSession(Login.NeteaseSession session) => _session = session;

    private static Task<Login.NeteaseSession> LoginAsync(ShadowConfig cfg)
    {
        var channel = cfg.LoginChannel.Trim().ToLowerInvariant();
        var secret = channel == "cookie" ? cfg.LoginCookie : cfg.LoginPassword;
        if (channel == "cookie" && string.IsNullOrWhiteSpace(secret) && File.Exists(Path.Combine(AppContext.BaseDirectory, "cookie.txt")))
            secret = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "cookie.txt"));
        return LoginAsync(cfg, channel, cfg.LoginAccount, secret);
    }

    /// <summary>Logs in by channel: netease = email + password, cookie = sauth_json.</summary>
    public static async Task<Login.NeteaseSession> LoginAsync(ShadowConfig cfg, string channel, string account, string secret, CancellationToken ct = default)
    {
        using var login = new Login.NeteaseLogin(LauncherVersion(cfg), Path.Combine(AppContext.BaseDirectory, "cache", "login"));
        Log.Info($"[login] 实时登录 channel={channel}");
        switch (channel)
        {
            case "netease":
                if (string.IsNullOrWhiteSpace(account) || string.IsNullOrEmpty(secret))
                    throw new InvalidOperationException("网易邮箱登录需要邮箱和密码");
                return await login.LoginWithEmailAsync(account.Trim(), secret, ct);
            case "cookie":
                if (string.IsNullOrWhiteSpace(secret))
                    throw new InvalidOperationException("Cookie 登录需要 sauth_json");
                return await login.LoginWithCookieAsync(secret, ct);
            default:
                throw new NotSupportedException($"不支持的登录渠道: {channel}（可用 netease / cookie）");
        }
    }

    public static string? TemplateGameId(string? commandLine)
    {
        var m = commandLine is null ? null : Regex.Match(commandLine, @"-DlauncherGameId=(\d+)");
        return m is { Success: true } ? m.Groups[1].Value : null;
    }

    public static AuthLibServer CreateServer(ShadowConfig cfg, string userToken, ModInfoProvider? mods = null)
    {
        var launcherVersion = LauncherVersion(cfg);
        var (bootstrap, dat) = ResolveMd5(cfg);
        var client = new YggdrasilClient(new YggdrasilData(launcherVersion, cfg.YggdrasilChannel, cfg.CrcSalt), cfg.YggdrasilServer);
        mods ??= CreateModProvider(cfg);

        Log.Info($"[ygg] launcherVersion={launcherVersion} gameVersion={cfg.YggGameVersion} crcSalt={cfg.CrcSalt[..Math.Min(6, cfg.CrcSalt.Length)]}…");

        return new AuthLibServer(cfg.AuthPort, client, async (gameId, userId, ct) => new GameProfile
        {
            GameId = gameId,
            GameVersion = cfg.YggGameVersion,
            BootstrapMd5 = bootstrap,
            DatFileMd5 = dat,
            ModInfo = await mods.GetAsync(gameId, userId, userToken, ct),
            UserId = userId,
            UserToken = userToken,
        });
    }

    public static ModInfoProvider CreateModProvider(ShadowConfig cfg) =>
        new(LauncherVersion(cfg), cfg.McVersionId, cfg.GameType, Path.Combine(AppContext.BaseDirectory, "cache", "mods"));

    private static (string, string) ResolveMd5(ShadowConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.BootstrapMd5) && !string.IsNullOrWhiteSpace(cfg.DatFileMd5))
            return (cfg.BootstrapMd5, cfg.DatFileMd5);
        if (Md5Map.TryGetValue(cfg.YggGameVersion, out var pair)) return pair;
        throw new InvalidOperationException($"没有 {cfg.YggGameVersion} 的 bootstrap/dat md5，请在 shadow.json 里填 BootstrapMd5/DatFileMd5");
    }
}
