using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using WtfNel.Core.Auth.Yggdrasil;

namespace WtfNel.Core;

/// <summary>
/// Builds the launch command line from the embedded template (Assets/launch-template.txt). The current
/// account's uid/token, the selected character and uuid, local paths, ports and server are all filled in
/// at launch time, so no external text file is needed.
/// </summary>
public static class LaunchCommand
{
    /// <summary>Native library extraction directory (LWJGL/JNA/netty, relative to .minecraft), matching the official launcher's name.</summary>
    private const string NativeCache = @"bin\d4abd45eda44d8516fe1c7a0c4f47a1dd04ab0d1";

    public static string Build(ShadowConfig cfg, string java, string gameId, string userToken)
    {
        static string Esc(string path) => path.Replace(@"\", @"\\");
        static string Quote(string v) => v.IndexOfAny([' ', '\t', '"']) < 0 ? v : "\"" + v.Replace("\"", "\\\"") + "\"";

        var vars = new Dictionary<string, string>
        {
            ["java"] = java,
            ["natives_dir"] = Esc(cfg.NativeDir),
            ["runtime_dir"] = Esc(cfg.RuntimeDir),
            ["native_cache"] = NativeCache,
            ["auth_port"] = cfg.AuthPort.ToString(),
            ["launcher_port"] = cfg.BridgePort.ToString(),
            ["game_id"] = gameId,
            ["user_id"] = cfg.Uid.ToString(),
            ["token"] = LaunchToken.Encrypt(userToken),
            ["max_memory"] = cfg.MaxMemoryMb.ToString(),
            ["version"] = cfg.VersionId,
            ["username"] = Quote(cfg.UserName),
            ["uuid"] = cfg.Uuid,
            ["client_id"] = cfg.ClientId,
            ["xuid"] = cfg.Xuid,
            ["quick_play"] = $@"quickPlay\java\{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.json",
            ["filter_key"] = RandomLower(32),
            ["filter_path"] = "",
            ["launcher_version"] = cfg.LauncherVersion,
            ["connect"] = cfg.AutoConnect ? $"--quickPlayMultiplayer {cfg.ConnectHost}:{cfg.ConnectPort}" : "",
            ["extra_jvm"] = string.Join(' ', cfg.ExtraJvmArgs.Select(Quote)),
            ["extra_game"] = string.Join(' ', cfg.ExtraGameArgs.Select(Quote)),
        };

        var line = Regex.Replace(LoadTemplate(), @"\$\{(\w+)\}", m =>
            vars.TryGetValue(m.Groups[1].Value, out var v) ? v : throw new InvalidDataException("启动模板里有未知占位符 " + m.Value));
        return Regex.Replace(line, " {2,}", " ").Trim();
    }

    /// <summary>Loads the embedded template: drops '#' comment lines and blank lines, then joins the rest with spaces.</summary>
    private static string LoadTemplate()
    {
        using var s = typeof(LaunchCommand).Assembly.GetManifestResourceStream("launch-template.txt")
                      ?? throw new FileNotFoundException("内置启动模板缺失");
        using var r = new StreamReader(s, Encoding.UTF8);
        return string.Join(' ', r.ReadToEnd().Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#')));
    }

    private static string RandomLower(int n) =>
        new(Enumerable.Range(0, n).Select(_ => (char)('a' + RandomNumberGenerator.GetInt32(26))).ToArray());
}
