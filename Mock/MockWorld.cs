// (c) 2017-2026 Sector 7G Consulting LLC
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using S7G.mbpm.Core;

namespace S7G.mbpm.Mock
{
    /// <summary>
    /// A simulated IIS server for --mock (64 cores, 100 app pools): one worker per pool,
    /// with CPU and memory that wander, and an occasional overlapped recycle so worker changes
    /// show. Deterministic for a given seed and sequence of times (no clock, no sleeping).
    /// The numbers stay physically possible: workers never add up to more than the machine.
    /// </summary>
    internal sealed class MockWorld
    {
        private const double MB = 1024.0 * 1024.0;
        private const double RecyclesPerHour = 30;

        private readonly Random random;
        private readonly double totalMemoryMB;
        private readonly List<Worker> workers = new List<Worker>();
        private DateTime? last;
        private int nextPid = 4000;

        private sealed class Worker
        {
            public int Pid;
            public int Pool;                // 1-based pool number
            public DateTime Started;
            public DateTime? ExitAt;        // the old worker of an overlapped recycle
            public double BaseCpu;          // % of one core
            public double Cpu;
            public double WorkingSetMB;
        }

        public MockWorld(int cores, int pools, int seed)
        {
            Cores = cores;
            Pools = pools;
            random = new Random(seed);
            totalMemoryMB = Math.Max(16, pools) * 1024.0;     // 1 GB per pool, at least 16 GB: room for every worker
        }

        public int Cores { get; private set; }

        /// <summary>Number of app pools; may be changed between samples.</summary>
        public int Pools { get; set; }

        public double TotalMemoryMB
        {
            get { return totalMemoryMB; }
        }

        public static string PoolName(int pool)
        {
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
            for (int pool = 1; pool <= Pools; pool++)
            {
                if (!workers.Any(w => w.Pool == pool && !w.ExitAt.HasValue))
                {
                    Start(pool, now);
                }
            }

            // An occasional overlapped recycle: the new worker starts, the old one exits seconds later.
            if (workers.Count > 0 && random.NextDouble() < RecyclesPerHour / 3600.0 * seconds)
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
                    WorkingSet = w.WorkingSetMB * MB,
                    PrivateWorkingSet = w.WorkingSetMB * 0.7 * MB,
                    Commit = w.WorkingSetMB * 0.85 * MB,
                    ElapsedSeconds = (now - w.Started).TotalSeconds,
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
            double usedMB = 2048 + workers.Sum(w => w.WorkingSetMB);
            snapshot.AvailableMB = Math.Max(100, TotalMemoryMB - usedMB);
            snapshot.CommittedBytes = (3072 + workers.Sum(w => w.WorkingSetMB * 0.85)) * MB;
            snapshot.CommitLimitBytes = TotalMemoryMB * 1.5 * MB;
            return snapshot;
        }
    }
}
