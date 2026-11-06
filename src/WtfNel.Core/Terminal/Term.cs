namespace WtfNel.Core.Terminal;

/// <summary>Terminal capability detection.</summary>
public static class Term
{
    /// <summary>
    /// Legacy consoles on Chinese systems (conhost with CJK fonts) render ambiguous-width glyphs such as
    /// ▀ █ · as two cells wide. Detected once at startup by writing a ▀, measuring how far the cursor
    /// advances, then erasing it.
    /// </summary>
    public static bool AmbiguousWide { get; private set; }

    public static void Detect()
    {
        if (Console.IsOutputRedirected) return;
        try
        {
            var (left, top) = (Console.CursorLeft, Console.CursorTop);
            Console.Out.Write("▀");
            Console.Out.Flush();
            AmbiguousWide = Console.CursorLeft - left >= 2;
            Console.SetCursorPosition(left, top);
            Console.Out.Write("  ");
            Console.SetCursorPosition(left, top);
        }
        catch { AmbiguousWide = false; }
    }
}
