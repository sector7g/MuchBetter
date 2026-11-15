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
    /// Writes the exports while MBPM runs, one row per sample, flushed every time: the same
    /// mbpm_export_perfmon.csv and mbpm_export_cpu.csv as "mbpm export" builds, so the session is
    /// logged as it happens, as a PerfMon data collector set logs. The files can be opened in
    /// Performance Monitor at any time, and a crash loses at most the sample being written.
    ///
    /// A CSV can't gain columns once its header is written (PerfMon's own CSV logs can't either).
    /// When a pool first appears mid-session, both files are rewritten once with its columns added,
    /// blank for the earlier samples. That costs a pass over the file so far, and is rare: pools
    /// come and go far less often than their workers.
    /// </summary>
    internal sealed class LiveExport : IDisposable
    {
        private readonly string folder;
        private readonly string machine;
        private readonly List<string> pools = new List<string>();     // column order (sorted)
        private readonly Exporter.Result result;
        private StreamWriter perfmon;
        private StreamWriter cpu;
        private bool closed;

        public LiveExport(string folder, string machine)
        {
            this.folder = folder;
            this.machine = machine;
            result = new Exporter.Result
            {
                PerfMonPath = Path.Combine(folder, Exporter.PerfMonFile),
                CpuPath = Path.Combine(folder, Exporter.CpuFile),
                InfoPath = Path.Combine(folder, Exporter.InfoFile),
            };
            perfmon = Exporter.Create(result.PerfMonPath);
            cpu = Exporter.Create(result.CpuPath);
            perfmon.WriteLine(Exporter.PerfMonHeader(machine, pools));
            cpu.WriteLine(Exporter.CpuHeader(pools));
            Flush();
            WriteInfo();
        }

        /// <summary>Samples, pools and paths so far.</summary>
        public Exporter.Result Result
        {
            get
            {
                result.Pools = pools.Count;
                return result;
            }
        }

        /// <summary>How many times the files were rewritten for a new pool.</summary>
        public int Rewrites { get; private set; }

        /// <summary>Appends one sample: the rows of that sample and the machine values (SessionLog.MachineValues).</summary>
        public void Append(DateTimeOffset time, IEnumerable<PoolRow> rows, IList<string> machineValues)
        {
            List<PoolRow> sampled = rows.Where(r => !r.NotInSample).ToList();
            List<string> added = sampled.Select(r => r.Pool).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(p => !pools.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
            if (added.Count > 0)
            {
                AddPools(added);
            }

            // Web garden: a pool's workers are summed, as in "mbpm export".
            var sums = new double?[pools.Count, Exporter.PoolCounters.Length];
            foreach (PoolRow row in sampled)
            {
                int p = pools.FindIndex(x => x.Equals(row.Pool, StringComparison.OrdinalIgnoreCase));
                double?[] values = { row.CpuOne, row.CpuAll, row.WorkingSetMB, row.PrivateWorkingSetMB, row.CommitMB };
                for (int k = 0; k < values.Length; k++)
                {
                    if (values[k].HasValue)
                    {
                        sums[p, k] = (sums[p, k] ?? 0) + Round(values[k].Value);
                    }
                }
            }

            string perfmonRow;
            string cpuRow;
            Exporter.FormatRow(time, sums, machineValues, out perfmonRow, out cpuRow);
            perfmon.WriteLine(perfmonRow);
            cpu.WriteLine(cpuRow);
            Flush();
            Exporter.Track(result, time.DateTime);
        }

        /// <summary>Writes export_info.txt for what's been logged so far (also done on Dispose).</summary>
        public void WriteInfo()
        {
            Exporter.Result current = Result;
            File.WriteAllText(current.InfoPath, Exporter.Info(folder, machine, current, null, null), new UTF8Encoding(false));
        }

        public void Dispose()
        {
            if (closed)
            {
                return;
            }
            closed = true;
            perfmon.Dispose();
            cpu.Dispose();
            WriteInfo();
        }

        private void Flush()
        {
            perfmon.Flush();
            cpu.Flush();
        }

        // Exactly what the raw logs hold (3 decimals as text, read back), so the live export and
        // "mbpm export" from the raw logs give identical numbers.
        private static double Round(double value)
        {
            return double.Parse(value.ToString("0.###", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }

        // Rewrites both files with the new pools' columns in place (sorted), blank for earlier rows.
        private void AddPools(List<string> added)
        {
            var newPools = pools.Concat(added).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            int[] oldIndex = newPools.Select(p => pools.FindIndex(x => x.Equals(p, StringComparison.OrdinalIgnoreCase))).ToArray();
            int counters = Exporter.PoolCounters.Length;

            perfmon.Dispose();
            cpu.Dispose();

            // PerfMon file: every data cell is quoted and has no commas inside, so a row splits on commas.
            result.PerfMonPath = Rewrite(result.PerfMonPath, Exporter.PerfMonHeader(machine, newPools), line =>
            {
                string[] cells = line.Split(',');
                var output = new List<string>(1 + newPools.Count * counters + Exporter.MachineCounters.Length) { cells[0] };
                foreach (int old in oldIndex)
                {
                    for (int k = 0; k < counters; k++)
                    {
                        output.Add(old >= 0 ? cells[1 + old * counters + k] : "\" \"");
                    }
                }
                output.AddRange(cells.Skip(1 + pools.Count * counters));
                return string.Join(",", output);
            });

            // CPU file: utc_timestamp, local_timestamp, then one plain number (or nothing) per pool.
            result.CpuPath = Rewrite(result.CpuPath, Exporter.CpuHeader(newPools), line =>
            {
                string[] cells = line.Split(',');
                var output = new List<string>(2 + newPools.Count) { cells[0], cells[1] };
                foreach (int old in oldIndex)
                {
                    output.Add(old >= 0 && 2 + old < cells.Length ? cells[2 + old] : string.Empty);
                }
                return string.Join(",", output);
            });

            pools.Clear();
            pools.AddRange(newPools);
            perfmon = OpenForAppend(result.PerfMonPath);
            cpu = OpenForAppend(result.CpuPath);
            Rewrites++;
        }

        /// <summary>Path of the file being written: changes only if a rewrite found the old file in use.</summary>
        public string MovedTo { get; private set; }

        // Returns the path now holding the data: the same one, or, if another program holds the old
        // file open so it can't be replaced, "<name> (2).csv" and so on (the old file keeps the rows
        // up to the new pool; the new one has them all).
        private string Rewrite(string path, string header, Func<string, string> convert)
        {
            string temp = path + ".tmp";
            using (var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.UTF8))
            using (StreamWriter writer = Exporter.Create(temp))
            {
                reader.ReadLine();      // old header
                writer.WriteLine(header);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length > 0)
                    {
                        writer.WriteLine(convert(line));
                    }
                }
            }
            try
            {
                File.Delete(path);
                File.Move(temp, path);
                return path;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            string stem = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path));
            for (int n = 2; ; n++)
            {
                string alternative = stem + " (" + n + ")" + Path.GetExtension(path);
                if (!File.Exists(alternative))
                {
                    File.Move(temp, alternative);
                    MovedTo = alternative;
                    return alternative;
                }
            }
        }

        private static StreamWriter OpenForAppend(string path)
        {
            return new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        }
    }
}
