using System.Text.Json;
using System.Text.Json.Serialization;

namespace WtfNel.Core.Auth;

/// <summary>A saved login account (stored in plain text in accounts.json).</summary>
public sealed class SavedAccount
{
    /// <summary>User-chosen display name.</summary>
    public string Name { get; set; } = "";

    /// <summary>netease = NetEase email + password; 4399pc / 4399com = 4399 username + password (stored in Email); cookie = sauth_json.</summary>
    public string Channel { get; set; } = "netease";

    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
    public string Cookie { get; set; } = "";

    /// <summary>The userId from the most recent login, for display only.</summary>
    public int UserId { get; set; }

    public DateTime? LastUsed { get; set; }

    /// <summary>The character (sub-account) last used to enter the game; shown first in the character list.</summary>
    public string LastRole { get; set; } = "";

    [JsonIgnore] public string Secret => Channel == "cookie" ? Cookie : Password;
    [JsonIgnore] public string ChannelLabel => Channel switch { "cookie" => "Cookie", "4399pc" => "4399", "4399com" => "4399com", _ => "网易邮箱" };
}

/// <summary>accounts.json next to the exe: the account list, most recently used first.</summary>
public sealed class AccountStore
{
    public List<SavedAccount> Accounts { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "accounts.json");

    public static AccountStore Load() =>
        File.Exists(FilePath) ? JsonSerializer.Deserialize<AccountStore>(File.ReadAllText(FilePath), Json) ?? new() : new();

    public void Save() => File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));

    public IEnumerable<SavedAccount> Ordered() => Accounts.OrderByDescending(a => a.LastUsed ?? DateTime.MinValue);
}
