// (c) 2017-2026 Sector 7G Consulting LLC
using System;
using System.Collections.Generic;
using System.Text;

namespace S7G.mbpm.UI
{
    /// <summary>
    /// The startup screen, compact and anchored top-left: the small Sector 7G logo with the
    /// product name, version, copyright and a status line beside it (below it when the window is
    /// narrow). Shown while MBPM connects to the target.
    /// </summary>
    internal static class Splash
    {
        private const int LogoColumns = 28;    // the smallest logo: 28 columns x 14 rows
        private const int LogoRows = 14;
        private const int Margin = 2;          // columns left of the logo, and between logo and text
        private const int TextTop = 4;         // logo row the text starts on when side by side

        public static void Draw(string status, bool ascii)
        {
            int width = Math.Max(40, Terminal.Width - 1);
            int height = Math.Max(12, Terminal.Height);

            var text = new List<KeyValuePair<int, string>>();      // visible width, VT text
            text.Add(Text(Fix(Brand.Title, ascii), Ansi.Bold));
            text.Add(Text(Brand.Version, Ansi.Dim));
            text.Add(Text(string.Empty, null));
            text.Add(Text(Fix(Brand.Copyright, ascii), Ansi.Dim));
            text.Add(Text(string.Empty, null));
            text.Add(Text(status ?? string.Empty, Ansi.Cyan));

            int textWidth = 0;
            foreach (var item in text)
            {
                textWidth = Math.Max(textWidth, item.Key);
            }

            List<string> logo = Logo.Render(LogoColumns, Math.Min(LogoRows, height - 2), ascii);
            bool beside = logo.Count > 0 && Margin + LogoColumns + Margin + textWidth <= width;

            var lines = new List<string>();
            string pad = new string(' ', Margin);
            if (beside)
            {
                string gap = new string(' ', Margin);
                for (int i = 0; i < Math.Max(logo.Count, TextTop + text.Count); i++)
                {
                    string left = i < logo.Count ? logo[i] + Ansi.Reset : new string(' ', LogoColumns);
                    int t = i - TextTop;
                    string right = t >= 0 && t < text.Count ? text[t].Value : string.Empty;
                    lines.Add(pad + left + gap + right);
                }
            }
            else
            {
                foreach (string line in logo)
                {
                    lines.Add(pad + line);
                }
                if (logo.Count > 0)
                {
                    lines.Add(string.Empty);
                }
                foreach (var item in text)
                {
                    lines.Add(pad + item.Value);
                }
            }

            var frame = new StringBuilder();
            frame.Append(Ansi.EraseToEndOfLine).Append("\r\n");            // one blank row on top
            for (int i = 0; i < lines.Count && i + 1 < height; i++)
            {
                if (i > 0)
                {
                    frame.Append("\r\n");
                }
                frame.Append(lines[i]).Append(Ansi.Reset).Append(Ansi.EraseToEndOfLine);
            }

            Terminal.Draw(frame.ToString());
        }

        private static KeyValuePair<int, string> Text(string text, string style)
        {
            return new KeyValuePair<int, string>(text.Length, style == null ? text : style + text + Ansi.Reset);
        }

        private static string Fix(string text, bool ascii)
        {
            return ascii ? Brand.Plain(text) : text;
        }
    }
}
