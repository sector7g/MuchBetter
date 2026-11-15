// (c) 2017-2026 Sector 7G Consulting LLC
using System;
using System.Collections.Generic;

namespace S7G.mbpm.Core
{
    /// <summary>One worker process instance from a PDH sample.</summary>
    internal sealed class ProcessSample
    {
        public string Instance;
        public int Pid;
        public double? Cpu;             // % of one core
        public double? WorkingSet;          // Task Manager: Working set (memory)
        public double? PrivateWorkingSet;   // Task Manager: Memory (private working set)
        public double? Commit;              // Private Bytes; Task Manager: Commit size
        public double? ElapsedSeconds;      // time since the process started
        public double? GcHeapBytes;         // .NET CLR Memory\# Bytes in all Heaps
        public double? GcCommittedBytes;    // .NET CLR Memory\# Total committed Bytes
        public double? Http500Percent;      // W3SVC_W3WP\% 500 HTTP Response Sent
    }

    /// <summary>Everything one PDH collection returned.</summary>
    internal sealed class Snapshot
    {
        public DateTime Time;               // local clock time (display, PerfMon export)
        public DateTimeOffset Stamp;        // the same moment with its UTC offset, captured together
        public List<ProcessSample> Processes = new List<ProcessSample>();
        public double?[] Cores = new double?[0];   // per logical processor, in processor order
        public double? TotalCpu;
        public double? AvailableMB;
        public double? CommittedBytes;      // machine commit charge
        public double? CommitLimitBytes;    // machine commit limit (RAM + page files)

        // Pool names from IIS's \W3SVC_W3WP(<PID>_<pool>) instances, from the same collection.
        public Dictionary<int, string> PoolByPid = new Dictionary<int, string>();
        public bool HasPoolCounters;        // false if the W3SVC_W3WP counter set isn't available


        // Filled in by the Sampler: MBPM's own cost and timing, measured where MBPM runs.
        public double? MbpmCpu;             // MBPM's CPU since the previous sample, % of all cores of this machine
        public double DelayMs;              // how late this collection started against its schedule
        public long MissedSamples;          // samples skipped so far because collection fell a whole interval behind

        /// <summary>The sample's moment with its UTC offset (from Time if none was captured).</summary>
        public DateTimeOffset When
        {
            get { return Stamp == default(DateTimeOffset) ? Timestamps.FromLocal(Time) : Stamp; }
        }
    }

    /// <summary>Where snapshots come from: PDH (Collector) or the mock (MockSource).</summary>
    internal interface ISnapshotSource : IDisposable
    {
        Snapshot Collect(DateTime time);
    }

    /// <summary>Where the WMI fallback for pool names comes from: WMI (PoolMapper) or the mock.</summary>
    internal interface IPoolNameSource
    {
        /// <summary>Worker processes whose pool couldn't be read (e.g. not elevated).</summary>
        int HiddenCount { get; }

        /// <summary>A new PID -> pool map on every call.</summary>
        Dictionary<int, string> GetPidToPool();

        /// <summary>Total physical memory in MB, or null if unknown.</summary>
        double? GetTotalPhysicalMemoryMB();

        /// <summary>The account the process runs as (DOMAIN\name), or null if it can't be read.</summary>
        string GetUser(int pid);
    }

    /// <summary>One table row: a worker process labeled with its app pool.</summary>
    internal sealed class PoolRow
    {
        public string Pool;
        public int Pid;
        public double? CpuOne;          // % of one core
        public double? CpuAll;          // % of the machine
        public double? WorkingSetMB;
        public double? PrivateWorkingSetMB;
        public double? CommitMB;
        public TimeSpan? Uptime;
        public double? GcMB;            // .NET heap: # Bytes in all Heaps
        public double? NativeMB;        // estimate: Private Bytes - .NET # Total committed Bytes
        public double? Http500Percent;  // share of responses that were HTTP 500, since the previous sample
        public string User;             // the account the worker runs as; null until known
        public bool NotInSample;        // WMI sees the process, the last sample doesn't

        public string Key
        {
            get { return Pool + "|" + Pid; }
        }
    }

    /// <summary>
    /// A change in a pool's worker processes, worded neutrally: a PID change shows a worker was
    /// replaced, not why (recycle, crash, idle timeout, iisreset and a manual kill all look alike).
    /// </summary>
    internal sealed class WorkerEvent
    {
        public DateTime Time;
        public DateTimeOffset Stamp;        // set from the sample that showed the change

        public DateTimeOffset When
        {
            get { return Stamp == default(DateTimeOffset) ? Timestamps.FromLocal(Time) : Stamp; }
        }

        public string Pool;
        public string Kind;     // started, replaced, overlap, exited, gone
        public string Detail;
    }
}
