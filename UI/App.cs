// (c) 2017-2026 Sector 7G Consulting LLC
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using S7G.mbpm.Collection;
using S7G.mbpm.Core;
using S7G.mbpm.Interop;
using S7G.mbpm.Logging;
using S7G.mbpm.Mock;

namespace S7G.mbpm.UI
{
    /// <summary>
    /// The main loop: takes snapshots from the sampler thread, names the workers, logs, handles keys
    /// and draws. Pool names come from IIS's W3SVC_W3WP counters in each snapshot; a timer thread asks
    /// WMI only while some sampled worker has no name that way (and stays idle otherwise).
    /// </summary>
    internal sealed class App : IDisposable
    {
        private const int MaxEvents = 200;
        public const int MaxCoresView = 64;        // F7's Cores view draws one bar per core, up to this many

        private static readonly string[] SortLabels = { Strings.SortCpu, Strings.SortPool, Strings.SortCommit, Strings.SortUptime };

        private readonly Options options;
        private readonly IPoolNameSource mapper;   // WMI, or the mock
        private readonly SessionLog log;
        private LiveExport live;                // null with --no-export, or after a write error
        private string liveError;
        private readonly Sampler sampler;
        private readonly WorkerTracker tracker = new WorkerTracker();
        private readonly double? totalMB;

        // WMI fallback, refreshed by the poll timer only while wmiNeeded is set.
        private Timer pollTimer;
        private int pollBusy;
        private volatile bool wmiNeeded = true;
        private volatile Dictionary<int, string> wmiMap = new Dictionary<int, string>();
        private long wmiMapTicks;                   // UTC ticks of the last successful WMI read (0 = none)
        private volatile string pollError;
        private volatile string pollNotice;
        private volatile bool quit;

        // Main-loop state.
        private readonly List<WorkerEvent> events = new List<WorkerEvent>();
        private readonly Dictionary<int, string> owners = new Dictionary<int, string>();     // PID -> account, read once per PID
        private Snapshot liveSnapshot = new Snapshot { Time = DateTime.Now };
        private List<PoolRow> liveRows = new List<PoolRow>();
        private Snapshot shownSnapshot;
        private List<PoolRow> shownRows;
        private readonly Dictionary<int, string> knownNames = new Dictionary<int, string>();   // PID -> pool, sampled PIDs only
        private Dictionary<int, string> currentMap = new Dictionary<int, string>();
        private int cores;
        private int workerChanges;
        private double maxDelayMs;              // worst sample delay this session
        private string notice;
        private DateTime noticeUntil;
        private DateTime? missingSince;

        // UI state.
        private int sortMode;
        private bool paused;
        private bool help;
        private int helpScroll;
        private bool showCores;                 // F7: the CPU card shows every core (up to 64)
        private int selected;
        private string selectedKey;
        private int scrollTop;
        private int pageSize = 10;

        public App(Options options)
        {
            this.options = options;
            if (options.Mock)
            {
                var mock = new MockSource(new MockWorld(options.MockCores, options.MockPools, Environment.TickCount));
                mapper = mock;
                sampler = new Sampler(options, () => mock);
                totalMB = mock.GetTotalPhysicalMemoryMB();
            }
            else
            {
                mapper = new PoolMapper(options.ProcessName);
                // Opens the PDH query here, so startup errors surface now.
                sampler = new Sampler(options, () => new Collector(options.ProcessName));
                totalMB = LocalTotalMB();
            }
            cores = options.Cores > 0 ? options.Cores : (options.Mock ? options.MockCores : Environment.ProcessorCount);
            // The export is written live; the per-worker raw logs only when asked for (--keep-raw),
            // or as the only record when there's no export (--no-export).
            log = new SessionLog(options.LogRoot, options.Machine, options.DebugLogs, options.KeepRaw || !options.ExportOnExit);
            if (options.ExportOnExit)
            {
                live = new LiveExport(log.Folder, options.Machine);
            }
        }

        /// <summary>The live export's files and counts so far; null with --no-export or after a write error.</summary>
        public Exporter.Result LiveResult
        {
            get { return live == null ? null : live.Result; }
        }

        /// <summary>Why the live export stopped, or null.</summary>
        public string LiveError
        {
            get { return liveError; }
        }

        public string SessionFolder
        {
            get { return log.Folder; }
        }

        public void RequestQuit()
        {
            quit = true;
        }

        public void Run()
        {
            pollTimer = new Timer(Poll, null, 0, options.PoolPollSeconds * 1000);
            sampler.Start();

            int lastWidth = -1;
            int lastHeight = -1;
            DateTime lastStatusDraw = DateTime.MinValue;
            bool dirty = true;

            while (!quit)
            {
                dirty |= HandleKeys();

                Snapshot snapshot;
                while (sampler.TryTake(out snapshot))
                {
                    Consume(snapshot);
                    dirty = true;
                }

                // Redraw once a second regardless, so a not-responding count keeps moving.
                if (DateTime.UtcNow - lastStatusDraw >= TimeSpan.FromSeconds(1))
                {
                    lastStatusDraw = DateTime.UtcNow;
                    dirty = true;
                }

                if (events.Count > MaxEvents)
                {
                    events.RemoveRange(0, events.Count - MaxEvents);
                }

                int width = Terminal.Width;
                int height = Terminal.Height;
                if (width != lastWidth || height != lastHeight)
                {
                    lastWidth = width;
                    lastHeight = height;
                    dirty = true;
                }

                if (dirty)
                {
                    Draw(width, height);
                    dirty = false;
                }

                Thread.Sleep(40);
            }
        }

        public void Dispose()
        {
            if (pollTimer != null)
            {
                using (var done = new ManualResetEvent(false))
                {
                    if (pollTimer.Dispose(done))
                    {
                        done.WaitOne(TimeSpan.FromSeconds(30));
                    }
                }
                pollTimer = null;
            }
            sampler.Dispose();
            if (live != null)
            {
                live.Dispose();         // closes the files and writes the final export_info.txt
            }
            log.Dispose();
        }

        // ---- Timer thread ----

        // WMI is the fallback for pool names: used only while a sampled worker has no W3SVC_W3WP name
        // (a new worker before IIS adds its instance, or IIS counters not available on the target).
        private void Poll(object state)
        {
            if (quit || !wmiNeeded || Interlocked.Exchange(ref pollBusy, 1) == 1)
            {
                return;
            }

            try
            {
                Dictionary<int, string> map = mapper.GetPidToPool();
                wmiMap = map;
                Interlocked.Exchange(ref wmiMapTicks, DateTime.UtcNow.Ticks);
                pollNotice = map.Count == 0 && mapper.HiddenCount > 0
                    ? string.Format(Strings.StatusHidden, mapper.HiddenCount, options.ProcessName)
                    : null;
                pollError = null;
            }
            catch (Exception e)
            {
                pollError = string.Format(Strings.StatusPoolNames, FirstLine(e.Message));
            }
            finally
            {
                Interlocked.Exchange(ref pollBusy, 0);
            }
        }

        // ---- Main thread ----

        private void Consume(Snapshot snapshot)
        {
            if (options.Cores == 0 && snapshot.Cores.Length > 0)
            {
                cores = snapshot.Cores.Length;
            }

            // Name each sampled worker: WMI's name when it has one (it reads the command line, so it's
            // never sanitized), else IIS's counter instance, else the name it had before.
            Dictionary<int, string> wmi = wmiMap;
            var sampledPids = new HashSet<int>();
            bool unnamed = false;
            foreach (ProcessSample process in snapshot.Processes)
            {
                if (!sampledPids.Add(process.Pid))
                {
                    continue;
                }
                string name;
                if (wmi.TryGetValue(process.Pid, out name) || snapshot.PoolByPid.TryGetValue(process.Pid, out name))
                {
                    knownNames[process.Pid] = name;
                }
                else if (!knownNames.ContainsKey(process.Pid))
                {
                    unnamed = true;
                }
            }
            foreach (int gone in knownNames.Keys.Where(pid => !sampledPids.Contains(pid)).ToList())
            {
                knownNames.Remove(gone);
            }
            wmiNeeded = unnamed || (sampledPids.Count > 0 && !snapshot.HasPoolCounters);

            // Workers seen by a recent WMI read but not by this sample show as "not in the sample yet".
            var map = new Dictionary<int, string>(knownNames);
            if (WmiMapIsFresh())
            {
                foreach (KeyValuePair<int, string> entry in wmi)
                {
                    if (!map.ContainsKey(entry.Key))
                    {
                        map[entry.Key] = entry.Value;
                    }
                }
            }

            List<PoolRow> rows = Correlator.Build(snapshot, map, Math.Max(1, cores));
            SetOwners(rows);

            foreach (WorkerEvent change in tracker.Update(knownNames, snapshot.Time))
            {
                change.Stamp = snapshot.When;
                log.WriteEvent(change);
                events.Add(change);
                workerChanges++;
            }

            log.WriteSamples(snapshot.When, rows);
            log.WriteMachine(snapshot, totalMB);
            WriteLive(snapshot, rows);
            log.WritePidMap(knownNames);

            liveSnapshot = snapshot;
            maxDelayMs = Math.Max(maxDelayMs, snapshot.DelayMs);
            liveRows = rows;
            currentMap = new Dictionary<int, string>(knownNames);

            CheckForMissingWorkers(rows);
        }

        // Appends the sample to the live export. If that fails (disk full, file locked...), the raw
        // logs take over so nothing more is lost, and "mbpm export" can rebuild from them.
        private void WriteLive(Snapshot snapshot, List<PoolRow> rows)
        {
            if (live == null)
            {
                return;
            }
            try
            {
                string movedBefore = live.MovedTo;
                live.Append(snapshot.When, rows, SessionLog.MachineValues(snapshot, totalMB));
                if (live.MovedTo != movedBefore)
                {
                    ShowNotice(string.Format(Strings.NoticeExportMoved, live.MovedTo));
                }
            }
            catch (Exception e)
            {
                liveError = FirstLine(e.Message);
                try
                {
                    live.Dispose();
                }
                catch (Exception)
                {
                }
                live = null;
                log.EnableRawLogs();
                ShowNotice(string.Format(Strings.NoticeLiveFailed, liveError));
            }
        }

        private bool WmiMapIsFresh()
        {
            long ticks = Interlocked.Read(ref wmiMapTicks);
            return ticks != 0 && DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) < TimeSpan.FromSeconds(2 * options.PoolPollSeconds);
        }

        // PDH re-expands the wildcard on each collection, so new workers should simply appear. If WMI sees
        // a worker that the samples keep missing for two poll intervals, have the sampler reopen its query.
        private void CheckForMissingWorkers(List<PoolRow> rows)
        {
            if (!rows.Any(r => r.NotInSample))
            {
                missingSince = null;
                return;
            }

            if (!missingSince.HasValue)
            {
                missingSince = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - missingSince.Value > TimeSpan.FromSeconds(2 * options.PoolPollSeconds))
            {
                sampler.RequestReopen();
                missingSince = null;
            }
        }

        // The account each worker runs as, read once per PID (a token lookup, cheap); PIDs that
        // are gone are forgotten.
        private void SetOwners(List<PoolRow> rows)
        {
            var seen = new HashSet<int>();
            foreach (PoolRow row in rows)
            {
                seen.Add(row.Pid);
                string user;
                if (!owners.TryGetValue(row.Pid, out user))
                {
                    try
                    {
                        user = mapper.GetUser(row.Pid);
                    }
                    catch (Exception)
                    {
                        user = null;
                    }
                    owners[row.Pid] = user;
                }
                row.User = user;
            }
            foreach (int pid in owners.Keys.Where(p => !seen.Contains(p)).ToList())
            {
                owners.Remove(pid);
            }
        }

        private bool HandleKeys()
        {
            bool handled = false;
            try
            {
                while (Console.KeyAvailable)
                {
                    HandleKey(Console.ReadKey(true));
                    handled = true;
                }
            }
            catch (InvalidOperationException)
            {
                // Input is redirected: no keys, only Ctrl-C.
            }
            return handled;
        }

        private void HandleKey(ConsoleKeyInfo key)
        {
            if (help && HandleHelpKey(key))
            {
                return;
            }

            switch (key.Key)
            {
                case ConsoleKey.F3:
                case ConsoleKey.T:
                    CycleTheme();
                    return;
                case ConsoleKey.F4:
                case ConsoleKey.I:
                    NextInterval();
                    return;
                case ConsoleKey.UpArrow:
                    Move(-1);
                    return;
                case ConsoleKey.DownArrow:
                    Move(1);
                    return;
                case ConsoleKey.PageUp:
                    Move(-Math.Max(1, pageSize - 1));
                    return;
                case ConsoleKey.PageDown:
                    Move(Math.Max(1, pageSize - 1));
                    return;
                case ConsoleKey.Home:
                    Move(-int.MaxValue / 2);
                    return;
                case ConsoleKey.End:
                    Move(int.MaxValue / 2);
                    return;
                case ConsoleKey.F5:
                case ConsoleKey.Spacebar:
                    TogglePause();
                    return;
                case ConsoleKey.F6:
                case ConsoleKey.S:
                    sortMode = (sortMode + 1) % SortLabels.Length;
                    return;
                case ConsoleKey.F8:
                case ConsoleKey.E:
                    ExportNow();
                    return;
                case ConsoleKey.F7:
                case ConsoleKey.C:
                    ToggleCores();
                    return;
                case ConsoleKey.F1:
                case ConsoleKey.H:
                    ToggleHelp();
                    return;
                case ConsoleKey.Escape:
                    help = false;
                    return;
                case ConsoleKey.F10:
                case ConsoleKey.Q:
                    quit = true;
                    return;
            }

            if (key.KeyChar == '?')
            {
                ToggleHelp();
            }
        }

        private void ToggleHelp()
        {
            help = !help;
            helpScroll = 0;
        }

        // While help is open, the movement keys scroll it. The view clamps the scroll position.
        private bool HandleHelpKey(ConsoleKeyInfo key)
        {
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    helpScroll = Math.Max(0, helpScroll - 1);
                    return true;
                case ConsoleKey.DownArrow:
                    helpScroll++;
                    return true;
                case ConsoleKey.PageUp:
                    helpScroll = Math.Max(0, helpScroll - Math.Max(1, pageSize - 1));
                    return true;
                case ConsoleKey.PageDown:
                    helpScroll += Math.Max(1, pageSize - 1);
                    return true;
                case ConsoleKey.Home:
                    helpScroll = 0;
                    return true;
                case ConsoleKey.End:
                    helpScroll = 1000;
                    return true;
                default:
                    return false;
            }
        }

        // Builds the exports from everything logged so far. Runs on the main loop: a long session takes
        // a moment, during which the display pauses.
        private void ExportNow()
        {
            try
            {
                if (live != null)
                {
                    // Already written live; bring export_info.txt up to date and say where it is.
                    live.WriteInfo();
                    Exporter.Result current = live.Result;
                    ShowNotice(string.Format(Strings.NoticeLiveExport, current.Samples, current.Pools, current.PerfMonPath));
                    return;
                }
                Exporter.Result result = Exporter.Export(log.Folder, options.Machine, null, null);
                ShowNotice(string.Format(Strings.NoticeExported, result.Samples, result.Pools, result.PerfMonPath));
            }
            catch (Exception e)
            {
                ShowNotice(string.Format(Strings.NoticeExportFailed, FirstLine(e.Message)));
            }
        }

        private void ShowNotice(string text)
        {
            notice = text;
            noticeUntil = DateTime.UtcNow.AddSeconds(15);
        }

        private void Move(int delta)
        {
            List<PoolRow> rows = SortedRows();
            if (rows.Count == 0)
            {
                return;
            }
            long target = (long)selected + delta;
            selected = (int)Math.Max(0, Math.Min(rows.Count - 1, target));
            selectedKey = rows[selected].Key;
        }

        private void TogglePause()
        {
            paused = !paused;
            if (paused)
            {
                // Freeze what's on screen; sampling and logging carry on underneath.
                shownSnapshot = liveSnapshot;
                shownRows = liveRows;
            }
        }

        private List<PoolRow> SortedRows()
        {
            IEnumerable<PoolRow> rows = paused ? shownRows : liveRows;
            switch (sortMode)
            {
                case 1:
                    return rows.OrderBy(r => r.Pool, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Pid).ToList();
                case 2:
                    return rows.OrderByDescending(r => r.CommitMB ?? -1).ThenBy(r => r.Pool, StringComparer.OrdinalIgnoreCase).ToList();
                case 3:
                    return rows.OrderBy(r => r.Uptime ?? TimeSpan.MaxValue).ThenBy(r => r.Pool, StringComparer.OrdinalIgnoreCase).ToList();
                default:
                    return rows.OrderByDescending(r => r.CpuAll ?? -1).ThenBy(r => r.Pool, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        private void Draw(int width, int height)
        {
            List<PoolRow> rows = SortedRows();

            // Keep the same worker selected as rows re-sort.
            int index = selectedKey == null ? -1 : rows.FindIndex(r => r.Key == selectedKey);
            if (index >= 0)
            {
                selected = index;
            }
            selected = Math.Max(0, Math.Min(selected, rows.Count - 1));
            selectedKey = rows.Count > 0 ? rows[selected].Key : null;

            Snapshot snapshot = paused ? shownSnapshot : liveSnapshot;
            Dictionary<int, string> map = currentMap;
            // A WMI problem matters only while WMI is actually needed for some worker's name.
            string status = SamplerStatus() ?? (wmiNeeded ? pollError ?? pollNotice : null);
            string currentNotice = DateTime.UtcNow < noticeUntil ? notice : null;

            var model = new ViewModel
            {
                Machine = options.Machine,
                Source = options.Mock ? Strings.SourceMock : Strings.SourceLocal,
                Mock = options.Mock,
                ThemeName = Theme.Current.Name,
                SortLabel = SortLabels[sortMode],
                Status = status,
                Notice = currentNotice,
                Cores = Math.Max(1, cores),
                Interval = (uint)sampler.Interval.TotalSeconds,
                Paused = paused,
                ShowCores = showCores,
                Help = help,
                HelpScroll = helpScroll,
                Ascii = options.Ascii,
                CoreLoads = snapshot.Cores,
                TotalCpu = snapshot.TotalCpu,
                AvailableMB = snapshot.AvailableMB,
                TotalMB = totalMB,
                CommittedBytes = snapshot.CommittedBytes,
                CommitLimitBytes = snapshot.CommitLimitBytes,
                Rows = rows,
                Selected = selected,
                Events = events,
                PoolCount = map.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                WorkerCount = map.Count,
                WorkerChanges = workerChanges,
                MbpmCpu = liveSnapshot.MbpmCpu,
                DelayMs = liveSnapshot.DelayMs,
                MaxDelayMs = maxDelayMs,
                MissedSamples = liveSnapshot.MissedSamples,
            };

            Terminal.Draw(View.Render(model, width, height, ref scrollTop, out pageSize));
            helpScroll = model.HelpScroll;
        }

        // A target that stops answering shows as such within a few seconds; a collection error shows next.
        private string SamplerStatus()
        {
            TimeSpan since = sampler.SinceLastSample;
            TimeSpan limit = TimeSpan.FromSeconds(Math.Max(3 * sampler.Interval.TotalSeconds, 5));
            string error = sampler.Error;
            if (sampler.Reconnecting)
            {
                return string.Format(Strings.StatusReconnecting, options.Machine, (int)since.TotalSeconds, error ?? string.Empty);
            }
            if (since > limit)
            {
                return string.Format(Strings.StatusNotResponding, options.Machine, (int)since.TotalSeconds);
            }
            return error == null ? null : string.Format(Strings.StatusSample, error);
        }

        // ---- Sampling interval (F4) ----

        private static readonly int[] Intervals = { 1, 2, 5, 10, 15, 30, 60 };

        // Steps through the preset intervals. The live export simply records the new spacing: each
        // row carries its own timestamp.
        private void NextInterval()
        {
            int current = (int)sampler.Interval.TotalSeconds;
            int next = Intervals.FirstOrDefault(s => s > current);
            if (next == 0)
            {
                next = Intervals[0];
            }
            sampler.SetInterval(TimeSpan.FromSeconds(next));
            ShowNotice(string.Format(Strings.NoticeInterval, next));
        }

        // ---- CPU card (F7) ----

        // Toggles the CPU card between the total (CPU) and one bar per core (Cores). More than 64
        // cores don't fit as bars, so the Cores view isn't offered there.
        private void ToggleCores()
        {
            int count = liveSnapshot.Cores == null ? cores : liveSnapshot.Cores.Length;
            if (!showCores && count > MaxCoresView)
            {
                ShowNotice(string.Format(Strings.NoticeCoresTooMany, count, MaxCoresView));
                return;
            }
            showCores = !showCores;
        }

        // ---- Themes ----

        private void CycleTheme()
        {
            Theme next = Theme.Next();
            FontResult font = Theme.Apply(next);
            ShowNotice(ThemeNotice(next, font));
        }

        /// <summary>The notice for a theme change, saying what happened to the font.</summary>
        public static string ThemeNotice(Theme theme, FontResult font)
        {
            switch (font)
            {
                case FontResult.Changed:
                    return theme.FontFace == null
                        ? string.Format(Strings.NoticeTheme, theme.Name, theme.Description)
                        : string.Format(Strings.NoticeThemeFont, theme.Name, theme.Description, theme.FontFace, theme.FontSize);
                case FontResult.NotSupported:
                    return string.Format(Strings.NoticeThemeNoFont, theme.Name, theme.Description);
                case FontResult.NotInstalled:
                    return string.Format(Strings.NoticeThemeFontMissing, theme.Name, theme.Description, theme.FontFace);
                default:
                    return string.Format(Strings.NoticeTheme, theme.Name, theme.Description);
            }
        }

        private static double? LocalTotalMB()
        {
            var status = new NativeMethods.MEMORYSTATUSEX();
            return NativeMethods.GlobalMemoryStatusEx(status) ? status.ullTotalPhys / (1024.0 * 1024.0) : (double?)null;
        }

        private static string FirstLine(string message)
        {
            message = message ?? string.Empty;
            int newline = message.IndexOfAny(new[] { '\r', '\n' });
            return newline >= 0 ? message.Substring(0, newline) : message;
        }
    }
}
