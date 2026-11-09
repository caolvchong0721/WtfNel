using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WtfNel.Core.Logging;
using WtfNel.Ui.Services;

namespace WtfNel.Ui.Pages;

/// <summary>Settings: theme, game directory and memory, install/connect switches, injection payloads and update checks.</summary>
public partial class SettingsPage : UserControl
{
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) Load(); };
    }

    private void Load()
    {
        _loading = true;
        var cfg = AppState.Config;
        (AppState.Ui.Theme switch { "Light" => ThemeLight, "Dark" => ThemeDark, _ => ThemeSystem }).IsChecked = true;
        GameDirBox.Text = cfg.GameDir;
        MemoryBox.Text = cfg.MaxMemoryMb.ToString();
        AutoInstall.IsChecked = cfg.AutoInstall;
        AutoConnect.IsChecked = cfg.AutoConnect;
        EnableHwidSpoof.IsChecked = cfg.EnableHwidSpoof;
        CheckUpdate.IsChecked = AppState.Ui.CheckUpdateOnStart;
        Verbose.IsChecked = Log.Verbose;
        _loading = false;
    }

    private void OnTheme(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string mode }) return;
        AppState.Ui.Theme = mode;
        AppState.Ui.Save();
        ThemeService.Apply(mode);
    }

    private void OnToggle(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var cfg = AppState.Config;
        cfg.AutoInstall = AutoInstall.IsChecked == true;
        cfg.AutoConnect = AutoConnect.IsChecked == true;
        cfg.EnableHwidSpoof = EnableHwidSpoof.IsChecked == true;
        Log.Verbose = Verbose.IsChecked == true;
        AppState.SaveConfig();
        AppState.Ui.CheckUpdateOnStart = CheckUpdate.IsChecked == true;
        AppState.Ui.Save();
    }

    private void OnGameDir(object sender, RoutedEventArgs e)
    {
        var dir = GameDirBox.Text.Trim();
        if (dir == AppState.Config.GameDir) return;
        AppState.Config.GameDir = dir;
        AppState.SaveConfig();
        Dialogs.Toast("游戏目录已保存", DialogKind.Success);
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择 .minecraft 目录" };
        if (Directory.Exists(GameDirBox.Text)) dialog.InitialDirectory = GameDirBox.Text;
        if (dialog.ShowDialog() != true) return;
        GameDirBox.Text = dialog.FolderName;
        OnGameDir(sender, e);
    }

    private void OnMemory(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(MemoryBox.Text.Trim(), out var mb) && mb is >= 512 and <= 65536)
        {
            if (mb == AppState.Config.MaxMemoryMb) return;
            AppState.Config.MaxMemoryMb = mb;
            AppState.SaveConfig();
            Dialogs.Toast($"最大内存已设为 {mb} MB", DialogKind.Success);
            return;
        }
        MemoryBox.Text = AppState.Config.MaxMemoryMb.ToString();
        Dialogs.Toast("内存需要在 512 到 65536 MB 之间", DialogKind.Warning);
    }
}
