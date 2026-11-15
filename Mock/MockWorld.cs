// (c) 2017-2026 Sector 7G Consulting LLC
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using S7G.mbpm.Core;

namespace S7G.mbpm.Mock
{
    /// <summary>
    /// A simulated IIS server for --mock (64 cores, 100 app pools by default): one worker per pool,
    /// with CPU and memory that wander, and an occasional overlapped recycle so worker changes
    /// show. Chaos (0-10, default 0) adds trouble in proportion: CPU spikes, memory leaks (native
    /// or managed) that end in a crash, outright crashes with a gap before the pool restarts, more
    /// recycles, and bursts of HTTP 500s. Deterministic for a given seed and sequence of times (no
    /// clock, no sleeping). The numbers stay physically possible: workers never add up to more than
    /// the machine.
    /// </summary>
    internal sealed class MockWorld
    {
        private const double MB = 1024.0 * 1024.0;
        private const double RecyclesPerHour = 30;
        public const int MaxChaos = 10;

        private readonly Random random;
        private readonly double totalMemoryMB;
        private readonly List<Worker> workers = new List<Worker>();
        private DateTime? last;
        private int nextPid = 4000;
        private readonly Dictionary<int, DateTime> restartAt = new Dictionary<int, DateTime>();   // crashed pool -> when it comes back

        private sealed class Worker
        {
            public int Pid;
            public int Pool;                // 1-based pool number
            public DateTime Started;
            public DateTime? ExitAt;        // the old worker of an overlapped recycle
            public double BaseCpu;          // % of one core
            public double Cpu;
            public double WorkingSetMB;     // before any leak
            public double LeakedMB;         // grows while leaking; all of it committed
            public double LeakRate;         // MB/s; 0 when not leaking
            public bool LeakNative;         // native leak (GC heap flat) or managed (GC heap grows)
            public double CrashAtMB;        // a leaking worker crashes at this working set
            public DateTime? SpikeUntil;    // CPU spike
            public double SpikeCpu;         // % of one core during the spike
            public DateTime? ErrorsUntil;   // burst of HTTP 500s
            public double ErrorRate;        // % of responses during the burst
            public bool Managed;            // false: a "No Managed Code" pool, no .NET counters
            public double GcShare;          // part of the working set that is .NET heap
            public double Http500;          // % of responses that were HTTP 500
        }

        public MockWorld(int cores, int pools, int seed, int chaos = 0)
        {
            Cores = cores;
            Pools = pools;
            Chaos = Math.Max(0, Math.Min(MaxChaos, chaos));
            random = new Random(seed);
            totalMemoryMB = Math.Max(16, pools) * 1024.0;     // 1 GB per pool, at least 16 GB: room for every worker
        }

        public int Cores { get; private set; }

        /// <summary>0 (calm) to 10 (constant trouble).</summary>
        public int Chaos { get; private set; }

        /// <summary>Number of app pools; may be changed between samples.</summary>
        public int Pools { get; set; }

        public double TotalMemoryMB
        {
            get { return totalMemoryMB; }
        }

        // With chaos, every third pool gets a name of the kind that trips up tools: spaces, dots,
        // underscores (the W3SVC_W3WP "PID_pool" split), leading digits, parentheses (PerfMon counter
        // paths), an apostrophe, non-ASCII scripts, one character, and a very long name.
        private static readonly string[] OddNames =
        {
            "Default Web Site Pool",
            "api_v2_internal",
            "2024_Reports",
            ".NET v4.5 Classic",
            "Pool (staging)",
            "O'Brien Portal",
            "Ünïcødé-Pöol",
            "日本語サイト",
            "Ελληνικά",
            "a",
            "Customer-Facing-Self-Service-Portal-Production-East-Region-Blue",
            "double  spaced   name",
            "Trailing.Dot.",
            "ROOT_api_v1_legacy_",
        };

        public string PoolName(int pool)
        {
            if (Chaos > 0 && pool % 3 == 0)
            {
                int odd = pool / 3 - 1;
                string name = OddNames[odd % OddNames.Length];
                // Pool names are unique in IIS: number the second round of odd names.
                return odd < OddNames.Length ? name : name + " " + (odd / OddNames.Length + 1).ToString(CultureInfo.InvariantCulture);
            }
            return "AppPool" + pool.ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The account a simulated worker runs as: mostly the pool's own identity, with a few
        /// built-in and domain service accounts mixed in, as on a real server.
        /// </summary>
        public string UserFor(int pid)
        {
            Worker worker = workers.FirstOrDefault(w => w.Pid == pid);
            if (worker == null)
            {
                return null;
            }
            if (worker.Pool % 11 == 0)
            {
                return @"CORP\svc_" + PoolName(worker.Pool).ToLowerInvariant();
            }
            if (worker.Pool % 7 == 0)
            {
                return @"NT AUTHORITY\NETWORK SERVICE";
            }
            return @"IIS APPPOOL\" + PoolName(worker.Pool);
        }

        /// <summary>The PID -> pool map WMI would return.</summary>
        public Dictionary<int, string> PidToPool()
        {
            return workers.ToDictionary(w => w.Pid, w => PoolName(w.Pool));
        }

        /// <summary>Advances to <paramref name="now"/> and returns what a collection would see.</summary>
        public Snapshot Next(DateTime now)
        {
            double seconds = last.HasValue ? Math.Max(0.001, (now - last.Value).TotalSeconds) : 1.0;
            last = now;

            workers.RemoveAll(w => (w.ExitAt.HasValue && w.ExitAt.Value <= now) || w.Pool > Pools);
            if (Chaos > 0)
            {
                Crash(now, seconds);
            }
            for (int pool = 1; pool <= Pools; pool++)
            {
                DateTime back;
                if (restartAt.TryGetValue(pool, out back))
                {
                    if (back > now)
                    {
                        continue;       // crashed: no worker until IIS starts a new one
                    }
                    restartAt.Remove(pool);
                }
                if (!workers.Any(w => w.Pool == pool && !w.ExitAt.HasValue))
                {
                    Start(pool, now);
                }
            }

            // An occasional overlapped recycle: the new worker starts, the old one exits seconds later.
            // Chaos makes them more frequent.
            double recycles = RecyclesPerHour * (1 + Chaos * 3);
            if (workers.Count > 0 && random.NextDouble() < recycles / 3600.0 * seconds)
            {
                Worker old = workers[random.Next(workers.Count)];
                if (!old.ExitAt.HasValue)
                {
                    Start(old.Pool, now);
                    old.ExitAt = now.AddSeconds(2 + random.Next(4));
                }
            }

            foreach (Worker w in workers)
            {
                w.Cpu = w.BaseCpu * (0.5 + random.NextDouble());
                w.WorkingSetMB = Math.Max(80, w.WorkingSetMB + (random.NextDouble() - 0.5) * 6);
                w.LeakedMB += w.LeakRate * seconds;
            }
            if (Chaos > 0)
            {
                StartTrouble(now, seconds);
            }

            // A few pools return the odd HTTP 500, now and then; chaos adds bursts anywhere.
            foreach (Worker w in workers)
            {
                if (w.SpikeUntil.HasValue && w.SpikeUntil.Value > now)
                {
                    w.Cpu = w.SpikeCpu * (0.9 + random.NextDouble() * 0.2);
                }
                w.Http500 = w.ErrorsUntil.HasValue && w.ErrorsUntil.Value > now ? w.ErrorRate * (0.5 + random.NextDouble())
                    : w.Pool % 13 == 0 && random.NextDouble() < 0.3 ? random.NextDouble() * 5
                    : 0;
            }
            double total = workers.Sum(w => w.Cpu);
            double capacity = Cores * 100.0 * 0.97;
            if (total > capacity)
            {
                foreach (Worker w in workers)
                {
                    w.Cpu *= capacity / total;
                }
            }

            return Build(now);
        }

        // Leaking workers that reach their limit crash; any worker may crash outright. A crashed
        // pool has no worker for a few seconds, as when IIS waits for the next request or for
        // rapid-fail protection.
        private void Crash(DateTime now, double seconds)
        {
            double crashChance = 0.00002 * Chaos * seconds;
            foreach (Worker w in workers.Where(x => !x.ExitAt.HasValue).ToList())
            {
                bool outOfMemory = w.LeakRate > 0 && w.WorkingSetMB + w.LeakedMB >= w.CrashAtMB;
                if (outOfMemory || random.NextDouble() < crashChance)
                {
                    workers.Remove(w);
                    restartAt[w.Pool] = now.AddSeconds(2 + random.Next(9));
                }
            }
        }

        // Each second, each worker may start a CPU spike, a leak or a burst of HTTP 500s, more
        // often the higher the chaos.
        private void StartTrouble(DateTime now, double seconds)
        {
            double level = Chaos * seconds;
            foreach (Worker w in workers.Where(x => !x.ExitAt.HasValue))
            {
                if ((!w.SpikeUntil.HasValue || w.SpikeUntil.Value <= now) && random.NextDouble() < 0.0003 * level)
                {
                    w.SpikeUntil = now.AddSeconds(5 + random.Next(26));
                    w.SpikeCpu = 100 * (1 + random.Next(Math.Max(1, Math.Min(4, Cores))));    // one to four cores
                }
                if (w.LeakRate == 0 && random.NextDouble() < 0.0001 * level)
                {
                    w.LeakRate = 5 + random.NextDouble() * 35;
                    w.LeakNative = !w.Managed || random.NextDouble() < 0.5;
                    w.CrashAtMB = 1500 + random.NextDouble() * 1500;
                }
                if ((!w.ErrorsUntil.HasValue || w.ErrorsUntil.Value <= now) && random.NextDouble() < 0.0003 * level)
                {
                    w.ErrorsUntil = now.AddSeconds(10 + random.Next(51));
                    w.ErrorRate = 5 + random.NextDouble() * 35;
                }
            }
        }

        private void Start(int pool, DateTime now)
        {
            nextPid += 4 * (1 + random.Next(50));
            workers.Add(new Worker
            {
                Pid = nextPid,
                Pool = pool,
                Started = now,
                BaseCpu = 2 + random.NextDouble() * 60,
                WorkingSetMB = 150 + random.NextDouble() * 450,
                Managed = pool % 9 != 0,
                GcShare = 0.3 + random.NextDouble() * 0.3,
            });
        }

        private Snapshot Build(DateTime now)
        {
            var snapshot = new Snapshot { Time = now, HasPoolCounters = true };
            int instance = 0;
            foreach (Worker w in workers.OrderBy(x => x.Pid))
            {
                snapshot.Processes.Add(new ProcessSample
                {
                    Instance = instance == 0 ? "w3wp" : "w3wp#" + instance.ToString(CultureInfo.InvariantCulture),
                    Pid = w.Pid,
                    Cpu = w.Cpu,
                    WorkingSet = (w.WorkingSetMB + w.LeakedMB) * MB,
                    PrivateWorkingSet = (w.WorkingSetMB * 0.7 + w.LeakedMB) * MB,
                    Commit = (w.WorkingSetMB * 0.85 + w.LeakedMB) * MB,
                    ElapsedSeconds = (now - w.Started).TotalSeconds,
                    GcHeapBytes = w.Managed ? (w.WorkingSetMB * w.GcShare + ManagedLeak(w)) * MB : (double?)null,
                    GcCommittedBytes = w.Managed ? (w.WorkingSetMB * (w.GcShare + 0.08) + ManagedLeak(w)) * MB : (double?)null,
                    Http500Percent = w.Http500,
                });
                snapshot.PoolByPid[w.Pid] = PoolName(w.Pool);
                instance++;
            }

            double workerShare = workers.Sum(w => w.Cpu) / Cores;
            snapshot.TotalCpu = Math.Min(100, workerShare + 1 + random.NextDouble() * 3);
            snapshot.Cores = new double?[Cores];
            for (int i = 0; i < Cores; i++)
            {
                snapshot.Cores[i] = Math.Max(0, Math.Min(100, snapshot.TotalCpu.Value + (random.NextDouble() - 0.5) * 20));
            }
            double usedMB = 2048 + workers.Sum(w => w.WorkingSetMB + w.LeakedMB);
            snapshot.AvailableMB = Math.Max(100, TotalMemoryMB - usedMB);
            snapshot.CommittedBytes = (3072 + workers.Sum(w => w.WorkingSetMB * 0.85 + w.LeakedMB)) * MB;
            snapshot.CommitLimitBytes = TotalMemoryMB * 1.5 * MB;
            return snapshot;
        }

        // A managed leak grows the GC heap with the commit (≈ Native MB stays flat); a native leak
        // grows only the commit (≈ Native MB rises while GC MB stays flat).
        private static double ManagedLeak(Worker w)
        {
            return w.LeakNative ? 0 : w.LeakedMB;
        }
    }
}
