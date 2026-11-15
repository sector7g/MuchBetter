// (c) 2017-2026 Sector 7G Consulting LLC
using System;
using System.Collections.Generic;
using System.Linq;
using S7G.mbpm.Interop;

namespace S7G.mbpm.UI
{
    /// <summary>
    /// A color scheme for the view, optionally with a console font. F3 (or T) cycles through them;
    /// --theme picks one at startup. Colors work in any VT console. The font can only be changed in
    /// the classic console window (conhost): Windows Terminal keeps fonts in its own settings and
    /// ignores requests from programs, so there the theme changes colors only.
    /// </summary>
    internal sealed class Theme
    {
        public string Name;
        public string FontFace;         // null: leave the font alone
        public short FontSize;          // character height in pixels

        // null in the HTOP theme: the console's own default colors.
        public string Text;             // base text color (RGB "r;g;b")
        public string Background;       // base background color
        public string Dim;
        public string Low;              // < 25 %
        public string Mid;              // < 60 %
        public string High;             // >= 60 %
        public string Accent;           // labels and headings
        public string BadgeText, BadgeBack;
        public string HeaderText, HeaderBack;
        public string SelectedText, SelectedBack;
        public string KeyText, KeyBack;

        // Sector 7G is the default; F3 cycles in this order.
        public static readonly List<Theme> All = new List<Theme>
        {
            new Theme
            {
                Name = "Sector 7G",
                FontFace = "Cascadia Code", FontSize = 20,
                Text = "245;245;245", Background = "28;28;28", Dim = "128;128;128",
                Low = "160;200;120", Mid = "230;190;90", High = "230;90;80", Accent = "200;200;200",
                BadgeText = "245;245;245", BadgeBack = "48;48;48",
                HeaderText = "28;28;28", HeaderBack = "245;245;245",
                SelectedText = "245;245;245", SelectedBack = "72;72;72",
                KeyText = "28;28;28", KeyBack = "200;200;200",
            },
            new Theme
            {
                // Greyscale only: load shows as brightness, from grey to white.
                Name = "Mono",
                Text = "220;220;220", Background = "18;18;18", Dim = "120;120;120",
                Low = "170;170;170", Mid = "210;210;210", High = "255;255;255", Accent = "235;235;235",
                BadgeText = "18;18;18", BadgeBack = "220;220;220",
                HeaderText = "18;18;18", HeaderBack = "190;190;190",
                SelectedText = "255;255;255", SelectedBack = "70;70;70",
                KeyText = "18;18;18", KeyBack = "160;160;160",
            },
            new Theme
            {
                Name = "Metro Night",
                FontFace = "Consolas", FontSize = 18,
                Text = "230;230;230", Background = "32;32;32", Dim = "140;140;140",
                Low = "108;203;95", Mid = "252;225;0", High = "247;99;12", Accent = "96;205;255",
                BadgeText = "0;0;0", BadgeBack = "96;205;255",
                HeaderText = "0;0;0", HeaderBack = "96;205;255",
                SelectedText = "255;255;255", SelectedBack = "0;90;158",
                KeyText = "230;230;230", KeyBack = "60;60;60",
            },
            new Theme
            {
                Name = "HTOP",      // the console's own colors, as htop draws them
            },
        };

        public static Theme Default
        {
            get { return All[0]; }
        }

        public static Theme Current { get; private set; }

        static Theme()
        {
            Current = All[0];
        }

        public static Theme Find(string name)
        {
            return All.FirstOrDefault(t => string.Equals(t.Name.Replace(" ", string.Empty), (name ?? string.Empty).Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase));
        }

        public static Theme Next()
        {
            return All[(All.IndexOf(Current) + 1) % All.Count];
        }

        /// <summary>Sets the view's colors. Returns what happened to the font (for a notice).</summary>
        public static FontResult Apply(Theme theme)
        {
            Current = theme;
            if (theme.Text == null)
            {
                Ansi.Reset = Ansi.PlainReset;
                Ansi.Dim = "\x1b[90m";
                Ansi.Red = "\x1b[31m";
                Ansi.Green = "\x1b[32m";
                Ansi.Yellow = "\x1b[33m";
                Ansi.Cyan = "\x1b[36m";
                Ansi.Badge = "\x1b[1;30;46m";
                Ansi.HeaderBar = "\x1b[30;42m";
                Ansi.Selected = "\x1b[30;46m";
                Ansi.KeyLabel = "\x1b[30;46m";
            }
            else
            {
                Ansi.Reset = Ansi.PlainReset + Pair(theme.Text, theme.Background);
                Ansi.Dim = Fore(theme.Dim);
                Ansi.Red = Fore(theme.High);
                Ansi.Green = Fore(theme.Low);
                Ansi.Yellow = Fore(theme.Mid);
                Ansi.Cyan = Fore(theme.Accent);
                Ansi.Badge = "\x1b[1m" + Pair(theme.BadgeText, theme.BadgeBack);
                Ansi.HeaderBar = Pair(theme.HeaderText, theme.HeaderBack);
                Ansi.Selected = Pair(theme.SelectedText, theme.SelectedBack);
                Ansi.KeyLabel = Pair(theme.KeyText, theme.KeyBack);
            }
            return ConsoleFont.Set(theme.FontFace, theme.FontSize);
        }

        private static string Fore(string rgb)
        {
            return "\x1b[38;2;" + rgb + "m";
        }

        private static string Pair(string text, string back)
        {
            return "\x1b[38;2;" + text + ";48;2;" + back + "m";
        }
    }

    internal enum FontResult
    {
        Unchanged,          // the theme keeps the current font
        Changed,
        NotSupported,       // Windows Terminal or another host that doesn't take font requests
        NotInstalled,       // the console didn't accept the face
    }

    /// <summary>
    /// The classic console window's font. The font in use at the first change is remembered and put
    /// back on exit, so a theme never leaves the console changed.
    /// </summary>
    internal static class ConsoleFont
    {
        private static NativeMethods.CONSOLE_FONT_INFOEX original;
        private static bool saved;

        /// <summary>True in Windows Terminal, which ignores font requests from programs.</summary>
        public static bool HostManagesFonts
        {
            get { return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION")); }
        }

        public static FontResult Set(string face, short size)
        {
            if (face == null)
            {
                return saved ? Restore() : FontResult.Unchanged;
            }
            if (HostManagesFonts)
            {
                return FontResult.NotSupported;
            }

            IntPtr handle = NativeMethods.GetStdHandle(NativeMethods.STD_OUTPUT_HANDLE);
            var current = NativeMethods.CONSOLE_FONT_INFOEX.Create();
            if (!NativeMethods.GetCurrentConsoleFontEx(handle, false, ref current))
            {
                return FontResult.NotSupported;
            }
            if (!saved)
            {
                original = current;
                saved = true;
            }

            var font = NativeMethods.CONSOLE_FONT_INFOEX.Create();
            font.FontFamily = 54;            // FF_MODERN | TMPF_TRUETYPE | fixed pitch
            font.FontWeight = 400;
            font.dwFontSizeX = 0;
            font.dwFontSizeY = size;
            font.FaceName = face;
            if (!NativeMethods.SetCurrentConsoleFontEx(handle, false, ref font))
            {
                return FontResult.NotSupported;
            }

            // The console substitutes a face it doesn't have; check what it actually took.
            var check = NativeMethods.CONSOLE_FONT_INFOEX.Create();
            NativeMethods.GetCurrentConsoleFontEx(handle, false, ref check);
            return string.Equals(check.FaceName, face, StringComparison.OrdinalIgnoreCase) ? FontResult.Changed : FontResult.NotInstalled;
        }

        /// <summary>Puts back the font in use before the first change. Safe to call more than once.</summary>
        public static FontResult Restore()
        {
            if (!saved)
            {
                return FontResult.Unchanged;
            }
            IntPtr handle = NativeMethods.GetStdHandle(NativeMethods.STD_OUTPUT_HANDLE);
            NativeMethods.CONSOLE_FONT_INFOEX font = original;
            NativeMethods.SetCurrentConsoleFontEx(handle, false, ref font);
            return FontResult.Changed;
        }
    }
}
