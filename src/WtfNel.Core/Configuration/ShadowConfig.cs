using System.Text.Json;
using System.Text.Json.Serialization;

namespace WtfNel.Core;

/// <summary>Runtime configuration. Every field can be overridden in shadow.json to switch machines or accounts.</summary>
public sealed class ShadowConfig
{
    /// <summary>The .minecraft root directory. Empty triggers auto-detection (see <see cref="GameDirLocator"/>).</summary>
    public string GameDir { get; set; } = "";

    /// <summary>
    /// Java used by the client. Empty uses the NetEase Java (&lt;MCL root&gt;\ext\jre-v64-220420,
    /// downloaded if missing); the client requires Java to live under MCLDownload, so a path elsewhere is ignored.
    /// </summary>
    public string JavaExe { get; set; } = "";

    /// <summary>The version directory name under versions.</summary>
    public string VersionId { get; set; } = "1.20";

    /// <summary>The program used as the client's parent process. Defaults to the WPFLauncher.exe stub next to the exe; set a real launcher path to use the real one.</summary>
    public string LauncherPath { get; set; } = "WPFLauncher.exe";

    /// <summary>Launcher process name, used to reuse a running instance.</summary>
    public string LauncherProcessName { get; set; } = "WPFLauncher";

    /// <summary>Launcher build written into the placeholder's version resource; refreshed from the x19 patch list on every launch.</summary>
    public string LauncherFileVersion { get; set; } = "";

    /// <summary>-Dminecraft.launcher.brand, the official client reports "netease".</summary>
    public string LauncherBrand { get; set; } = "netease";

    /// <summary>filterpath of --userProperties; empty means %LOCALAPPDATA%\Netease\MCLauncher\config\fltmp.</summary>
    public string FilterPath { get; set; } = "";

    /// <summary>Rewrite the placeholder's version resource and the filter path, and point the MCLauncher key at it before launching.</summary>
    public bool WriteLauncherRegistry { get; set; } = true;

    /// <summary>Whether to create the client process under the launcher process as its parent.</summary>
    public bool UseParentProcess { get; set; } = true;

    /// <summary>The real game server (direct IP/host). Empty or "auto" resolves it from X19 /item-address/get by gameId.</summary>
    public string RemoteHost { get; set; } = "auto";

    /// <summary>false = client connects directly to RemoteHost:RemotePort; true = connects to the local LocalPort, forwarded by GameRelay.</summary>
    public bool UseRelay { get; set; } = false;
    public int RemotePort { get; set; } = 25565;

    /// <summary>Local forwarding port; the game connects here and traffic is relayed to RemoteHost:RemotePort.</summary>
    public int LocalPort { get; set; } = 25565;

    /// <summary>Local bridge port, written as launcherport in the game's launch arguments.</summary>
    public int BridgePort { get; set; } = 11655;

    /// <summary>Max game memory (-Xmx, in MB).</summary>
    public int MaxMemoryMb { get; set; } = 4096;

    public string UserName { get; set; } = "";
    public string Uuid { get; set; } = "";
    public int Uid { get; set; }
    public string ClientId { get; set; } = "MjdhN2RkMDUtOGIzNy00MjFiLWE3OTQtNDllYzQ0MTY0ZWEz";
    public string Xuid { get; set; } = "2535472070902804";

    /// <summary>The launcherVersion in userPropertiesEx.</summary>
    public string LauncherVersion { get; set; } = "1.15.31.20647";
    public int GameType { get; set; } = 2;

    /// <summary>The authlib local auth port, written as -DlauncherControlPort (distinct from the launcherport bridge).</summary>
    public int AuthPort { get; set; } = 11656;

    /// <summary>The crcSalt bound to the launcher version.</summary>
    public string CrcSalt { get; set; } = "2AFA3B6116260DE4300577E013EAC757";

    /// <summary>Launcher version used for authentication; empty uses <see cref="LauncherVersion"/>. Must match CrcSalt.</summary>
    public string YggdrasilLauncherVersion { get; set; } = "1.15.34.21425";

    /// <summary>-DlauncherGameId: the network server item id to join (exceeds int). Empty uses <see cref="DefaultGameId"/>.</summary>
    public string LauncherGameId { get; set; } = "";

    /// <summary>Default network server item id.</summary>
    public const string DefaultGameId = "4661334467366178884";

    [JsonIgnore]
    public string GameIdOrDefault => string.IsNullOrWhiteSpace(LauncherGameId) ? DefaultGameId : LauncherGameId.Trim();

    public string YggdrasilChannel { get; set; } = "netease";

    /// <summary>Fixed auth server host:port; empty picks one at random from authserver.list.</summary>
    public string YggdrasilServer { get; set; } = "";

    /// <summary>Live login channel: empty = no login (use UserToken / template -DToken); netease = NetEase email+password; cookie = sauth_json.</summary>
    public string LoginChannel { get; set; } = "";

    /// <summary>NetEase email and password when LoginChannel = netease.</summary>
    public string LoginAccount { get; set; } = "";
    public string LoginPassword { get; set; } = "";

    /// <summary>The sauth_json when LoginChannel = cookie (or placed in cookie.txt next to the exe).</summary>
    public string LoginCookie { get; set; } = "";

    /// <summary>16-digit NetEase userToken. Empty derives it from the template -DToken; set, it also rewrites -DToken.</summary>
    public string UserToken { get; set; } = "";

    /// <summary>Game version name used in authentication (e.g. "1.20", the key into the md5 lookup table).</summary>
    public string YggGameVersion { get; set; } = "1.20";

    /// <summary>The mc_version_id for X19 APIs (EnumGameVersion value, 1.20 = 1020000).</summary>
    public int McVersionId { get; set; } = 1020000;

    /// <summary>Empty looks up the built-in mapping by <see cref="YggGameVersion"/>.</summary>
    public string BootstrapMd5 { get; set; } = "";
    public string DatFileMd5 { get; set; } = "";

    /// <summary>Before launch, fetch Java, the game itself, core mods and the server resource pack from the NetEase CDN (skipped when md5 is unchanged).</summary>
    public bool AutoInstall { get; set; } = true;

    /// <summary>
    /// When files the NetEase CDN does not serve are missing (versions\&lt;v&gt;\natives\runtime\*: WinLicense /
    /// libenvsdk / OpenSSL / VC runtimes, and versions\&lt;v&gt;\&lt;v&gt;.dat), copy them from this existing .minecraft.
    /// Empty only warns.
    /// </summary>
    public string LocalFallbackDir { get; set; } = "";

    /// <summary>Connect to the server directly, or stay in the lobby.</summary>
    public bool AutoConnect { get; set; } = true;

    /// <summary>Extra JVM arguments (inserted before the main class).</summary>
    public List<string> ExtraJvmArgs { get; set; } = new();

    /// <summary>Extra game arguments (appended at the end).</summary>
    public List<string> ExtraGameArgs { get; set; } = new();

    public string InjectTarget { get; set; } = "Minecraft";

    public List<string> InjectPayloads { get; set; } = new();

    public string InjectStrategy { get; set; } = "Auto";

    public int InjectWaitSeconds { get; set; } = 180;

    public string InjectInitExport { get; set; } = "";

    public bool EnableProxy { get; set; } = true;
    public bool EnableHwidSpoof { get; set; } = true;

    /// <summary>The client's auto-connect target: the real server directly, or the local forwarding port.</summary>
    [JsonIgnore]
    public string ConnectHost => UseRelay ? "127.0.0.1" : RemoteHost;
    [JsonIgnore]
    public int ConnectPort => UseRelay ? LocalPort : RemotePort;

    [JsonIgnore]
    public string LibraryDir => Path.Combine(GameDir, "libraries");

    [JsonIgnore]
    public string NativeDir => Path.Combine(GameDir, "versions", VersionId, "natives");

    [JsonIgnore]
    public string RuntimeDir => Path.Combine(NativeDir, "runtime");

    [JsonIgnore]
    public string VersionDir => Path.Combine(GameDir, "versions", VersionId);

    public static ShadowConfig Load(string path)
    {
        if (!File.Exists(path)) return new ShadowConfig();
        var opt = new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        return JsonSerializer.Deserialize<ShadowConfig>(File.ReadAllText(path), opt) ?? new ShadowConfig();
    }

    public void Save(string path)
    {
        var opt = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        File.WriteAllText(path, JsonSerializer.Serialize(this, opt));
    }
}
