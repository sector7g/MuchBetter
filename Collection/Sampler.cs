// (c) 2017-2026 Sector 7G Consulting LLC
using System;
using System.Collections.Concurrent;
using System.Threading;
using S7G.mbpm.Core;
using S7G.mbpm.Interop;

namespace S7G.mbpm.Collection
{
    /// <summary>
    /// Runs the collection on its own thread, on a fixed schedule (every interval from the start, so
    /// samples don't drift), and hands each snapshot to the main loop through a queue. A target that
    /// stops answering blocks only this thread: the screen keeps running and shows how long it has
    /// been since the last sample. After a long run of failed collections it reopens the query
    /// periodically, so MBPM recovers by itself when the target comes back.
    /// Each snapshot also carries MBPM's own CPU and how late it was taken.
    /// </summary>
    internal sealed class Sampler : IDisposable
    {
        /// <summary>Failing this long, the sampler starts reopening the query (then every ReconnectEvery).</summary>
        public static readonly TimeSpan ReconnectAfter = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan ReconnectEvery = TimeSpan.FromSeconds(30);

        private readonly Options options;
        private readonly Func<ISnapshotSource> open;
        private readonly ConcurrentQueue<Snapshot> output = new ConcurrentQueue<Snapshot>();
        private readonly ManualResetEvent stopRequested = new ManualResetEvent(false);
        private readonly AutoResetEvent intervalChanged = new AutoResetEvent(false);
        private readonly Thread thread;

        private ISnapshotSource source;          // used only on the sampler thread once started
        private volatile bool reopenRequested;
        private volatile bool reconnecting;
        private volatile string error;
        private long lastSampleTicks;           // UTC ticks of the last successful collection
        private int reopens;
        private long missed;
        private long intervalTicks;              // the sampling interval; changeable while running (F4)
        private bool started;

        /// <summary>Opens the source on the calling thread, so startup problems surface right away.</summary>
        public Sampler(Options options, Func<ISnapshotSource> open)
        {
            this.options = options;
            this.open = open;
            source = open();
            intervalTicks = TimeSpan.FromSeconds(options.SampleIntervalSeconds).Ticks;
            lastSampleTicks = DateTime.UtcNow.Ticks;
            thread = new Thread(Loop) { IsBackground = true, Name = "MBPM sampler" };
        }

        /// <summary>The newest collection error, or null after a successful sample.</summary>
        public string Error
        {
            get { return error; }
        }

        /// <summary>True while collections keep failing and the sampler is reopening the query.</summary>
        public bool Reconnecting
        {
            get { return reconnecting; }
        }

        public int Reopens
        {
            get { return Volatile.Read(ref reopens); }
        }

        public TimeSpan SinceLastSample
        {
            get { return DateTime.UtcNow - new DateTime(Interlocked.Read(ref lastSampleTicks), DateTimeKind.Utc); }
        }

        public void Start()
        {
            started = true;
            thread.Start();
        }

        public bool TryTake(out Snapshot snapshot)
        {
            return output.TryDequeue(out snapshot);
        }

        /// <summary>The sampling interval.</summary>
        public TimeSpan Interval
        {
            get { return TimeSpan.FromTicks(Interlocked.Read(ref intervalTicks)); }
        }

        /// <summary>Changes the sampling interval; the next sample is one new interval from now.</summary>
        public void SetInterval(TimeSpan interval)
        {
            Interlocked.Exchange(ref intervalTicks, interval.Ticks);
            intervalChanged.Set();      // wakes the sampler even mid-wait (e.g. going from 60 s to 1 s)
        }

        /// <summary>Asks the sampler thread to replace its query before the next sample.</summary>
        public void RequestReopen()
        {
            reopenRequested = true;
        }

        public void Dispose()
        {
            stopRequested.Set();
            if (started && !thread.Join(TimeSpan.FromSeconds(3)))
            {
                // Still blocked in a call to an unresponsive target. It's a background thread, so it
                // ends with the process; leave its query alone rather than close it under it.
                return;
            }
            source.Dispose();
        }

        private void Loop()
        {
            TimeSpan interval = Interval;
            DateTime next = DateTime.UtcNow + interval;
            DateTime? failingSince = null;
            DateTime lastReconnect = DateTime.MinValue;
            long lastCpu = NativeMethods.OwnCpuTime();
            DateTime lastCpuTime = DateTime.UtcNow;

            while (true)
            {
                TimeSpan wait = next - DateTime.UtcNow;
                int woken = WaitHandle.WaitAny(new WaitHandle[] { stopRequested, intervalChanged }, wait > TimeSpan.Zero ? wait : TimeSpan.Zero);
                if (woken == 0)
                {
                    return;
                }
                if (woken == 1 || Interval != interval)
                {
                    // A new interval (F4) takes effect now: sample, then every new interval from here,
                    // without counting the change as missed samples.
                    interval = Interval;
                    next = DateTime.UtcNow;
                    continue;
                }

                // How late this sample is against its schedule; whole intervals behind are skipped
                // (counted as missed) rather than collected in a burst.
                DateTime now = DateTime.UtcNow;
                TimeSpan late = now - next;
                long skipped = late > TimeSpan.Zero ? late.Ticks / interval.Ticks : 0;
                if (skipped > 0)
                {
                    Interlocked.Add(ref missed, skipped);
                }
                double delayMs = Math.Max(0, (late - TimeSpan.FromTicks(skipped * interval.Ticks)).TotalMilliseconds);
                next += TimeSpan.FromTicks((skipped + 1) * interval.Ticks);

                try
                {
                    if (reopenRequested)
                    {
                        reopenRequested = false;
                        Reopen();
                    }

                    // One clock reading, with its UTC offset: no conversion later, no DST ambiguity.
                    DateTimeOffset stamp = DateTimeOffset.Now;
                    Snapshot snapshot = source.Collect(stamp.LocalDateTime);
                    snapshot.Stamp = stamp;

                    long cpu = NativeMethods.OwnCpuTime();
                    DateTime cpuTime = DateTime.UtcNow;
                    double wallTicks = (cpuTime - lastCpuTime).Ticks;
                    if (cpu > 0 && lastCpu > 0 && wallTicks > 0)
                    {
                        snapshot.MbpmCpu = Math.Max(0, (cpu - lastCpu) / (wallTicks * Environment.ProcessorCount) * 100.0);
                    }
                    lastCpu = cpu;
                    lastCpuTime = cpuTime;
                    snapshot.DelayMs = delayMs;
                    snapshot.MissedSamples = Interlocked.Read(ref missed);

                    output.Enqueue(snapshot);
                    Interlocked.Exchange(ref lastSampleTicks, DateTime.UtcNow.Ticks);
                    error = null;
                    failingSince = null;
                    reconnecting = false;
                }
                catch (Exception e)
                {
                    error = FirstLine(e.Message);
                    DateTime failedAt = DateTime.UtcNow;
                    if (!failingSince.HasValue)
                    {
                        failingSince = failedAt;
                    }

                    // A target that was restarted or dropped off the network needs a new query.
                    if (failedAt - failingSince.Value >= ReconnectAfter && failedAt - lastReconnect >= ReconnectEvery)
                    {
                        lastReconnect = failedAt;
                        reconnecting = true;
                        try
                        {
                            Reopen();
                        }
                        catch (Exception reopenError)
                        {
                            error = FirstLine(reopenError.Message);
                        }
                    }
                }
            }
        }

        // Open the new source first: if that fails, the old one stays in place.
        private void Reopen()
        {
            ISnapshotSource fresh = open();
            source.Dispose();
            source = fresh;
            Interlocked.Increment(ref reopens);
        }

        private static string FirstLine(string message)
        {
            message = message ?? string.Empty;
            int newline = message.IndexOfAny(new[] { '\r', '\n' });
            return newline >= 0 ? message.Substring(0, newline) : message;
        }
    }
}
