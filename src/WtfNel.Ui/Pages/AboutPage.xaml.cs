using System.Windows;
using System.Windows.Controls;
using WtfNel.Ui.Services;

namespace WtfNel.Ui.Pages;

/// <summary>About page: current version, manual update check against GitHub Releases and project links.</summary>
public partial class AboutPage : UserControl
{
    private ReleaseInfo? _release;

    public AboutPage()
    {
        InitializeComponent();
        VersionText.Text = "版本 " + UpdateService.CurrentText;
        RepoText.Text = $"{UpdateService.Owner}/{UpdateService.Repo}";
        StatusText.Text = "尚未检查";
    }

    private async void OnCheck(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        CheckText.Text = "检查中…";
        Checking.Visibility = Visibility.Visible;
        try
        {
            _release = MainWindow.Instance is { } window ? await window.CheckUpdateAsync(manual: true) : await UpdateService.CheckAsync();
            ShowRelease();
            UpdateHeadline.Text = _release is null ? "已是最新版本" : $"有可用更新：{_release.Tag}";
            StatusText.Text = $"上次检查 {DateTime.Now:HH:mm}";
        }
        finally
        {
            Checking.Visibility = Visibility.Collapsed;
            CheckButton.IsEnabled = true;
            CheckText.Text = "检查更新";
        }
    }

    private void ShowRelease()
    {
        ReleaseCard.Visibility = _release is null ? Visibility.Collapsed : Visibility.Visible;
        if (_release is null) return;
        ReleaseTitle.Text = _release.Name;
        ReleaseMeta.Text = $"{_release.Tag} · {_release.PublishedAt:yyyy-MM-dd}" +
                           (_release.AssetName is null ? "" : $" · {_release.AssetName} ({_release.AssetSize / 1024.0 / 1024.0:0.0} MB)");
        ReleaseNotes.Text = string.IsNullOrWhiteSpace(_release.Notes) ? "这个版本没有填写更新说明。" : _release.Notes.Trim();
        DownloadText.Text = _release.AssetUrl is null ? "前往下载" : "下载";
    }

    private void OnDownload(object sender, RoutedEventArgs e)
    {
        if (_release is not null) UpdateService.Open(_release.AssetUrl ?? _release.PageUrl);
    }

    private void OnOpenRelease(object sender, RoutedEventArgs e)
    {
        if (_release is not null) UpdateService.Open(_release.PageUrl);
    }

    private void OnRepo(object sender, RoutedEventArgs e) => UpdateService.Open(UpdateService.RepoUrl);

    private void OnReleases(object sender, RoutedEventArgs e) => UpdateService.Open(UpdateService.ReleasesUrl);

    private void OnFolder(object sender, RoutedEventArgs e) => UpdateService.Open(AppContext.BaseDirectory);
}
