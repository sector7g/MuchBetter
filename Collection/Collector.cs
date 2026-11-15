// (c) 2017-2026 Sector 7G Consulting LLC
using System;
using System.Collections.Generic;
using System.Linq;
using S7G.mbpm.Core;

namespace S7G.mbpm.Collection
{
    /// <summary>
    /// One PDH query for everything MBPM shows: per-worker CPU, memory, uptime and .NET heap,
    /// per-worker share of HTTP 500 responses, per-core CPU and available memory, on this machine. Nothing is created or changed. Not thread-safe; the caller
    /// serializes access.
    /// </summary>
    internal sealed class Collector : ISnapshotSource
    {
        private readonly PdhQuery query;
        private readonly IntPtr pid;
        private readonly IntPtr cpu;
        private readonly IntPtr workingSet;
        private readonly IntPtr privateWorkingSet;
        private readonly IntPtr privateBytes;
        private readonly IntPtr elapsed;
        private readonly IntPtr cores;
        private readonly IntPtr availableMB;
        private readonly IntPtr committedBytes;
        private readonly IntPtr commitLimit;
        private readonly IntPtr iisWorkers;
        private readonly IntPtr clrPid;
        private readonly IntPtr gcHeap;
        private readonly IntPtr gcCommitted;
        private readonly IntPtr iis500;

        public Collector(string processName)
        {
            string process = @"\Process(" + processName + @"*)\";   // w3wp, w3wp#1, w3wp#2, ...

            query = new PdhQuery();
            try
            {
                pid = query.Add(process + "ID Process");
                cpu = query.Add(process + "% Processor Time");
                workingSet = query.TryAdd(process + "Working Set");
                privateWorkingSet = query.TryAdd(process + "Working Set - Private");
                privateBytes = query.TryAdd(process + "Private Bytes");
                elapsed = query.TryAdd(process + "Elapsed Time");

                // "Processor Information" covers every logical processor, including beyond 64 (processor
                // groups, instances "group,index"). Fall back to "Processor" where it isn't available.
                cores = query.TryAdd(@"\Processor Information(*)\% Processor Time");
                if (cores == IntPtr.Zero)
                {
                    cores = query.TryAdd(@"\Processor(*)\% Processor Time");
                }

                availableMB = query.TryAdd(@"\Memory\Available MBytes");
                committedBytes = query.TryAdd(@"\Memory\Committed Bytes");
                commitLimit = query.TryAdd(@"\Memory\Commit Limit");

                // IIS names these instances "<PID>_<pool>": pool names in the same snapshot as the
                // figures, with no WMI. Any counter of the set will do; only the instance names are used.
                iisWorkers = query.TryAdd(@"\W3SVC_W3WP(*)\Active Requests");

                // .NET heap per worker. This set uses their own "w3wp#n" names, so
                // they're joined to workers by their own Process ID counter. Workers without .NET
                // (No Managed Code pools) simply have no instance here.
                string clr = "(" + processName + @"*)\";
                clrPid = query.TryAdd(@"\.NET CLR Memory" + clr + "Process ID");
                gcHeap = query.TryAdd(@"\.NET CLR Memory" + clr + "# Bytes in all Heaps");
                gcCommitted = query.TryAdd(@"\.NET CLR Memory" + clr + "# Total committed Bytes");

                // Share of the worker's responses that were HTTP 500 (status 500 only; IIS has no
                // counter for the whole 5xx range). Same "<PID>_<pool>" instances as above.
                iis500 = query.TryAdd(@"\W3SVC_W3WP(*)\% 500 HTTP Response Sent");

                // Rate counters need two samples; this first one primes them.
                query.Collect();
            }
            catch
            {
                query.Dispose();
                throw;
            }
        }

        public Snapshot Collect(DateTime time)
        {
            var snapshot = new Snapshot { Time = time };
            if (!query.Collect())
            {
                return snapshot;
            }

            List<InstanceValue> pids = PdhQuery.ReadArray(pid);
            double?[] cpuValues = Align(pids, PdhQuery.ReadArray(cpu));
            double?[] workingSetValues = Align(pids, PdhQuery.ReadArray(workingSet));
            double?[] privateWorkingSetValues = Align(pids, PdhQuery.ReadArray(privateWorkingSet));
            double?[] commitValues = Align(pids, PdhQuery.ReadArray(privateBytes));
            double?[] elapsedValues = Align(pids, PdhQuery.ReadArray(elapsed));

            for (int i = 0; i < pids.Count; i++)
            {
                if (!pids[i].Valid)
                {
                    continue;
                }
                int id = (int)Math.Round(pids[i].Value);
                if (id <= 0)
                {
                    continue;
                }

                snapshot.Processes.Add(new ProcessSample
                {
                    Instance = pids[i].Name,
                    Pid = id,
                    Cpu = cpuValues[i],
                    WorkingSet = workingSetValues[i],
                    PrivateWorkingSet = privateWorkingSetValues[i],
                    Commit = commitValues[i],
                    ElapsedSeconds = elapsedValues[i],
                });
            }

            ReadClr(snapshot);
            ReadIisErrors(snapshot);
            ReadCores(snapshot);
            snapshot.AvailableMB = PdhQuery.ReadValue(availableMB);
            snapshot.CommittedBytes = PdhQuery.ReadValue(committedBytes);
            snapshot.CommitLimitBytes = PdhQuery.ReadValue(commitLimit);
            ReadPoolNames(snapshot);
            return snapshot;
        }

        public void Dispose()
        {
            query.Dispose();
        }

        // Finds each process's value in another counter of the same object.
        // PDH may report several instances under one base name ("w3wp", "w3wp", ...) instead of "w3wp#1",
        // so names alone can't pair them. Arrays from one collection of one object come in the same order,
        // so when the name sequences match, position pairs them. If names are unique (e.g. with
        // ProcessNameFormat=2), pair by name. Otherwise report nothing rather than guess.
        private static double?[] Align(List<InstanceValue> reference, List<InstanceValue> other)
        {
            var result = new double?[reference.Count];

            bool sameSequence = reference.Count == other.Count;
            for (int i = 0; sameSequence && i < reference.Count; i++)
            {
                sameSequence = string.Equals(reference[i].Name, other[i].Name, StringComparison.OrdinalIgnoreCase);
            }

            if (sameSequence)
            {
                for (int i = 0; i < reference.Count; i++)
                {
                    result[i] = other[i].Valid ? other[i].Value : (double?)null;
                }
                return result;
            }

            var byName = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var duplicate = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (InstanceValue item in other)
            {
                if (!item.Valid)
                {
                    continue;
                }
                if (byName.ContainsKey(item.Name))
                {
                    duplicate.Add(item.Name);
                }
                byName[item.Name] = item.Value;
            }

            for (int i = 0; i < reference.Count; i++)
            {
                double value;
                if (!duplicate.Contains(reference[i].Name) && byName.TryGetValue(reference[i].Name, out value))
                {
                    result[i] = value;
                }
            }
            return result;
        }

        private void ReadCores(Snapshot snapshot)
        {
            var perCore = new List<KeyValuePair<long, double?>>();
            foreach (InstanceValue item in PdhQuery.ReadArray(cores))
            {
                double? value = item.Valid ? item.Value : (double?)null;
                if (item.Name == "_Total")
                {
                    snapshot.TotalCpu = value;
                    continue;
                }

                long key;
                if (TryCoreKey(item.Name, out key))      // skips per-group totals such as "0,_Total"
                {
                    perCore.Add(new KeyValuePair<long, double?>(key, value));
                }
            }

            perCore.Sort((a, b) => a.Key.CompareTo(b.Key));
            snapshot.Cores = perCore.Select(e => e.Value).ToArray();
        }

        // "<PID>_<pool>" -> PID and pool. The PID is all digits, so the first underscore splits them;
        // pool names may contain underscores themselves. "_Total" has no PID and is skipped.
        private void ReadPoolNames(Snapshot snapshot)
        {
            snapshot.HasPoolCounters = iisWorkers != IntPtr.Zero;
            foreach (InstanceValue item in PdhQuery.ReadArray(iisWorkers))
            {
                int pid;
                string pool;
                if (TryParsePoolInstance(item.Name, out pid, out pool))
                {
                    snapshot.PoolByPid[pid] = pool;
                }
            }
        }

        /// <summary>Splits a W3SVC_W3WP instance name, "PID_pool", at its first underscore.</summary>
        public static bool TryParsePoolInstance(string name, out int pid, out string pool)
        {
            pid = 0;
            pool = null;
            name = name ?? string.Empty;
            int underscore = name.IndexOf('_');
            if (underscore > 0 && underscore + 1 < name.Length
                && int.TryParse(name.Substring(0, underscore), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out pid))
            {
                pool = name.Substring(underscore + 1);
                return true;
            }
            pid = 0;
            return false;
        }

        // .NET CLR Memory instances -> the worker with the same Process ID.
        private void ReadClr(Snapshot snapshot)
        {
            List<InstanceValue> pids = PdhQuery.ReadArray(clrPid);
            if (pids.Count == 0)
            {
                return;
            }
            double?[] heap = Align(pids, PdhQuery.ReadArray(gcHeap));
            double?[] committed = Align(pids, PdhQuery.ReadArray(gcCommitted));

            var byPid = new Dictionary<int, ProcessSample>();
            foreach (ProcessSample process in snapshot.Processes)
            {
                byPid[process.Pid] = process;
            }
            for (int i = 0; i < pids.Count; i++)
            {
                ProcessSample process;
                if (pids[i].Valid && byPid.TryGetValue((int)Math.Round(pids[i].Value), out process))
                {
                    process.GcHeapBytes = heap[i];
                    process.GcCommittedBytes = committed[i];
                }
            }
        }

        // \W3SVC_W3WP(<PID>_<pool>)\% 500 HTTP Response Sent -> the worker with that PID. The counter
        // is a fraction of the responses sent since the previous sample; with no responses in between
        // PDH can't compute it, which here means 0 %.
        private void ReadIisErrors(Snapshot snapshot)
        {
            var byPid = new Dictionary<int, ProcessSample>();
            foreach (ProcessSample process in snapshot.Processes)
            {
                byPid[process.Pid] = process;
            }
            foreach (InstanceValue item in PdhQuery.ReadArray(iis500))
            {
                int pid;
                string pool;
                ProcessSample process;
                if (TryParsePoolInstance(item.Name, out pid, out pool) && byPid.TryGetValue(pid, out process))
                {
                    process.Http500Percent = item.Valid ? item.Value : 0.0;
                }
            }
        }

        // "group,index" (Processor Information) or "index" (Processor) -> sortable key.
        private static bool TryCoreKey(string name, out long key)
        {
            key = 0;
            int group = 0;
            int index;
            int comma = name.IndexOf(',');
            if (comma >= 0)
            {
                if (!int.TryParse(name.Substring(0, comma), out group) || !int.TryParse(name.Substring(comma + 1), out index))
                {
                    return false;
                }
            }
            else if (!int.TryParse(name, out index))
            {
                return false;
            }

            key = ((long)group << 32) | (uint)index;
            return true;
        }
    }
}
