// (c) 2017-2026 Sector 7G Consulting LLC
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using S7G.mbpm.Core;

namespace S7G.mbpm.UI
{
    /// <summary>Everything one frame shows. Built by the App from its state.</summary>
    internal sealed class ViewModel
    {
        public string Machine;
        public string Source;
        public string SortLabel;
        public string Status;           // newest error or warning, shown in red in the event area
        public string Notice;           // e.g. the result of an export, shown for a few seconds
        public int Cores;
        public uint Interval;
        public bool Paused;
        public bool ShowCores;          // F7: the CPU card shows every core
        public bool Help;
        public int HelpScroll;          // first help line shown; the view writes back the clamped value
        public bool Ascii;
        public double?[] CoreLoads = new double?[0];
        public double? TotalCpu;
        public double? AvailableMB;
        public double? TotalMB;
        public double? CommittedBytes;
        public double? CommitLimitBytes;
        public List<PoolRow> Rows = new List<PoolRow>();
        public int Selected;
        public List<WorkerEvent> Events = new List<WorkerEvent>();
        public int PoolCount;
        public int WorkerCount;
        public int WorkerChanges;
        public double? MbpmCpu;         // MBPM's own CPU, % of all cores of the machine it runs on
        public double DelayMs;          // newest sample's delay against its schedule
        public double MaxDelayMs;
        public long MissedSamples;
        public bool Mock;               // --mock: labeled MOCK in the title
        public string ThemeName;
    }

    /// <summary>
    /// Lays out one frame for the current window size: the title line, a row of cards (CPU or Cores,
    /// Memory, App pools, mbpm.exe), the per-pool table (scrolled to keep the selection visible), one event
    /// line, and the key bar. The frame never has more lines than the window, so the console never
    /// scrolls.
    /// </summary>
    internal static class View
    {
        private const int MinWidth = 60;
        private const int MinHeight = 16;
        private const int OneRowOfCards = 120;      // narrower windows put the cards two to a row
        private const int OneRowUnderCores = 100;   // below the Cores card: three to a row from here

        private const string UnicodeBlocks = "▁▂▃▄▅▆▇█";
        private const string AsciiBlocks = "_.-:=+*#";

        /// <summary>Renders a frame. scrollTop is kept so the selection stays visible; pageSize is the number of table rows shown.</summary>
        public static string Render(ViewModel m, int windowWidth, int windowHeight, ref int scrollTop, out int pageSize)
        {
            int w = Math.Max(MinWidth, windowWidth - 1);    // leave the last column free: no auto-wrap
            int h = Math.Max(MinHeight, windowHeight);

            var lines = new List<LineBuilder>();
            lines.Add(TitleLine(m, w));

            // Help uses the whole area below the title instead of the cards and the table.
            List<LineBuilder> cards = m.Help ? new List<LineBuilder>() : Cards(m, w);
            lines.AddRange(cards);

            // What's left for the table: below it go the event line and the key bar.
            int capacity = Math.Max(1, h - lines.Count - 1 - 2);
            pageSize = capacity;

            int count = m.Help ? 0 : m.Rows.Count;
            if (m.Selected < scrollTop)
            {
                scrollTop = m.Selected;
            }
            if (m.Selected >= scrollTop + capacity)
            {
                scrollTop = m.Selected - capacity + 1;
            }
            scrollTop = Math.Max(0, Math.Min(scrollTop, Math.Max(0, count - capacity)));

            string sortAndRange = null;
            if (m.Help)
            {
                lines.AddRange(HelpLines(m, w, capacity + 1));
            }
            else
            {
                string range = count == 0 ? Strings.RangeNone
                    : count <= capacity ? string.Format(Strings.RangeAll, count)
                    : string.Format(Strings.RangeSome, scrollTop + 1, Math.Min(count, scrollTop + capacity), count);
                sortAndRange = string.Format(Strings.SortAndRange, m.SortLabel, range);

                Columns columns = Columns.Fit(m, w);
                lines.Add(columns.Header(m, w));
                for (int i = 0; i < capacity; i++)
                {
                    int index = scrollTop + i;
                    lines.Add(index < count ? columns.Row(m, m.Rows[index], index == m.Selected, w) : new LineBuilder(w));
                }
            }

            lines.Add(EventLine(m, w, sortAndRange));
            lines.Add(KeyBar(m, w));

            var frame = new StringBuilder();
            for (int i = 0; i < lines.Count && i < h; i++)
            {
                if (i > 0)
                {
                    frame.Append("\r\n");
                }
                frame.Append(lines[i]).Append(Ansi.EraseToEndOfLine);
            }
            return frame.ToString();
        }

        private static LineBuilder TitleLine(ViewModel m, int w)
        {
            var line = new LineBuilder(w);
            line.Add(" ");
            Logo.Badge(line, m.Ascii);
            line.Add(" " + Brand.Short, Ansi.Bold)
                .Add(m.Ascii ? "  -  " : "  ·  ", Ansi.Dim)
                .Add(Brand.Name, Ansi.Cyan + Ansi.Bold)
                .Add("  " + Brand.Version, Ansi.Dim);
            if (m.Mock)
            {
                line.Add("  ").Add(" " + Strings.TitleMock + " ", Ansi.MockBadge);
            }
            if (m.Paused)
            {
                line.Add("   " + Strings.TitlePaused, Ansi.Yellow + Ansi.Bold);
            }

            string right = string.Format(Strings.TitleRight, m.Machine, m.Source, m.Cores, m.Interval);
            line.PadTo(w - right.Length).Add(right, Ansi.Dim);
            return line;
        }

        // ---- Cards ----

        private sealed class CardSpec
        {
            public string Title;
            public int Weight;                              // share of the row's width
            public Func<int, LineBuilder[]> Content;        // content lines for an inner width
        }

        // Boxed cards, each with a title in its top border:
        //   CPU        total bar; one cell per core below it (cores averaged per cell when they don't fit)
        //   Cores      (F7) total bar, then one bar per core, htop-style; on a row of its own
        //   Memory     Mem and Commit bars
        //   App pools  pools and workers; all workers' CPU and worker changes
        //   mbpm.exe   MBPM's own CPU and missed samples; sample delay
        // CPU view: one row of four, or two rows of two when narrow. Cores view: the Cores card,
        // then the other three in a row (two and one when narrow).
        private static List<LineBuilder> Cards(ViewModel m, int w)
        {
            var cpu = new CardSpec
            {
                Title = m.ShowCores ? Strings.CardCores : Strings.CardCpu,
                Weight = 28,
                Content = inner => m.ShowCores ? CoresContent(m, inner) : CpuContent(m, inner),
            };
            var memory = new CardSpec { Title = Strings.CardMemory, Weight = 28, Content = inner => MemoryContent(m, inner) };
            var pools = new CardSpec { Title = Strings.CardPools, Weight = 22, Content = inner => PoolsContent(m, inner) };
            var mbpm = new CardSpec { Title = Strings.CardMbpm, Weight = 22, Content = inner => MbpmContent(m, inner) };

            var rows = new List<CardSpec[]>();
            if (m.ShowCores)
            {
                rows.Add(new[] { cpu });
                if (w >= OneRowUnderCores)
                {
                    rows.Add(new[] { memory, pools, mbpm });
                }
                else
                {
                    rows.Add(new[] { memory, pools });
                    rows.Add(new[] { mbpm });
                }
            }
            else if (w >= OneRowOfCards)
            {
                rows.Add(new[] { cpu, memory, pools, mbpm });
            }
            else
            {
                rows.Add(new[] { cpu, memory });
                rows.Add(new[] { pools, mbpm });
            }

            var lines = new List<LineBuilder>();
            foreach (CardSpec[] row in rows)
            {
                int available = w - (row.Length - 1);       // one space between cards
                int totalWeight = row.Sum(c => c.Weight);
                var boxes = new List<List<LineBuilder>>();
                var widths = new List<int>();
                int used = 0;
                for (int i = 0; i < row.Length; i++)
                {
                    int width = i == row.Length - 1 ? available - used : available * row[i].Weight / totalWeight;
                    used += width;
                    widths.Add(width);
                    boxes.Add(Card(m, row[i].Title, row[i].Content(width - 4), width));
                }

                int height = boxes.Max(b => b.Count);
                for (int r = 0; r < height; r++)
                {
                    var line = new LineBuilder(w);
                    for (int i = 0; i < boxes.Count; i++)
                    {
                        if (i > 0)
                        {
                            line.Add(" ");
                        }
                        int start = line.Length;
                        if (r < boxes[i].Count)
                        {
                            line.Append(boxes[i][r]);
                        }
                        line.PadTo(start + widths[i]);
                    }
                    lines.Add(line);
                }
            }
            return lines;
        }

        private static LineBuilder[] CpuContent(ViewModel m, int inner)
        {
            return new[]
            {
                Bar(new LineBuilder(inner), string.Empty, m.TotalCpu, inner, null),
                CoreStrip(m, inner),
            };
        }

        // The total, then one bar per core in up to 8 columns (column-major, as htop), in as few rows as fit.
        private static LineBuilder[] CoresContent(ViewModel m, int inner)
        {
            double?[] loads = m.CoreLoads ?? new double?[0];
            int n = loads.Length;
            int labelWidth = Math.Max(Strings.CardCoresAll.Length, n.ToString(CultureInfo.InvariantCulture).Length);

            var lines = new List<LineBuilder>();
            lines.Add(Bar(new LineBuilder(inner), Strings.CardCoresAll.PadLeft(labelWidth), m.TotalCpu, inner, null));
            if (n == 0)
            {
                return lines.ToArray();
            }

            int columns = 1;
            foreach (int candidate in new[] { 8, 4, 2, 1 })
            {
                if ((inner - (candidate - 1)) / candidate >= labelWidth + 10)
                {
                    columns = candidate;
                    break;
                }
            }
            int rows = (n + columns - 1) / columns;
            int cellWidth = (inner - (columns - 1)) / columns;
            for (int r = 0; r < rows; r++)
            {
                var line = new LineBuilder(inner);
                for (int c = 0; c < columns; c++)
                {
                    int core = c * rows + r;
                    if (core >= n)
                    {
                        break;
                    }
                    if (c > 0)
                    {
                        line.Add(" ");
                    }
                    Bar(line, (core + 1).ToString(CultureInfo.InvariantCulture).PadLeft(labelWidth), loads[core], cellWidth, null);
                }
                lines.Add(line);
            }
            return lines.ToArray();
        }

        private static LineBuilder[] MemoryContent(ViewModel m, int inner)
        {
            const double BytesPerMB = 1024.0 * 1024.0;
            int labelWidth = Math.Max(Strings.MeterMem.Length, Strings.MeterCommit.Length) + 1;
            double? usedMB = m.TotalMB.HasValue && m.AvailableMB.HasValue ? m.TotalMB.Value - m.AvailableMB.Value : (double?)null;
            string memText = usedMB.HasValue
                ? Gigabytes(usedMB.Value) + "/" + Gigabytes(m.TotalMB.Value)
                : (m.AvailableMB.HasValue ? string.Format(Strings.MeterFree, Gigabytes(m.AvailableMB.Value)) : "--");
            double? memPercent = usedMB.HasValue && m.TotalMB.Value > 0 ? usedMB.Value / m.TotalMB.Value * 100 : (double?)null;
            double? commitPercent = m.CommittedBytes.HasValue && m.CommitLimitBytes.HasValue && m.CommitLimitBytes.Value > 0
                ? m.CommittedBytes.Value / m.CommitLimitBytes.Value * 100 : (double?)null;
            string commitText = commitPercent.HasValue
                ? Gigabytes(m.CommittedBytes.Value / BytesPerMB) + "/" + Gigabytes(m.CommitLimitBytes.Value / BytesPerMB)
                : "--";
            return new[]
            {
                Bar(new LineBuilder(inner), Strings.MeterMem.PadRight(labelWidth), memPercent, inner, memText),
                Bar(new LineBuilder(inner), Strings.MeterCommit.PadRight(labelWidth), commitPercent, inner, commitText),
            };
        }

        private static LineBuilder[] PoolsContent(ViewModel m, int inner)
        {
            double all = m.Rows.Where(r => r.CpuAll.HasValue).Sum(r => r.CpuAll.Value);
            return new[]
            {
                new LineBuilder(inner)
                    .Add(m.PoolCount.ToString(CultureInfo.InvariantCulture), Ansi.Bold).Add(Strings.CardPoolsPools, Ansi.Dim)
                    .Add(m.WorkerCount.ToString(CultureInfo.InvariantCulture), Ansi.Bold).Add(Strings.CardPoolsWorkers, Ansi.Dim),
                new LineBuilder(inner)
                    .Add(Percent(all), Ansi.Bold + Ansi.Level(all)).Add(Strings.CardPoolsCpu, Ansi.Dim)
                    .Add(m.WorkerChanges.ToString(CultureInfo.InvariantCulture), Ansi.Bold).Add(Strings.CardPoolsChanges, Ansi.Dim),
            };
        }

        // MBPM's own cost and sampling health: the monitor shouldn't skew what it measures.
        private static LineBuilder[] MbpmContent(ViewModel m, int inner)
        {
            return new[]
            {
                new LineBuilder(inner)
                    .Add(m.MbpmCpu.HasValue ? Percent(m.MbpmCpu.Value) : "-", Ansi.Bold).Add(Strings.CardMbpmCpu, Ansi.Dim)
                    .Add(m.MissedSamples.ToString(CultureInfo.InvariantCulture), m.MissedSamples > 0 ? Ansi.Yellow + Ansi.Bold : Ansi.Bold)
                    .Add(Strings.CardMbpmMissed, Ansi.Dim),
                new LineBuilder(inner)
                    .Add(string.Format(CultureInfo.InvariantCulture, Strings.CardMbpmDelay, m.DelayMs, m.MaxDelayMs), Ansi.Dim),
            };
        }

        // A box of the given outer width: title in the top border, one content line per entry.
        private static List<LineBuilder> Card(ViewModel m, string title, LineBuilder[] content, int width)
        {
            string tl = m.Ascii ? "+" : "╭", tr = m.Ascii ? "+" : "╮", bl = m.Ascii ? "+" : "╰", br = m.Ascii ? "+" : "╯";
            string horizontal = m.Ascii ? "-" : "─", vertical = m.Ascii ? "|" : "│";
            int inner = width - 2;

            var lines = new List<LineBuilder>();
            string label = " " + title + " ";
            var top = new LineBuilder(width).Add(tl + horizontal, Ansi.Dim).Add(label, Ansi.Cyan + Ansi.Bold);
            top.Add(Repeat(horizontal, Math.Max(0, inner - 1 - label.Length)), Ansi.Dim).Add(tr, Ansi.Dim);
            lines.Add(top);

            foreach (LineBuilder text in content)
            {
                var line = new LineBuilder(width).Add(vertical + " ", Ansi.Dim).Append(text);
                line.PadTo(width - 2).Add(" " + vertical, Ansi.Dim);
                lines.Add(line);
            }

            lines.Add(new LineBuilder(width).Add(bl + Repeat(horizontal, inner) + br, Ansi.Dim));
            return lines;
        }

        // One cell per core, colored by load; several cores averaged per cell when they don't fit.
        private static LineBuilder CoreStrip(ViewModel m, int width)
        {
            var line = new LineBuilder(width);
            double?[] loads = m.CoreLoads ?? new double?[0];
            if (loads.Length == 0)
            {
                return line;
            }
            string blocks = m.Ascii ? AsciiBlocks : UnicodeBlocks;
            int group = (loads.Length + width - 1) / width;
            for (int start = 0; start < loads.Length; start += group)
            {
                double sum = 0;
                int valid = 0;
                for (int i = start; i < Math.Min(loads.Length, start + group); i++)
                {
                    if (loads[i].HasValue)
                    {
                        sum += loads[i].Value;
                        valid++;
                    }
                }
                double? load = valid > 0 ? sum / valid : (double?)null;
                int level = load.HasValue ? (int)Math.Round(Math.Max(0, Math.Min(100, load.Value)) / 100 * (blocks.Length - 1)) : 0;
                line.Add(blocks[level].ToString(), Ansi.Level(load));
            }
            return line;
        }

        private static LineBuilder Bar(LineBuilder line, string label, double? percent, int cellWidth, string text)
        {
            string value = text ?? (percent.HasValue ? Percent(percent.Value) : "--");
            int inner = cellWidth - label.Length - 2;
            if (text == null && inner < value.Length + 4 && percent.HasValue)
            {
                value = percent.Value.ToString("0", CultureInfo.InvariantCulture);
            }
            int barWidth = Math.Max(0, inner - value.Length);
            int filled = percent.HasValue ? (int)Math.Round(Math.Max(0, Math.Min(100, percent.Value)) / 100 * barWidth) : 0;

            line.Add(label, Ansi.Cyan).Add("[", Ansi.Bold);
            line.Add(new string('|', filled), Ansi.Level(percent));
            line.Add(new string(' ', barWidth - filled));
            line.Add(value, Ansi.Dim).Add("]", Ansi.Bold);
            return line;
        }

        private static string Percent(double value)
        {
            return value.ToString("0.0", CultureInfo.InvariantCulture) + "%";
        }

        private static string Repeat(string text, int count)
        {
            var result = new StringBuilder(text.Length * Math.Max(0, count));
            for (int i = 0; i < count; i++)
            {
                result.Append(text);
            }
            return result.ToString();
        }

        // ---- Table ----

        private sealed class Columns
        {
            public int PoolWidth;
            public bool ShowUser;
            public int UserWidth;
            public bool ShowMemory;

            private const int PidWidth = 10;     // "W3WP PID" plus a space
            private const int CpuWidth = 7;     // share of the whole machine, as Task Manager's Processes tab
            private const int WsWidth = 8;
            private const int PrivateWsWidth = 15;
            private const int CommitWidth = 11;
            private const int UptimeWidth = 15;
            private const int MemoryWidth = WsWidth + PrivateWsWidth + CommitWidth;
            private const int MinUserWidth = 12;
            private const int MaxUserWidth = 40;

            // Drops, in order, the USER column and the memory columns when the window is narrow.
            public static Columns Fit(ViewModel m, int w)
            {
                var c = new Columns();
                int longest = m.Rows.Count == 0 ? 0 : m.Rows.Max(r => r.Pool.Length);
                c.PoolWidth = Math.Max(14, Math.Min(34, longest + 2));

                int available = w - (1 + c.PoolWidth + PidWidth + CpuWidth + UptimeWidth);
                c.ShowMemory = available >= MemoryWidth;
                if (c.ShowMemory)
                {
                    available -= MemoryWidth;
                }

                int longestUser = m.Rows.Count == 0 ? 0 : m.Rows.Max(r => (r.User ?? "-").Length);
                c.UserWidth = Math.Min(Math.Min(MaxUserWidth, Math.Max(Strings.ColUser.Length, longestUser)), available - 2);
                c.ShowUser = c.UserWidth >= MinUserWidth;
                return c;
            }

            public LineBuilder Header(ViewModel m, int w)
            {
                var text = new StringBuilder();
                text.Append(' ').Append(Strings.ColPool.PadRight(PoolWidth))
                    .Append(Strings.ColPid.PadLeft(PidWidth - 1)).Append(' ')
                    .Append(Strings.ColCpu.PadLeft(CpuWidth));
                if (ShowUser)
                {
                    text.Append("  ").Append(Strings.ColUser.PadRight(UserWidth));
                }
                if (ShowMemory)
                {
                    text.Append(Strings.ColWs.PadLeft(WsWidth))
                        .Append(Strings.ColPrivateWs.PadLeft(PrivateWsWidth))
                        .Append(Strings.ColCommit.PadLeft(CommitWidth));
                }
                text.Append("  ").Append(Strings.ColUptime);
                return new LineBuilder(w).Add(text.ToString(), Ansi.HeaderBar).Fill(Ansi.HeaderBar);
            }

            public LineBuilder Row(ViewModel m, PoolRow row, bool selected, int w)
            {
                var line = new LineBuilder(w);
                string baseStyle = selected ? Ansi.Selected : null;
                Func<string, string> style = s => selected ? Ansi.Selected : s;

                line.Add(" " + Truncate(row.Pool, PoolWidth - 1).PadRight(PoolWidth), baseStyle);
                line.Add(row.Pid.ToString(CultureInfo.InvariantCulture).PadLeft(PidWidth - 1) + " ", style(Ansi.Dim));

                if (row.NotInSample)
                {
                    line.Add("  " + Strings.RowNotInSample, style(Ansi.Dim));
                    return selected ? line.Fill(Ansi.Selected) : line;
                }

                line.Add(Number(row.CpuAll, "0.0").PadLeft(CpuWidth), style(Ansi.Bold + Ansi.Level(row.CpuAll)));

                if (ShowUser)
                {
                    line.Add("  ", baseStyle);
                    line.Add(Truncate(row.User ?? "-", UserWidth).PadRight(UserWidth), style(Ansi.Cyan));
                }
                if (ShowMemory)
                {
                    line.Add(Number(row.WorkingSetMB, "0").PadLeft(WsWidth), baseStyle);
                    line.Add(Number(row.PrivateWorkingSetMB, "0").PadLeft(PrivateWsWidth), baseStyle);
                    line.Add(Number(row.CommitMB, "0").PadLeft(CommitWidth), baseStyle);
                }
                line.Add("  " + Uptime(row.Uptime), style(Ansi.Dim));

                return selected ? line.Fill(Ansi.Selected) : line;
            }
        }

        // ---- Events, help, key bar ----

        // One line: an error or warning first, then a notice, then the newest worker change; the
        // sort order and visible rows at the right end when there's room.
        private static LineBuilder EventLine(ViewModel m, int w, string right)
        {
            var line = new LineBuilder(w);
            if (!string.IsNullOrEmpty(m.Status))
            {
                line.Add(" " + m.Status, Ansi.Red);
            }
            else if (!string.IsNullOrEmpty(m.Notice))
            {
                line.Add(" " + m.Notice, Ansi.Green);
            }
            else if (m.Events.Count > 0)
            {
                WorkerEvent e = m.Events[m.Events.Count - 1];
                line.Add(" " + e.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  ", Ansi.Dim)
                    .Add(Strings.Get("Kind_" + e.Kind).PadRight(9), Ansi.Yellow)
                    .Add(e.Pool + "  ")
                    .Add(e.Detail, Ansi.Dim);
            }
            else
            {
                line.Add(" " + Strings.EventsNone, Ansi.Dim);
            }

            if (right != null && line.Length + right.Length + 4 <= w)
            {
                line.PadTo(w - right.Length - 1).Add(right, Ansi.Dim);
            }
            return line;
        }

        private const int HelpLogoSize = 28;     // the 28-pixel logo: 28 columns, 14 rows

        /// <summary>
        /// The help screen: an "about" header (logo beside the name, version and copyright),
        /// then keys, columns, export and worker-change notes. Scrolls with the arrow keys.
        /// </summary>
        private static IEnumerable<LineBuilder> HelpLines(ViewModel m, int w, int count)
        {
            var all = new List<LineBuilder>();
            all.AddRange(AboutLines(m, w, count));
            all.Add(new LineBuilder(w));

            var text = new List<string>(Strings.HelpBody.Replace("\r\n", "\n").Split('\n'));

            // Themes, listed from Theme.All so the help always matches what F3 cycles through.
            text.Add(string.Empty);
            text.Add(" " + Strings.HelpThemesHeading);
            int nameWidth = Theme.All.Max(t => t.Name.Length) + 2;
            foreach (Theme theme in Theme.All)
            {
                string font = theme.FontFace == null ? string.Empty
                    : string.Format(CultureInfo.InvariantCulture, " ({0} {1})", theme.FontFace, theme.FontSize);
                text.Add("   " + theme.Name.PadRight(nameWidth) + theme.Description + font
                    + (theme == Theme.Current ? "  " + Strings.HelpThemeCurrent : string.Empty));
            }
            text.Add("   " + Strings.HelpThemesFontNote);
            text.Add(string.Empty);
            text.Add(" " + Brand.Copyright);

            foreach (string raw in text)
            {
                string value = m.Ascii ? Brand.Plain(raw) : raw;
                bool heading = value.Length > 1 && value[1] != ' ' && !value.StartsWith(" ©", StringComparison.Ordinal)
                    && !value.StartsWith(" (c)", StringComparison.Ordinal) && !value.StartsWith(" “", StringComparison.Ordinal)
                    && !value.StartsWith(" \"", StringComparison.Ordinal);
                all.Add(new LineBuilder(w).Add(value, heading ? Ansi.Cyan + Ansi.Bold : (value.StartsWith("   ", StringComparison.Ordinal) ? null : Ansi.Dim)));
            }

            // Scroll window; the last line says how to see more when the help doesn't fit.
            int maxScroll = Math.Max(0, all.Count - count);
            int scroll = Math.Max(0, Math.Min(m.HelpScroll, maxScroll));
            m.HelpScroll = scroll;      // hands the clamped position back to the App
            for (int i = 0; i < count; i++)
            {
                int index = scroll + i;
                if (i == count - 1 && maxScroll > 0)
                {
                    string where = scroll >= maxScroll ? Strings.HelpScrollEnd : Strings.HelpScrollMore;
                    yield return new LineBuilder(w).Add(string.Format(Strings.HelpScrollFooter,
                        where, Math.Min(all.Count, scroll + count), all.Count), Ansi.Dim);
                    yield break;
                }
                yield return index < all.Count ? all[index] : new LineBuilder(w);
            }
        }

        // The logo with the product name, version and copyright beside it; text only when there's
        // no room for the logo.
        private static List<LineBuilder> AboutLines(ViewModel m, int w, int available)
        {
            string[] about =
            {
                Brand.Title,
                Brand.Version,
                "",
                Brand.Copyright,
                "",
                Strings.HelpAboutDescription,
            };
            string[] styles = { Ansi.Bold + Ansi.Cyan, Ansi.Dim, null, null, null, null };

            var lines = new List<LineBuilder>();
            int logoRows = HelpLogoSize / 2;
            bool withLogo = available >= logoRows + 8 && w >= HelpLogoSize + 4 + 60;
            List<string> logo = withLogo ? Logo.Render(HelpLogoSize, logoRows, m.Ascii) : new List<string>();
            int logoWidth = logo.Count > 0 ? HelpLogoSize : 0;

            int rows = Math.Max(logo.Count, about.Length);
            int textTop = logo.Count > 0 ? (logo.Count - about.Length) / 2 : 0;
            for (int r = 0; r < rows; r++)
            {
                var line = new LineBuilder(w).Add(" ");
                if (logo.Count > 0)
                {
                    line.AddRaw(r < logo.Count ? logo[r] : string.Empty, r < logo.Count ? logoWidth : 0);
                    line.PadTo(1 + logoWidth + 3);
                }
                int t = r - textTop;
                if (t >= 0 && t < about.Length)
                {
                    string value = m.Ascii ? Brand.Plain(about[t]) : about[t];
                    line.Add(value, styles[t]);
                }
                lines.Add(line);
            }
            return lines;
        }

        // htop-style key bar; the theme key sits at the right end, with the current theme's name.
        private static LineBuilder KeyBar(ViewModel m, int w)
        {
            var line = new LineBuilder(w);
            var keys = new List<string[]> { new[] { "F1", Strings.KeyHelp } };
            keys.Add(new[] { "F4", string.Format(Strings.KeyInterval, m.Interval) });
            keys.Add(new[] { "F5", Strings.KeyPause });
            keys.Add(new[] { "F6", Strings.KeySort });
            keys.Add(new[] { "F7", m.ShowCores ? Strings.KeyCpu : Strings.KeyCores });
            keys.Add(new[] { "F8", Strings.KeyExport });
            keys.Add(new[] { "F10", Strings.KeyQuit });
            foreach (string[] key in keys)
            {
                line.Add(key[0]).Add(key[1].PadRight(6), Ansi.KeyLabel);     // at least 6 wide, as htop does
            }
            line.Add("  " + Strings.KeyUpDown, null).Add(" " + Strings.KeySelect + " ", Ansi.KeyLabel);

            string theme = " " + string.Format(Strings.KeyTheme, m.ThemeName) + " ";
            int themeWidth = 2 + theme.Length;
            if (line.Length + themeWidth + 1 <= w)
            {
                line.PadTo(w - themeWidth, Ansi.KeyLabel).Add("F3").Add(theme, Ansi.KeyLabel);
            }
            return line.Fill(Ansi.KeyLabel);
        }

        // ---- Formatting ----

        public static string Uptime(TimeSpan? uptime)
        {
            if (!uptime.HasValue)
            {
                return "-";
            }
            TimeSpan t = uptime.Value;
            string clock = string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}", t.Hours, t.Minutes, t.Seconds);
            return t.Days > 0 ? string.Format(CultureInfo.InvariantCulture, Strings.UptimeDays, t.Days, clock) : clock;
        }

        private static string Number(double? value, string format)
        {
            return value.HasValue ? value.Value.ToString(format, CultureInfo.InvariantCulture) : "-";
        }

        private static string Gigabytes(double megabytes)
        {
            return (megabytes / 1024).ToString("0.0", CultureInfo.InvariantCulture) + "G";
        }

        private static string Truncate(string text, int length)
        {
            return text.Length <= length ? text : text.Substring(0, Math.Max(0, length - 1)) + "…";
        }
    }
}
