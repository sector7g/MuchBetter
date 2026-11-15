// (c) 2017-2026 Sector 7G Consulting LLC
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using S7G.mbpm.Core;

namespace S7G.mbpm.Logging
{
    /// <summary>
    /// Builds the per-pool exports from a session's raw logs (samples.csv, machine.csv). While MBPM
    /// runs, LiveExport writes the same files sample by sample; this is "mbpm export", for sessions
    /// kept with --keep-raw (to cut a time range) or run with --no-export. The files:
    ///   mbpm_export_perfmon.csv  PerfMon CSV log format, one column per pool and metric. Opens in
    ///                            Performance Monitor (View Log Data) like any PerfMon CSV, so it also
    ///                            works with relog and with tools that ingest PerfMon CSV logs.
    ///   mbpm_export_cpu.csv      plain CSV: timestamp, then CPU % of one core per pool (for Excel).
    ///   export_info.txt          where the data came from and how each column is computed.
    /// Columns are keyed by pool, not PID: a replaced worker stays in its pool's column, and several
    /// workers of one pool (web garden) are summed. The counter objects are named "Much Better App Pool" and
    /// "Much Better Machine" so no one mistakes them for counters Windows provides.
    /// Safe to run while MBPM is still writing the session (partial lines are skipped).
    /// </summary>
    internal static class Exporter
    {
        internal sealed class Result
        {
            public string PerfMonPath;
            public string CpuPath;
            public string InfoPath;
            public int Samples;
            public int Pools;
            public DateTime? First;
            public DateTime? Last;
        }

        internal const string LogTimeFormat = "yyyy-MM-dd HH:mm:ss.fff";
        internal const string PerfMonTimeFormat = "MM/dd/yyyy HH:mm:ss.fff";
        internal const string PerfMonFile = "mbpm_export_perfmon.csv";
        internal const string CpuFile = "mbpm_export_cpu.csv";
        internal const string InfoFile = "export_info.txt";
        // Raw log layouts. Current: utc_timestamp,local_timestamp, then the values. Older sessions
        // (one local "timestamp" column) are still read.
        private const int SampleFields = 13;    // utc,local,pool,pid,cpu1,cpuAll,ws,privateWs,commit,uptime,gc,native,http500pct
        private const int SampleFieldsV2 = 10;  // utc,local,pool,pid,cpu1,cpuAll,ws,privateWs,commit,uptime
        private const int SampleFieldsV1 = 9;   // timestamp,pool,pid,...
        private const int MachineFields = 10;   // utc,local,cpu,memUsed,memTotal,commit,commitLimit,mbpmCpu,delay,missed
        private const int MachineFieldsV2 = 9;  // timestamp + the same 8 values
        private const int MachineFieldsV1 = 6;  // timestamp,cpu,memUsed,memTotal,commit,commitLimit

        internal static readonly string[] PoolCounters =
        {
            "CPU % of one core", "CPU % of machine", "WS MB", "Private WS MB", "Commit MB",
            "GC MB", "Native MB (approx)", "HTTP 500 %",
        };

        // How a pool's workers combine (web garden): summed, except HTTP 500 %, a percentage, where
        // the highest of the pool's workers is kept.
        internal static readonly bool[] PoolCounterSummed = { true, true, true, true, true, true, true, false };

        /// <summary>Combines a worker's value into its pool's cell.</summary>
        internal static void Combine(double?[,] sums, int pool, int counter, double value)
        {
            double? current = sums[pool, counter];
            sums[pool, counter] = !current.HasValue ? value
                : PoolCounterSummed[counter] ? current.Value + value
                : Math.Max(current.Value, value);
        }

        // Where each PoolCounters value is in a samples.csv row, counted from the pool column.
        // Uptime (offset 7) sits between Commit MB and the counters added later.
        private static readonly int[] SampleOffsets = { 2, 3, 4, 5, 6, 8, 9, 10 };

        internal static readonly string[] MachineCounters =
            { "CPU %", "Memory used MB", "Memory total MB", "Commit MB", "Commit limit MB", "Much Better CPU %", "Sample delay ms", "Missed samples" };

        /// <param name="from">Optional start: "yyyy-MM-dd HH:mm[:ss]" or a time of day on the session's first day.</param>
        /// <param name="to">Optional end, same forms.</param>
        public static Result Export(string folder, string machine, string from, string to)
        {
            string samplesPath = Path.Combine(folder, "samples.csv");
            if (!File.Exists(samplesPath))
            {
                throw new FileNotFoundException(string.Format(Strings.ErrNoSamplesFile, folder), samplesPath);
            }
            if (string.IsNullOrEmpty(machine))
            {
                DirectoryInfo parent = new DirectoryInfo(folder).Parent;   // <root>\<machine>\<session>
                machine = parent != null ? parent.Name : Environment.MachineName;
            }

            // Pass 1: the pools in range (the header needs them all) and the session's first day.
            DateTime? firstInFile = null;
            var pools = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            DateTime? start = null;
            DateTime? end = null;
            bool boundsResolved = false;

            foreach (string[] fields in ReadRows(samplesPath, SampleFields, SampleFieldsV2, SampleFieldsV1))
            {
                int t = SampleTimeColumns(fields);
                DateTime time;
                DateTimeOffset when;
                if (!TryRowTime(fields, t, out when))
                {
                    continue;
                }
                time = when.DateTime;
                if (!boundsResolved)
                {
                    firstInFile = time;
                    start = ResolveBound(from, time, "--from");
                    end = ResolveBound(to, time, "--to");
                    boundsResolved = true;
                }
                if (InRange(time, start, end))
                {
                    pools.Add(fields[t]);
                }
            }

            List<string> poolList = pools.ToList();
            var column = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < poolList.Count; i++)
            {
                column[poolList[i]] = i;
            }

            Dictionary<string, string[]> machineRows = LoadMachineRows(Path.Combine(folder, "machine.csv"));

            var result = new Result
            {
                PerfMonPath = Path.Combine(folder, PerfMonFile),
                CpuPath = Path.Combine(folder, CpuFile),
                InfoPath = Path.Combine(folder, InfoFile),
                Pools = poolList.Count,
            };

            // Pass 2: samples.csv is in time order, so stream it, one output row per timestamp.
            using (StreamWriter perfmon = Create(result.PerfMonPath))
            using (StreamWriter cpu = Create(result.CpuPath))
            {
                perfmon.WriteLine(PerfMonHeader(machine, poolList));
                cpu.WriteLine(CpuHeader(poolList));

                string currentKey = null;
                DateTimeOffset currentTime = default(DateTimeOffset);
                var sums = new double?[poolList.Count, PoolCounters.Length];

                foreach (string[] fields in ReadRows(samplesPath, SampleFields, SampleFieldsV2, SampleFieldsV1))
                {
                    int t = SampleTimeColumns(fields);
                    DateTimeOffset when;
                    if (!TryRowTime(fields, t, out when) || !InRange(when.DateTime, start, end))
                    {
                        continue;
                    }

                    string key = fields[t - 1];     // local time text: the same in samples.csv and machine.csv
                    if (key != currentKey)
                    {
                        if (currentKey != null)
                        {
                            WriteRow(perfmon, cpu, currentKey, currentTime, sums, machineRows);
                            Track(result, currentTime.DateTime);
                        }
                        currentKey = key;
                        currentTime = when;
                        Array.Clear(sums, 0, sums.Length);
                    }

                    int p;
                    if (!column.TryGetValue(fields[t], out p))
                    {
                        continue;
                    }
                    for (int k = 0; k < PoolCounters.Length; k++)
                    {
                        int index = t + SampleOffsets[k];
                        double value;
                        if (index < fields.Length && TryNumber(fields[index], out value))     // older logs have fewer
                        {
                            Combine(sums, p, k, value);     // web garden: the pool's workers together
                        }
                    }
                }

                if (currentKey != null)
                {
                    WriteRow(perfmon, cpu, currentKey, currentTime, sums, machineRows);
                    Track(result, currentTime.DateTime);
                }
            }

            File.WriteAllText(result.InfoPath, Info(folder, machine, result, from, to), new UTF8Encoding(false));
            return result;
        }

        private static void WriteRow(StreamWriter perfmon, StreamWriter cpu, string key, DateTimeOffset time,
            double?[,] sums, Dictionary<string, string[]> machineRows)
        {
            string[] machine;
            machineRows.TryGetValue(key, out machine);      // values only, timestamps removed
            string perfmonRow;
            string cpuRow;
            FormatRow(time, sums, machine, out perfmonRow, out cpuRow);
            perfmon.WriteLine(perfmonRow);
            cpu.WriteLine(cpuRow);
        }

        internal static string PerfMonHeader(string machine, IEnumerable<string> pools)
        {
            var header = new List<string> { PerfMonHeaderCell() };
            foreach (string pool in pools)
            {
                foreach (string counter in PoolCounters)
                {
                    header.Add(string.Format(@"\\{0}\Much Better App Pool({1})\{2}", machine, PerfMonInstance(pool), counter));
                }
            }
            foreach (string counter in MachineCounters)
            {
                header.Add(string.Format(@"\\{0}\Much Better Machine\{1}", machine, counter));
            }
            return string.Join(",", header.Select(Quote));
        }

        // Instance names in a counter path can't hold the path's own delimiters. Windows writes
        // "(" and ")" as "[" and "]" and "/", "\\" and "#" as "_" in its own instance names; do the
        // same, so a pool such as "Pool (staging)" gives a path PerfMon can parse.
        internal static string PerfMonInstance(string pool)
        {
            return pool.Replace('(', '[').Replace(')', ']').Replace('/', '_').Replace('\\', '_').Replace('#', '_');
        }

        internal static string CpuHeader(IEnumerable<string> pools)
        {
            return Timestamps.Header + "," + string.Join(",", pools.Select(Csv));
        }

        /// <summary>One sample's rows: sums[pool, counter] in PoolCounters order; machine values in MachineCounters order.</summary>
        /// <remarks>The PerfMon row uses local time, as PerfMon does; the CPU row has utc_timestamp and local_timestamp.</remarks>
        internal static void FormatRow(DateTimeOffset time, double?[,] sums, IList<string> machine, out string perfmonRow, out string cpuRow)
        {
            int pools = sums.GetLength(0);
            var perfmon = new StringBuilder(Quote(time.DateTime.ToString(PerfMonTimeFormat, CultureInfo.InvariantCulture)));
            var cpu = new StringBuilder(Timestamps.Fields(time));
            for (int p = 0; p < pools; p++)
            {
                for (int k = 0; k < PoolCounters.Length; k++)
                {
                    perfmon.Append(',').Append(Quote(Format(sums[p, k])));
                }
                cpu.Append(',').Append(sums[p, 0].HasValue ? Format(sums[p, 0]) : string.Empty);
            }
            for (int k = 0; k < MachineCounters.Length; k++)
            {
                string value = machine != null && k < machine.Count ? machine[k] : string.Empty;    // older logs have fewer
                perfmon.Append(',').Append(Quote(string.IsNullOrEmpty(value) ? " " : value));
            }
            perfmonRow = perfmon.ToString();
            cpuRow = cpu.ToString();
        }

        internal static void Track(Result result, DateTime time)
        {
            result.Samples++;
            if (!result.First.HasValue)
            {
                result.First = time;
            }
            result.Last = time;
        }

        // The first header cell PerfMon writes: format version, time zone and its bias in minutes.
        internal static string PerfMonHeaderCell()
        {
            TimeZoneInfo zone = TimeZoneInfo.Local;
            int bias = (int)Math.Round(-zone.BaseUtcOffset.TotalMinutes);
            return string.Format(CultureInfo.InvariantCulture, "(PDH-CSV 4.0) ({0})({1})", zone.StandardName, bias);
        }

        internal static string Info(string folder, string machine, Result result, string from, string to)
        {
            string range = result.First.HasValue
                ? string.Format(Strings.ExportRange,
                    result.First.Value.ToString(LogTimeFormat, CultureInfo.InvariantCulture),
                    result.Last.Value.ToString(LogTimeFormat, CultureInfo.InvariantCulture))
                : Strings.ExportRangeNone;
            if (from != null || to != null)
            {
                range += string.Format(Strings.ExportRangeRequested, from ?? Strings.ExportStart, to ?? Strings.ExportEnd);
            }

            string text = string.Format(CultureInfo.InvariantCulture, Strings.ExportInfo,
                Brand.Title + "  " + Brand.Version + "  export",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                folder,
                machine,
                range,
                result.Samples,
                result.Pools,
                Brand.Copyright);
            return text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine) + Environment.NewLine;
        }

        // ---- Reading ----

        private static IEnumerable<string[]> ReadRows(string path, params int[] fieldCounts)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                reader.ReadLine();      // header
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string[] fields = SplitCsv(line);
                    if (Array.IndexOf(fieldCounts, fields.Length) >= 0)
                    {
                        yield return fields;    // a line still being written has too few fields: skipped
                    }
                }
            }
        }

        private static Dictionary<string, string[]> LoadMachineRows(string path)
        {
            var rows = new Dictionary<string, string[]>();
            if (File.Exists(path))
            {
                foreach (string[] fields in ReadRows(path, MachineFields, MachineFieldsV2, MachineFieldsV1))
                {
                    int t = TimeColumns(fields, MachineFields);
                    rows[fields[t - 1]] = fields.Skip(t).ToArray();     // keyed by local time text
                }
            }
            return rows;
        }

        private static string[] SplitCsv(string line)
        {
            var fields = new List<string>();
            var field = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (inQuotes)
                {
                    if (ch == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            field.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        field.Append(ch);
                    }
                }
                else if (ch == '"')
                {
                    inQuotes = true;
                }
                else if (ch == ',')
                {
                    fields.Add(field.ToString());
                    field.Clear();
                }
                else
                {
                    field.Append(ch);
                }
            }
            fields.Add(field.ToString());
            return fields.ToArray();
        }

        // ---- Values and bounds ----

        // 2 for the current layout (utc_timestamp,local_timestamp), 1 for older sessions.
        private static int SampleTimeColumns(string[] fields)
        {
            return fields.Length == SampleFieldsV1 ? 1 : 2;
        }

        private static int TimeColumns(string[] fields, int currentFieldCount)
        {
            return fields.Length == currentFieldCount ? 2 : 1;
        }

        // The row's moment: local_timestamp (with offset) in the current layout, the old local text otherwise.
        private static bool TryRowTime(string[] fields, int timeColumns, out DateTimeOffset time)
        {
            if (timeColumns == 2)
            {
                return Timestamps.TryParseLocal(fields[1], out time);
            }
            DateTime local;
            if (TryLogTime(fields[0], out local))
            {
                time = Timestamps.FromLocal(local);
                return true;
            }
            time = default(DateTimeOffset);
            return false;
        }

        private static bool TryLogTime(string text, out DateTime time)
        {
            return DateTime.TryParseExact(text, LogTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
        }

        private static DateTime? ResolveBound(string text, DateTime firstSample, string option)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            DateTime value;
            string[] full = { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm" };
            if (DateTime.TryParseExact(text, full, CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
            {
                return value;
            }

            TimeSpan timeOfDay;
            string[] clock = { @"hh\:mm\:ss", @"hh\:mm" };
            if (TimeSpan.TryParseExact(text, clock, CultureInfo.InvariantCulture, out timeOfDay))
            {
                return firstSample.Date + timeOfDay;
            }

            throw new ArgumentException(string.Format(Strings.ErrBadTime, option, text));
        }

        private static bool InRange(DateTime time, DateTime? start, DateTime? end)
        {
            return (!start.HasValue || time >= start.Value) && (!end.HasValue || time <= end.Value);
        }

        private static bool TryNumber(string text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static string Format(double? value)
        {
            return value.HasValue ? value.Value.ToString("0.###", CultureInfo.InvariantCulture) : " ";
        }

        internal static StreamWriter Create(string path)
        {
            return new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static string Csv(string value)
        {
            return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0 ? value : Quote(value);
        }
    }
}
