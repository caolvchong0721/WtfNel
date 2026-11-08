using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WtfNel.Ui.Services;

namespace WtfNel.Ui.Pages;

/// <summary>Live log viewer with level filter, text search and auto-scroll.</summary>
public partial class LogsPage : UserControl
{
    private readonly ICollectionView _view;
    private string _level = "";
    private string _search = "";

    public LogsPage()
    {
        InitializeComponent();
        _view = CollectionViewSource.GetDefaultView(LogBuffer.Entries);
        _view.Filter = Matches;
        List.ItemsSource = _view;
        LogBuffer.Entries.CollectionChanged += OnEntries;
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) { UpdateEmpty(); ScrollToEnd(); } };
    }

    private bool Matches(object item)
    {
        if (item is not LogEntry entry) return false;
        if (_level.Length > 0 && entry.Level != _level) return false;
        if (_level.Length == 0 && entry.Level == "DBG") return false;
        if (_search.Length == 0) return true;
        return entry.Message.Contains(_search, StringComparison.OrdinalIgnoreCase)
               || entry.Tag.Contains(_search, StringComparison.OrdinalIgnoreCase);
    }

    private void OnEntries(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!IsVisible) return;
        UpdateEmpty();
        if (e.Action == NotifyCollectionChangedAction.Add) ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        if (AutoScroll.IsChecked != true || List.Items.Count == 0) return;
        List.ScrollIntoView(List.Items[^1]);
    }

    private void UpdateEmpty() => EmptyText.Visibility = List.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnLevel(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string level }) return;
        _level = level;
        if (_view is null) return;
        _view.Refresh();
        UpdateEmpty();
        ScrollToEnd();
    }

    private void OnSearch(object sender, TextChangedEventArgs e)
    {
        _search = SearchBox.Text.Trim();
        _view.Refresh();
        UpdateEmpty();
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        LogBuffer.Entries.Clear();
        UpdateEmpty();
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }
}
