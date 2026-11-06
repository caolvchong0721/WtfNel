using System.Runtime.InteropServices;
using Spectre.Console;
using WtfNel.Core.Terminal;

namespace WtfNel.Tui;

/// <summary>
/// A mouse-clickable selection menu: hover highlights, left click selects; arrow keys + Enter and number
/// keys also work. Mouse events are read via ReadConsoleInput (quick-edit mode is temporarily disabled
/// while the menu is shown, otherwise a click would select console text instead).
/// </summary>
public static class ClickMenu
{
    private static int? _anchorTop;

    public static void ResetAnchor()
    {
        try { _anchorTop = Console.CursorTop; }
        catch { _anchorTop = null; }
    }

    private static void RewindToAnchor()
    {
        if (Console.IsOutputRedirected) return;
        try
        {
            if (_anchorTop is null) { _anchorTop = Console.CursorTop; return; }

            int anchor = _anchorTop.Value;
            if (anchor < Console.WindowTop || Console.CursorTop - anchor > Math.Max(4, Console.WindowHeight - 4))
            {
                AnsiConsole.Clear();
                anchor = 0;
                _anchorTop = 0;
            }

            Console.SetCursorPosition(0, Math.Clamp(anchor, 0, Math.Max(0, Console.BufferHeight - 1)));
            Console.Write("\x1b[J");
        }
        catch { _anchorTop = null; }
    }

    public static T Select<T>(string title, IReadOnlyList<T> items, Func<T, string> label)
    {
        if (items.Count == 0) throw new ArgumentException("菜单为空", nameof(items));
        RewindToAnchor();
        AnsiConsole.MarkupLine(title);

        var width = Math.Max(20, Console.WindowWidth - 1);
        var lines = items.Select(i => Fit(label(i), width - 3)).ToArray();
        foreach (var l in lines) AnsiConsole.MarkupLine("   " + l);
        var top = Console.CursorTop - lines.Length;

        var index = 0;
        void Draw(int i, bool on)
        {
            Console.SetCursorPosition(0, top + i);
            AnsiConsole.Markup(on ? $"[mediumpurple bold]❯[/]  [on grey19]{lines[i]}[/]" : "   " + lines[i]);
            Console.Write("\x1b[K");
        }
        void Move(int to)
        {
            if (to == index) return;
            Draw(index, false);
            index = to;
            Draw(index, true);
        }

        Console.CursorVisible = false;
        Draw(0, true);
        var chosen = -1;
        using (new MouseMode())
        {
            while (chosen < 0)
            {
                var rec = ReadInput();
                if (rec.EventType == MOUSE_EVENT)
                {
                    var m = rec.Mouse;
                    var row = m.Y - top;
                    if (row < 0 || row >= lines.Length) continue;
                    if (m.EventFlags == MOUSE_MOVED) Move(row);
                    else if (m.EventFlags == 0 && (m.ButtonState & FROM_LEFT_1ST_BUTTON_PRESSED) != 0) { Move(row); chosen = row; }
                }
                else if (rec.EventType == KEY_EVENT && rec.Key.KeyDown != 0)
                {
                    switch (rec.Key.VirtualKeyCode)
                    {
                        case 0x26: Move((index + lines.Length - 1) % lines.Length); break;
                        case 0x28: Move((index + 1) % lines.Length); break;
                        case 0x0D: chosen = index; break;
                        default:
                            var c = rec.Key.UnicodeChar;
                            if (c >= '1' && c <= '9' && c - '1' < lines.Length) { Move(c - '1'); chosen = index; }
                            break;
                    }
                }
            }
        }

        for (var i = 0; i < lines.Length; i++)
        {
            Console.SetCursorPosition(0, top + i);
            Console.Write("\x1b[K");
        }
        Console.SetCursorPosition(0, top);
        AnsiConsole.MarkupLine($"   [mediumpurple]›[/] {lines[chosen]}");
        Console.SetCursorPosition(0, top + 1);
        Console.CursorVisible = true;
        return items[chosen];
    }

    public static bool Confirm(string question, bool defaultYes = false)
    {
        var yes = "[green]是[/]";
        var no = "[red]否[/]";
        var pick = Select(question, defaultYes ? new[] { yes, no } : new[] { no, yes }, s => s);
        return pick == yes;
    }

    /// <summary>Truncates to a display width (CJK counts as 2 columns) so a wrapped line never shifts click row numbers.</summary>
    private static string Fit(string markup, int max)
    {
        var plain = Markup.Remove(markup);
        if (Width(plain) <= max) return markup;
        var sb = new System.Text.StringBuilder();
        var w = 0;
        foreach (var ch in plain)
        {
            var cw = CharWidth(ch);
            if (w + cw > max - 1) break;
            sb.Append(ch);
            w += cw;
        }
        return Markup.Escape(sb.ToString()) + "…";
    }

    private static int Width(string s) => s.Sum(CharWidth);

    private static int CharWidth(char c) =>
        Term.AmbiguousWide && (c == '·' || c is >= '‐' and <= '➿') ? 2 :
        c >= 0x1100 && (c <= 0x115F || c is >= '⺀' and <= '꓏' || c is >= '가' and <= '힣' ||
                        c is >= '豈' and <= '﫿' || c is >= '︰' and <= '﹏' || c is >= '＀' and <= '｠' ||
                        c is >= '￠' and <= '￦') ? 2 : 1;

    private const ushort KEY_EVENT = 1, MOUSE_EVENT = 2;
    private const uint FROM_LEFT_1ST_BUTTON_PRESSED = 1, MOUSE_MOVED = 1;
    private const uint ENABLE_MOUSE_INPUT = 0x10, ENABLE_QUICK_EDIT_MODE = 0x40, ENABLE_EXTENDED_FLAGS = 0x80,
        ENABLE_VIRTUAL_TERMINAL_INPUT = 0x200, ENABLE_PROCESSED_INPUT = 0x1, ENABLE_LINE_INPUT = 0x2, ENABLE_ECHO_INPUT = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyRecord
    {
        public int KeyDown;
        public ushort RepeatCount, VirtualKeyCode, VirtualScanCode;
        public char UnicodeChar;
        public uint ControlKeyState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseRecord
    {
        public short X, Y;
        public uint ButtonState, ControlKeyState, EventFlags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 20)]
    private struct InputRecord
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public KeyRecord Key;
        [FieldOffset(4)] public MouseRecord Mouse;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleMode(IntPtr h, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleMode(IntPtr h, uint mode);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ReadConsoleInputW")]
    private static extern bool ReadConsoleInput(IntPtr h, out InputRecord rec, uint len, out uint read);

    private static IntPtr StdIn => GetStdHandle(-10);

    private static InputRecord ReadInput()
    {
        if (!ReadConsoleInput(StdIn, out var rec, 1, out _)) throw new InvalidOperationException("读取控制台输入失败");
        return rec;
    }

    /// <summary>While the menu is shown: enable mouse input, disable quick-edit / VT input / line buffering; restore the original mode afterwards.</summary>
    private sealed class MouseMode : IDisposable
    {
        private readonly uint _old;

        public MouseMode()
        {
            GetConsoleMode(StdIn, out _old);
            var mode = (_old | ENABLE_MOUSE_INPUT | ENABLE_EXTENDED_FLAGS)
                       & ~(ENABLE_QUICK_EDIT_MODE | ENABLE_VIRTUAL_TERMINAL_INPUT | ENABLE_LINE_INPUT | ENABLE_ECHO_INPUT);
            mode |= ENABLE_PROCESSED_INPUT;
            SetConsoleMode(StdIn, mode);
        }

        public void Dispose() => SetConsoleMode(StdIn, _old | ENABLE_EXTENDED_FLAGS);
    }
}
