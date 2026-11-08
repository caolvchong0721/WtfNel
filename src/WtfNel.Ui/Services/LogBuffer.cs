using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using WtfNel.Core.Logging;

namespace WtfNel.Ui.Services;

/// <summary>One log line as shown in the log page.</summary>
public sealed record LogEntry(DateTime Time, string Level, string Tag, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");
    public bool HasTag => Tag.Length > 0;
}

/// <summary>Collects <see cref="Log"/> output on the UI thread in batches, capped to the most recent entries.</summary>
public static partial class LogBuffer
{
    private const int Capacity = 4000;
    private static readonly object Gate = new();
    private static readonly List<LogEntry> Pending = new();
    private static bool _scheduled;

    public static ObservableCollection<LogEntry> Entries { get; } = new();

    public static event Action<string?>? StatusChanged;

    public static void Attach()
    {
        Log.Written += OnWritten;
        Log.StatusChanged += s => Application.Current?.Dispatcher.BeginInvoke(() => StatusChanged?.Invoke(s));
    }

    private static void OnWritten(string level, DateTime time, string message)
    {
        var match = TagRegex().Match(message);
        var entry = match.Success
            ? new LogEntry(time, level, match.Groups[1].Value, message[match.Length..])
            : new LogEntry(time, level, "", message);
        lock (Gate)
        {
            Pending.Add(entry);
            if (_scheduled) return;
            _scheduled = true;
        }
        Application.Current?.Dispatcher.BeginInvoke(Flush, DispatcherPriority.Background);
    }

    private static void Flush()
    {
        List<LogEntry> batch;
        lock (Gate)
        {
            batch = new List<LogEntry>(Pending);
            Pending.Clear();
            _scheduled = false;
        }
        foreach (var entry in batch) Entries.Add(entry);
        while (Entries.Count > Capacity) Entries.RemoveAt(0);
    }

    [GeneratedRegex(@"^\[([^\]]{1,24})\]\s*")]
    private static partial Regex TagRegex();
}
