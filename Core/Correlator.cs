// (c) 2017-2026 Sector 7G Consulting LLC
using System;
using System.Collections.Generic;

namespace S7G.mbpm.Core
{
    /// <summary>Joins one sample with the PID -> pool map. Pure: no I/O and no shared state.</summary>
    internal static class Correlator
    {
        private const double BytesPerMB = 1024.0 * 1024.0;

        public static List<PoolRow> Build(Snapshot snapshot, IDictionary<int, string> pidToPool, int cores)
        {
            if (cores < 1)
            {
                cores = 1;
            }

            var rows = new List<PoolRow>();
            var sampled = new HashSet<int>();

            foreach (ProcessSample process in snapshot.Processes)
            {
                if (!sampled.Add(process.Pid))
                {
                    continue;
                }

                string pool;
                if (!pidToPool.TryGetValue(process.Pid, out pool))
                {
                    pool = Strings.UnknownPool;
                }

                double? oneCore = null;
                double? allCores = null;
                if (process.Cpu.HasValue)
                {
                    // A sample taken right after instances are renumbered can be computed across two
                    // different processes; clamp it to what is physically possible.
                    oneCore = Math.Max(0.0, Math.Min(process.Cpu.Value, 100.0 * cores));
                    allCores = oneCore.Value / cores;
                }

                rows.Add(new PoolRow
                {
                    Pool = pool,
                    Pid = process.Pid,
                    CpuOne = oneCore,
                    CpuAll = allCores,
                    WorkingSetMB = ToMB(process.WorkingSet),
                    PrivateWorkingSetMB = ToMB(process.PrivateWorkingSet),
                    CommitMB = ToMB(process.Commit),
                    Uptime = process.ElapsedSeconds.HasValue && process.ElapsedSeconds.Value >= 0
                        ? TimeSpan.FromSeconds(process.ElapsedSeconds.Value)
                        : (TimeSpan?)null,
                });
            }

            foreach (KeyValuePair<int, string> entry in pidToPool)
            {
                if (!sampled.Contains(entry.Key))
                {
                    rows.Add(new PoolRow { Pool = entry.Value, Pid = entry.Key, NotInSample = true });
                }
            }

            return rows;
        }

        private static double? ToMB(double? bytes)
        {
            return bytes.HasValue ? bytes.Value / BytesPerMB : (double?)null;
        }
    }
}
