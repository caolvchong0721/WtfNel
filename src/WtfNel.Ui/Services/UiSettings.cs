using System.IO;
using System.Text.Json;

namespace WtfNel.Ui.Services;

/// <summary>UI-only preferences stored in ui.json next to the exe.</summary>
public sealed class UiSettings
{
    public string Theme { get; set; } = "System";
    public bool CheckUpdateOnStart { get; set; } = true;
    public string SkippedVersion { get; set; } = "";
    public string LastAccount { get; set; } = "";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "ui.json");

    public static UiSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath), Json) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json)); }
        catch (IOException) { }
    }
}
