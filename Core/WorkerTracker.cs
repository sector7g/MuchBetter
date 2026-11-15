// (c) 2017-2026 Sector 7G Consulting LLC
using System;
using System.Collections.Generic;
using System.Linq;

namespace S7G.mbpm.Core
{
    /// <summary>
    /// Turns successive PID -> pool maps (one per sample) into worker-change events. The wording stays
    /// neutral: a PID change shows that a worker was replaced, not why. Called from the main loop.
    /// Kind codes (started, replaced, ...) go to events.csv as they are; the screen shows Strings.Kind_*.
    /// </summary>
    internal sealed class WorkerTracker
    {
        private Dictionary<string, HashSet<int>> previous;

        public List<WorkerEvent> Update(IDictionary<int, string> pidToPool, DateTime now)
        {
            var current = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<int, string> entry in pidToPool)
            {
                HashSet<int> pids;
                if (!current.TryGetValue(entry.Value, out pids))
                {
                    pids = new HashSet<int>();
                    current[entry.Value] = pids;
                }
                pids.Add(entry.Key);
            }

            var events = new List<WorkerEvent>();
            if (previous == null)
            {
                previous = current;     // first poll: the baseline, nothing has changed yet
                return events;
            }

            var pools = new SortedSet<string>(previous.Keys.Concat(current.Keys), StringComparer.OrdinalIgnoreCase);
            foreach (string pool in pools)
            {
                HashSet<int> before;
                HashSet<int> after;
                if (!previous.TryGetValue(pool, out before))
                {
                    before = new HashSet<int>();
                }
                if (!current.TryGetValue(pool, out after))
                {
                    after = new HashSet<int>();
                }

                List<int> added = after.Except(before).OrderBy(p => p).ToList();
                List<int> removed = before.Except(after).OrderBy(p => p).ToList();
                if (added.Count == 0 && removed.Count == 0)
                {
                    continue;
                }

                if (added.Count > 0 && removed.Count > 0)
                {
                    events.Add(Event(now, pool, "replaced", string.Format(Strings.ChangeReplaced, Pids(removed), Pids(added))));
                }
                else if (added.Count > 0 && before.Count == 0)
                {
                    events.Add(Event(now, pool, "started", string.Format(Strings.ChangeStarted, Pids(added))));
                }
                else if (added.Count > 0)
                {
                    events.Add(Event(now, pool, "overlap", string.Format(Strings.ChangeOverlap, Pids(added), Pids(before.ToList()))));
                }
                else if (after.Count > 0)
                {
                    events.Add(Event(now, pool, "exited", string.Format(Strings.ChangeExited, Pids(removed), Pids(after.ToList()))));
                }
                else
                {
                    events.Add(Event(now, pool, "gone", string.Format(Strings.ChangeGone, Pids(removed))));
                }
            }

            previous = current;
            return events;
        }

        private static WorkerEvent Event(DateTime time, string pool, string kind, string detail)
        {
            return new WorkerEvent { Time = time, Pool = pool, Kind = kind, Detail = detail };
        }

        private static string Pids(IEnumerable<int> pids)
        {
            return string.Join(", ", pids.OrderBy(p => p));
        }
    }
}
