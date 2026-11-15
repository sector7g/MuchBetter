// (c) 2017-2026 Sector 7G Consulting LLC
using System;
using System.Globalization;
using System.IO;
using System.Text;
using S7G.mbpm.Interop;

namespace S7G.mbpm.UI
{
    /// <summary>
    /// ANSI/VT escape sequences used by the view. The colors are set by the current Theme (see
    /// Theme.Apply); the defaults here are the "HTOP" theme. Reset returns to the theme's base text
    /// and background colors, so erased line ends take the theme's background.
    /// </summary>
    internal static class Ansi
    {
        public const string PlainReset = "\x1b[0m";          // the console's own colors (used on exit)
        public const string Bold = "\x1b[1m";
        public const string EraseToEndOfLine = "\x1b[K";
        public const string MockBadge = "\x1b[1;97;41m";     // bold white on red, in every theme

        public static string Reset = PlainReset;
        public static string Dim = "\x1b[90m";
        public static string Red = "\x1b[31m";
        public static string Green = "\x1b[32m";
        public static string Yellow = "\x1b[33m";
        public static string Cyan = "\x1b[36m";               // accent: labels and headings
        public static string Badge = "\x1b[1;30;46m";         // bold black on cyan
        public static string HeaderBar = "\x1b[30;42m";       // black on green
        public static string Selected = "\x1b[30;46m";        // black on cyan
        public static string KeyLabel = "\x1b[30;46m";        // black on cyan

        /// <summary>Green under 25%, yellow under 60%, red above.</summary>
        public static string Level(double? percent)
        {
            if (!percent.HasValue)
            {
                return Dim;
            }
            return percent.Value >= 60 ? Red : (percent.Value >= 25 ? Yellow : Green);
        }
    }

    /// <summary>
    /// The console as a full-screen VT surface: each frame is built as one string and written in a
    /// single call, from the top-left of the window, so nothing scrolls and nothing half-drawn shows.
    /// </summary>
    internal static class Terminal
    {
        private static Stream output;
        private static bool alternate;
        private static bool active;

        /// <summary>Turns on VT processing (and the alternate screen). False if the console can't do VT.</summary>
        public static bool Enter(bool useAlternateScreen)
        {
            IntPtr handle = NativeMethods.GetStdHandle(NativeMethods.STD_OUTPUT_HANDLE);
            uint mode;
            if (!NativeMethods.GetConsoleMode(handle, out mode)
                || !NativeMethods.SetConsoleMode(handle, mode | NativeMethods.ENABLE_VIRTUAL_TERMINAL_PROCESSING))
            {
                return false;
            }

            Console.OutputEncoding = new UTF8Encoding(false);
            output = Console.OpenStandardOutput();
            alternate = useAlternateScreen;
            active = true;

            Write((alternate ? "\x1b[?1049h" : string.Empty) + "\x1b[?25l\x1b[2J");   // alt screen, hide cursor, clear
            return true;
        }

        public static void Draw(string frame)
        {
            if (active)
            {
                Write("\x1b[H" + frame + Ansi.Reset + "\x1b[J");   // home, frame, clear anything below
            }
        }

        /// <summary>Restores the cursor and the normal screen. Safe to call more than once.</summary>
        public static void Leave()
        {
            if (!active)
            {
                return;
            }
            active = false;
            ConsoleFont.Restore();      // a theme's font never outlives MBPM
            Write(Ansi.PlainReset + "\x1b[?25h" + (alternate ? "\x1b[?1049l" : "\r\n"));
        }

        public static int Width
        {
            get
            {
                try { return Console.WindowWidth; }
                catch (IOException) { return 120; }
            }
        }

        public static int Height
        {
            get
            {
                try { return Console.WindowHeight; }
                catch (IOException) { return 40; }
            }
        }

        private static void Write(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            output.Write(bytes, 0, bytes.Length);
            output.Flush();
        }
    }

    /// <summary>
    /// Builds one screen line with colors, tracking the visible width so it never exceeds the window
    /// (a line that wraps would scroll the screen).
    /// </summary>
    internal sealed class LineBuilder
    {
        private readonly StringBuilder text = new StringBuilder();
        private readonly int width;

        public LineBuilder(int width)
        {
            this.width = width;
        }

        public int Length { get; private set; }

        public LineBuilder Add(string value, string style = null)
        {
            if (string.IsNullOrEmpty(value) || Length >= width)
            {
                return this;
            }
            int columns = TextWidth.Of(value);
            if (columns > width - Length)
            {
                value = TextWidth.Fit(value, width - Length);
                columns = TextWidth.Of(value);
            }

            if (style != null)
            {
                text.Append(style);
            }
            text.Append(value);
            if (style != null)
            {
                text.Append(Ansi.Reset);
            }
            Length += columns;
            return this;
        }

        /// <summary>Adds text that already contains escape codes, with its visible width; skipped if it doesn't fit.</summary>
        public LineBuilder AddRaw(string text, int visibleWidth)
        {
            if (string.IsNullOrEmpty(text) || Length + visibleWidth > width)
            {
                return this;
            }
            this.text.Append(text).Append(Ansi.Reset);
            Length += visibleWidth;
            return this;
        }

        public LineBuilder Append(LineBuilder other)
        {
            text.Append(other.text);
            Length += other.Length;
            return this;
        }

        public LineBuilder PadTo(int column, string style = null)
        {
            column = Math.Min(column, width);
            if (column > Length)
            {
                Add(new string(' ', column - Length), style);
            }
            return this;
        }

        public LineBuilder Fill(string style = null)
        {
            return PadTo(width, style);
        }

        public override string ToString()
        {
            return text.ToString();
        }
    }

    /// <summary>
    /// Text width in terminal columns, not characters: East Asian wide and fullwidth characters
    /// (CJK, Hangul, fullwidth forms) and most emoji take two columns; combining marks, zero-width
    /// characters and variation selectors take none; a surrogate pair is one character.
    /// Approximates Unicode East Asian Width, which is what Windows Terminal and conhost follow.
    /// </summary>
    internal static class TextWidth
    {
        public static int Of(string text)
        {
            int width = 0;
            for (int i = 0; i < (text ?? string.Empty).Length; i += Step(text, i))
            {
                width += Columns(text, i);
            }
            return width;
        }

        /// <summary>The longest start of the text that fits in the given columns; combining marks stay with their character.</summary>
        public static string Fit(string text, int columns)
        {
            if (string.IsNullOrEmpty(text) || columns <= 0)
            {
                return string.Empty;
            }
            int width = 0;
            int i = 0;
            while (i < text.Length)
            {
                int w = Columns(text, i);
                if (width + w > columns)
                {
                    break;
                }
                width += w;
                i += Step(text, i);
            }
            return text.Substring(0, i);
        }

        /// <summary>Cuts the text to the given columns with an ellipsis, padding a column a wide character couldn't fill.</summary>
        public static string Truncate(string text, int columns)
        {
            text = text ?? string.Empty;
            if (Of(text) <= columns)
            {
                return text;
            }
            string start = Fit(text, Math.Max(0, columns - 1));
            return PadRight(start, Math.Max(0, columns - 1)) + "…";
        }

        public static string PadRight(string text, int columns)
        {
            int width = Of(text);
            return width >= columns ? text : text + new string(' ', columns - width);
        }

        public static string PadLeft(string text, int columns)
        {
            int width = Of(text);
            return width >= columns ? text : new string(' ', columns - width) + text;
        }

        private static int Step(string text, int i)
        {
            return char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
        }

        private static int Columns(string text, int i)
        {
            int code = Step(text, i) == 2 ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i];
            if (code < 0x20 || code == 0x200B || code == 0x200C || code == 0x200D || code == 0x2060 || code == 0xFEFF)
            {
                return 0;
            }
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(text, i);     // surrogate-pair aware
            if (category == UnicodeCategory.NonSpacingMark || category == UnicodeCategory.EnclosingMark
                || category == UnicodeCategory.Format)
            {
                return 0;
            }
            return IsWide(code) ? 2 : 1;
        }

        private static bool IsWide(int c)
        {
            return (c >= 0x1100 && c <= 0x115F)         // Hangul Jamo initials
                || (c >= 0x2E80 && c <= 0x303E)         // CJK radicals, punctuation
                || (c >= 0x3041 && c <= 0x33FF)         // Hiragana, Katakana, CJK compatibility
                || (c >= 0x3400 && c <= 0x4DBF)         // CJK extension A
                || (c >= 0x4E00 && c <= 0x9FFF)         // CJK unified ideographs
                || (c >= 0xA000 && c <= 0xA4CF)         // Yi
                || (c >= 0xAC00 && c <= 0xD7A3)         // Hangul syllables
                || (c >= 0xF900 && c <= 0xFAFF)         // CJK compatibility ideographs
                || (c >= 0xFE30 && c <= 0xFE4F)         // CJK compatibility forms
                || (c >= 0xFF00 && c <= 0xFF60)         // fullwidth forms
                || (c >= 0xFFE0 && c <= 0xFFE6)
                || (c >= 0x1F300 && c <= 0x1F64F)       // emoji: symbols, pictographs, emoticons
                || (c >= 0x1F680 && c <= 0x1F6FF)       // emoji: transport, map
                || (c >= 0x1F900 && c <= 0x1F9FF)       // emoji: supplemental
                || (c >= 0x20000 && c <= 0x3FFFD);      // CJK extensions B and beyond
        }
    }
}
