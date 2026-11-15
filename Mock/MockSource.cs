// (c) 2017-2026 Sector 7G Consulting LLC
using System;
using System.Collections.Generic;
using S7G.mbpm.Core;

namespace S7G.mbpm.Mock
{
    /// <summary>
    /// The mock (--mock) as MBPM's data sources: snapshots for the Sampler, and the WMI fallback's
    /// PID -> pool map. Everything downstream (naming, worker tracking, logging, the view, the
    /// export) runs exactly as it does with real counters.
    /// </summary>
    internal sealed class MockSource : ISnapshotSource, IPoolNameSource
    {
        private readonly MockWorld world;
        private readonly object sync = new object();

        public MockSource(MockWorld world)
        {
            this.world = world;
        }

        public int HiddenCount
        {
            get { return 0; }
        }

        public Snapshot Collect(DateTime time)
        {
            lock (sync)
            {
                return world.Next(time);
            }
        }

        public Dictionary<int, string> GetPidToPool()
        {
            lock (sync)
            {
                return world.PidToPool();
            }
        }

        public string GetUser(int pid)
        {
            lock (sync)
            {
                return world.UserFor(pid);
            }
        }

        public double? GetTotalPhysicalMemoryMB()
        {
            return world.TotalMemoryMB;
        }

        public void Dispose()
        {
        }
    }
}
