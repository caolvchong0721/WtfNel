using System.Windows;
using Microsoft.Win32;

namespace WtfNel.Ui.Services;

/// <summary>Swaps the light/dark color dictionary at runtime; "System" follows the Windows app theme.</summary>
public static class ThemeService
{
    public static event Action? Changed;

    public static bool IsDark { get; private set; }

    public static void Apply(string mode)
    {
        IsDark = mode switch
        {
            "Dark" => true,
            "Light" => false,
            _ => SystemPrefersDark(),
        };
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var palette = new ResourceDictionary { Source = new Uri($"Themes/{(IsDark ? "Dark" : "Light")}.xaml", UriKind.Relative) };
        if (dictionaries.Count > 0) dictionaries[0] = palette;
        else dictionaries.Add(palette);
        Changed?.Invoke();
    }

    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }
}
