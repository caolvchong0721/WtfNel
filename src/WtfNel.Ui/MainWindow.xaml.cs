using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using WtfNel.Core.Logging;
using WtfNel.Ui.Controls;
using WtfNel.Ui.Pages;
using WtfNel.Ui.Services;

namespace WtfNel.Ui;

/// <summary>Shell window: sidebar navigation, custom title bar, update banner and the dialog/toast overlay layers.</summary>
public partial class MainWindow : Window
{
    private readonly Dictionary<string, UserControl> _pages = new();
    private ReleaseInfo? _release;
    private UserControl? _current;

    public static MainWindow? Instance { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        Instance = this;
        Dialogs.Attach(DialogLayer, ToastLayer);
        ThemeService.Changed += ApplyWindowFrame;
        AppState.RunningChanged += () => Dispatcher.BeginInvoke(() =>
            RunningBadge.Visibility = AppState.IsRunning ? Visibility.Visible : Visibility.Collapsed);

        StateChanged += (_, _) =>
        {
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaxIcon.Data = Dialogs.Geo(WindowState == WindowState.Maximized ? "I.Restore" : "I.Maximize");
        };
        SourceInitialized += (_, _) => ApplyWindowFrame();
        Loaded += async (_, _) =>
        {
            NavHome.IsChecked = true;
            if (AppState.Ui.CheckUpdateOnStart) await CheckUpdateAsync(manual: false);
        };
        Closing += OnClosing;
    }

    public void Navigate(string key)
    {
        var target = key switch
        {
            "accounts" => NavAccounts,
            "logs" => NavLogs,
            "settings" => NavSettings,
            "about" => NavAbout,
            _ => NavHome,
        };
        if (target.IsChecked == true) Show(key);
        else target.IsChecked = true;
    }

    /// <summary>Queries GitHub Releases; shows the banner and a dialog for a new version, and reports the result when checked manually.</summary>
    public async Task<ReleaseInfo?> CheckUpdateAsync(bool manual)
    {
        try
        {
            var release = await UpdateService.CheckAsync();
            _release = release;
            UpdateBanner.Visibility = release is null ? Visibility.Collapsed : Visibility.Visible;
            if (release is null)
            {
                if (manual) Dialogs.Toast($"已是最新版本 v{UpdateService.CurrentText}", DialogKind.Success);
                return null;
            }
            UpdateTitle.Text = $"新版本 {release.Tag} 可用";
            if (manual || AppState.Ui.SkippedVersion != release.Tag) await ShowUpdateDialogAsync(release);
            return release;
        }
        catch (Exception ex)
        {
            Log.Debug("[update] 检查更新失败 " + ex);
            if (manual) await Dialogs.AlertAsync("检查更新失败", ex.GetBaseException().Message, DialogKind.Error);
            return null;
        }
    }

    public async Task ShowUpdateDialogAsync(ReleaseInfo release)
    {
        var notes = string.IsNullOrWhiteSpace(release.Notes) ? "这个版本没有填写更新说明。" : release.Notes.Trim();
        var body = new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = $"当前 v{UpdateService.CurrentText}  →  最新 {release.Tag} · {release.PublishedAt:yyyy-MM-dd}",
                    Foreground = Dialogs.Res("TextSecondary"),
                    FontSize = 12.5,
                    Margin = new Thickness(0, 0, 0, 12),
                },
                new Border
                {
                    Background = Dialogs.Res("SubtleFill"),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(14, 12, 14, 12),
                    Child = new ScrollViewer
                    {
                        MaxHeight = 280,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        Content = new TextBlock { Text = notes, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 20, Foreground = Dialogs.Res("TextPrimary") },
                    },
                },
            },
        };
        var pick = await Dialogs.ShowAsync(release.Name, body, ["忽略此版本", "稍后", "前往下载"], DialogKind.Info, width: 520);
        switch (pick)
        {
            case 0:
                AppState.Ui.SkippedVersion = release.Tag;
                AppState.Ui.Save();
                break;
            case 2:
                UpdateService.Open(release.AssetUrl ?? release.PageUrl);
                break;
        }
    }

    private void OnUpdateBannerClick(object sender, MouseButtonEventArgs e)
    {
        if (_release is not null) _ = ShowUpdateDialogAsync(_release);
    }

    private void OnNav(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string key }) Show(key);
    }

    private void Show(string key)
    {
        if (!_pages.TryGetValue(key, out var page))
        {
            page = key switch
            {
                "accounts" => new AccountsPage(),
                "logs" => new LogsPage(),
                "settings" => new SettingsPage(),
                "about" => new AboutPage(),
                _ => new HomePage(),
            };
            _pages[key] = page;
            PageHost.Children.Add(page);
        }
        if (_current == page) return;
        _current = page;
        foreach (var other in _pages.Values) other.Visibility = other == page ? Visibility.Visible : Visibility.Collapsed;

        PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = CubicBezierEase.Standard });
        var grow = new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(400)) { EasingFunction = CubicBezierEase.EmphasizedDecelerate };
        PageScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, grow);
        PageScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, grow);
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!AppState.IsRunning) return;
        e.Cancel = true;
        if (!await Dialogs.ConfirmAsync("游戏还在运行", "关闭启动器会同时结束游戏进程。确定要退出吗？", "退出", danger: true)) return;
        AppState.StopGame();
        Closing -= OnClosing;
        Close();
    }

    private void ApplyWindowFrame()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int corner = 2;
        DwmSetWindowAttribute(hwnd, 33, ref corner, sizeof(int));
        int dark = ThemeService.IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
