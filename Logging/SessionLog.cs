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
    /// One session's logs on the monitoring machine, in a user location (nothing on the target):
    ///   &lt;root&gt;\&lt;machine&gt;\&lt;yyyyMMdd-HHmmss&gt;\samples.csv   one row per worker per sample
    ///   &lt;root&gt;\&lt;machine&gt;\&lt;yyyyMMdd-HHmmss&gt;\machine.csv   machine CPU, memory and commit per sample
    ///   &lt;root&gt;\&lt;machine&gt;\&lt;yyyyMMdd-HHmmss&gt;\events.csv    worker changes
    ///   &lt;root&gt;\&lt;machine&gt;\&lt;yyyyMMdd-HHmmss&gt;\pid_map.csv   pool names and PIDs per poll (--debug-logs only)
    /// The PerfMon export is written live (LiveExport), so samples.csv and machine.csv are written only
    /// when asked for: with --keep-raw (to re-slice later with "mbpm export --from/--to") or with
    /// --no-export (then they are the only record). Everything is written from the main loop.
    /// Files are shared for reading, so they can be opened while a session runs.
    /// </summary>
    internal sealed class SessionLog : IDisposable
    {

        private StreamWriter samples;    // null unless raw logs are on
        private StreamWriter machine;    // null unless raw logs are on
        private readonly StreamWriter events;
        private readonly StreamWriter pidMap;     // null unless debug logs are on
        private string lastMapHeader;

        public const string SamplesFile = "samples.csv";
        public const string MachineFile = "machine.csv";

        public SessionLog(string root, string machine, bool debugLogs, bool rawLogs = true)
        {
            Folder = Path.Combine(root, machine, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(Folder);
            RawLogs = rawLogs;

            events = Open(Path.Combine(Folder, "events.csv"));
            events.WriteLine(Timestamps.Header + ",pool,change,detail");
            events.Flush();

            if (debugLogs)
            {
                pidMap = Open(Path.Combine(Folder, "pid_map.csv"));
            }
            if (rawLogs)
            {
                EnableRawLogs();
            }
        }

        /// <summary>Starts samples.csv and machine.csv (if not already): from the start, or as a fallback.</summary>
        public void EnableRawLogs()
        {
            if (samples != null)
            {
                return;
            }
            RawLogs = true;
            samples = Open(Path.Combine(Folder, SamplesFile));
            samples.WriteLine(Timestamps.Header + ",pool,pid,cpu_one_core,cpu_all_cores,ws_mb,private_ws_mb,commit_mb,uptime_s");
            samples.Flush();

            this.machine = Open(Path.Combine(Folder, MachineFile));
            this.machine.WriteLine(Timestamps.Header + ",cpu_percent,mem_used_mb,mem_total_mb,commit_mb,commit_limit_mb,mbpm_cpu_percent,sample_delay_ms,missed_samples");
            this.machine.Flush();
        }

        public string Folder { get; private set; }

        /// <summary>True when samples.csv and machine.csv are being written.</summary>
        public bool RawLogs { get; private set; }

        public void WriteSamples(DateTimeOffset time, IEnumerable<PoolRow> rows)
        {
            if (samples == null)
            {
                return;
            }
            string timestamp = Timestamps.Fields(time);
            foreach (PoolRow row in rows)
            {
                if (row.NotInSample)
                {
                    continue;
                }
                samples.WriteLine(string.Join(",",
                    timestamp,
                    Csv(row.Pool),
                    row.Pid.ToString(CultureInfo.InvariantCulture),
                    Number(row.CpuOne),
                    Number(row.CpuAll),
                    Number(row.WorkingSetMB),
                    Number(row.PrivateWorkingSetMB),
                    Number(row.CommitMB),
                    row.Uptime.HasValue ? row.Uptime.Value.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) : string.Empty));
            }
            samples.Flush();
        }

        // Same timestamp text as samples.csv, so the export can match the two.
        public void WriteMachine(Snapshot snapshot, double? totalMB)
        {
            if (machine == null)
            {
                return;
            }
            machine.WriteLine(Timestamps.Fields(snapshot.When) + "," + string.Join(",", MachineValues(snapshot, totalMB)));
            machine.Flush();
        }

        /// <summary>
        /// The machine values of one sample, as text, in the order of the export's Much Better Machine
        /// counters: CPU %, memory used MB, memory total MB, commit MB, commit limit MB, Much Better CPU %,
        /// sample delay ms, missed samples. Shared by machine.csv and the live export.
        /// </summary>
        public static string[] MachineValues(Snapshot snapshot, double? totalMB)
        {
            const double BytesPerMB = 1024.0 * 1024.0;
            double? used = totalMB.HasValue && snapshot.AvailableMB.HasValue ? totalMB.Value - snapshot.AvailableMB.Value : (double?)null;
            return new[]
            {
                Number(snapshot.TotalCpu),
                Number(used),
                Number(totalMB),
                Number(snapshot.CommittedBytes.HasValue ? snapshot.CommittedBytes.Value / BytesPerMB : (double?)null),
                Number(snapshot.CommitLimitBytes.HasValue ? snapshot.CommitLimitBytes.Value / BytesPerMB : (double?)null),
                Number(snapshot.MbpmCpu),
                snapshot.DelayMs.ToString("0", CultureInfo.InvariantCulture),
                snapshot.MissedSamples.ToString(CultureInfo.InvariantCulture),
            };
        }

        public void WriteEvent(WorkerEvent change)
        {
            events.WriteLine(string.Join(",",
                Timestamps.Fields(change.When),
                Csv(change.Pool),
                change.Kind,
                Csv(change.Detail)));
            events.Flush();
        }

        // A header of pool names, then one row of PIDs per poll; a new header whenever the pool list changes.
        public void WritePidMap(IDictionary<int, string> map)
        {
            if (pidMap == null || map.Count == 0)
            {
                return;
            }

            List<KeyValuePair<int, string>> ordered = map
                .OrderBy(e => e.Value, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Key)
                .ToList();
            string timestamp = Timestamps.Fields(DateTimeOffset.Now);
            string header = Timestamps.Header + "," + string.Join(",", ordered.Select(e => Csv(e.Value)));

            if (header != lastMapHeader)
            {
                pidMap.WriteLine("#" + timestamp);
                pidMap.WriteLine(header);
                lastMapHeader = header;
            }
            pidMap.WriteLine(timestamp + "," + string.Join(",", ordered.Select(e => e.Key)));
            pidMap.Flush();
        }

        public void Dispose()
        {
            if (samples != null)
            {
                samples.Dispose();
            }
            if (machine != null)
            {
                machine.Dispose();
            }
            events.Dispose();
            if (pidMap != null)
            {
                pidMap.Dispose();
            }
        }

        private static StreamWriter Open(string path)
        {
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            return new StreamWriter(stream, new UTF8Encoding(false));
        }

        private static string Number(double? value)
        {
            return value.HasValue ? value.Value.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty;
        }

        private static string Csv(string value)
        {
            if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
            {
                return value;
            }
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
