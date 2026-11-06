using Spectre.Console;

namespace WtfNel.Core.Logging;

/// <summary>
/// Unified, timestamped logging to both the console and a file, with support for a single
/// status line pinned to the bottom of the console (used for download progress).
/// </summary>
public static class Log
{
    private const string ClearLine = "\r\u001b[K";
    private static readonly object Gate = new();
    private static StreamWriter? _file;
    private static string? _status;
    public static bool Verbose { get; set; }

    public static void Open(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _file = new StreamWriter(path, append: true) { AutoFlush = true };
        _file.WriteLine($"---- 会话开始 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ----");
    }

    /// <summary>Always written to the file; shown on the console only when <see cref="Verbose"/> is set.</summary>
    public static void Debug(string msg) => Write("DBG", msg);
    public static void Info(string msg) => Write("INF", msg);
    public static void Warn(string msg) => Write("WRN", msg);
    public static void Error(string msg) => Write("ERR", msg);

    /// <summary>Writes the game's own output verbatim to the file, bypassing the console prefix handling.</summary>
    public static void Raw(string line)
    {
        lock (Gate) { _file?.WriteLine(line); }
        Console.WriteLine(line);
    }

    private static void Write(string level, string msg)
    {
        var now = DateTime.Now;
        var line = $"{now:HH:mm:ss.fff} [{level}] {msg}";
        var color = level switch { "WRN" => "yellow", "ERR" => "red", "DBG" => "grey", _ => "default" };
        lock (Gate)
        {
            if (level != "DBG" || Verbose)
            {
                if (_status is not null) Console.Write(ClearLine);
                AnsiConsole.MarkupLine($"[grey]{now:HH:mm:ss}[/] [{color}]{Markup.Escape(msg)}[/]");
                if (_status is not null) AnsiConsole.Markup(_status);
            }
            _file?.WriteLine(line);
        }
    }

    /// <summary>Sets or refreshes the pinned bottom status line (markup, redrawn in place); null clears it.</summary>
    public static void Status(string? markup)
    {
        lock (Gate)
        {
            Console.Write(ClearLine);
            _status = markup;
            if (markup is not null) AnsiConsole.Markup(markup);
        }
    }

    public static void Close()
    {
        lock (Gate) { _file?.Dispose(); _file = null; }
    }
}
