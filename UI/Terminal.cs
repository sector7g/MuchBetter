// (c) 2017-2026 Sector 7G Consulting LLC
using System;
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
            if (value.Length > width - Length)
            {
                value = value.Substring(0, width - Length);
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
            Length += value.Length;
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
}
