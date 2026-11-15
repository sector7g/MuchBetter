// (c) 2017-2026 Sector 7G Consulting LLC
using System.Globalization;
using System.Resources;

namespace S7G.mbpm
{
    /// <summary>
    /// User-facing text, from Strings.resx (embedded as S7G.mbpm.Strings.resources). One property per
    /// resource, so a mistyped name is a compile error. Translations go in Strings.&lt;culture&gt;.resx
    /// (e.g. Strings.de.resx); .NET picks them up from satellite assemblies for the UI culture.
    /// Not in here on purpose: data formats (CSV headers, counter paths, the export's counter names)
    /// and the product and legal names (see Brand).
    /// </summary>
    internal static class Strings
    {
        private static readonly ResourceManager Manager = new ResourceManager("S7G.mbpm.Strings", typeof(Strings).Assembly);

        /// <summary>Overrides the UI culture for lookups; null uses the current UI culture.</summary>
        public static CultureInfo Culture { get; set; }

        /// <summary>Looks up a resource by name; returns "[name]" if it's missing, so gaps show up instead of crashing.</summary>
        public static string Get(string name)
        {
            return Manager.GetString(name, Culture) ?? "[" + name + "]";
        }

        /// <summary>Live CPU and memory per IIS app pool, htop-style.</summary>
        public static string Usage
        {
            get { return Get("Usage"); }
        }

        /// <summary>Unknown option '{0}'.</summary>
        public static string ErrUnknownOption
        {
            get { return Get("ErrUnknownOption"); }
        }

        /// <summary>Option '{0}' needs a value.</summary>
        public static string ErrOptionNeedsValue
        {
            get { return Get("ErrOptionNeedsValue"); }
        }

        /// <summary>Option '{0}' needs a positive whole number, not '{1}'.</summary>
        public static string ErrOptionNeedsNumber
        {
            get { return Get("ErrOptionNeedsNumber"); }
        }

        /// <summary>export needs a session folder.</summary>
        public static string ErrExportNeedsFolder
        {
            get { return Get("ErrExportNeedsFolder"); }
        }

        /// <summary>--from and --to only apply to 'mbpm export'.</summary>
        public static string ErrFromToOnlyExport
        {
            get { return Get("ErrFromToOnlyExport"); }
        }

        /// <summary>Unknown --mock setting '{0}'. Use cores=&lt;1-{1}&gt;, pools=&lt;1-{2}&gt; and chaos=&lt;0-{3}&gt;, in an...</summary>
        public static string ErrMockSetting
        {
            get { return Get("ErrMockSetting"); }
        }

        /// <summary>--mock {0} must be a whole number from {1} to {2}, not '{3}'.</summary>
        public static string ErrMockRange
        {
            get { return Get("ErrMockRange"); }
        }

        /// <summary>Unknown theme '{0}'. Themes: {1}</summary>
        public static string ErrUnknownTheme
        {
            get { return Get("ErrUnknownTheme"); }
        }

        /// <summary>This console doesn't support VT sequences (Windows 10 or later is needed). Try Windows ...</summary>
        public static string MsgVtUnsupported
        {
            get { return Get("MsgVtUnsupported"); }
        }

        /// <summary>Connecting to {0}…</summary>
        public static string SplashConnecting
        {
            get { return Get("SplashConnecting"); }
        }

        /// <summary>Connected. Press any key to continue.</summary>
        public static string SplashConnected
        {
            get { return Get("SplashConnected"); }
        }

        /// <summary>Startup failed: {0}</summary>
        public static string MsgStartupFailed
        {
            get { return Get("MsgStartupFailed"); }
        }

        /// <summary>MBPM stopped: {0}</summary>
        public static string MsgStoppedError
        {
            get { return Get("MsgStoppedError"); }
        }

        /// <summary>MBPM stopped. Logs: {0}</summary>
        public static string MsgStopped
        {
            get { return Get("MsgStopped"); }
        }

        /// <summary>Cleanup failed: {0}</summary>
        public static string MsgCleanupFailed
        {
            get { return Get("MsgCleanupFailed"); }
        }

        /// <summary>No export (--no-export); raw logs in {0}. Build it with: mbpm export "{0}"</summary>
        public static string MsgExportSkipped
        {
            get { return Get("MsgExportSkipped"); }
        }

        /// <summary>Export failed: {0}</summary>
        public static string MsgExportFailed
        {
            get { return Get("MsgExportFailed"); }
        }

        /// <summary>Raw logs kept in {0}; retry with: mbpm export "{0}"</summary>
        public static string MsgExportRetry
        {
            get { return Get("MsgExportRetry"); }
        }

        /// <summary>No samples were collected; the session folder was removed.</summary>
        public static string MsgNoSamplesRemoved
        {
            get { return Get("MsgNoSamplesRemoved"); }
        }

        /// <summary>Couldn't remove the empty session folder: {0}</summary>
        public static string MsgEmptyFolderRemoveFailed
        {
            get { return Get("MsgEmptyFolderRemoveFailed"); }
        }

        /// <summary>Raw logs kept (--keep-raw).</summary>
        public static string MsgRawKept
        {
            get { return Get("MsgRawKept"); }
        }

        /// <summary>Logged {0} samples, {1} pools:</summary>
        public static string MsgExported
        {
            get { return Get("MsgExported"); }
        }

        /// <summary>The live export stopped ({0}); raw logs were written from then on.</summary>
        public static string MsgLiveExportStopped
        {
            get { return Get("MsgLiveExportStopped"); }
        }

        /// <summary>(Performance Monitor: View Log Data &gt; Log files &gt; Add)</summary>
        public static string MsgExportPerfmonHint
        {
            get { return Get("MsgExportPerfmonHint"); }
        }

        /// <summary>CPU ▼</summary>
        public static string SortCpu
        {
            get { return Get("SortCpu"); }
        }

        /// <summary>APP POOL ▲</summary>
        public static string SortPool
        {
            get { return Get("SortPool"); }
        }

        /// <summary>Commit ▼</summary>
        public static string SortCommit
        {
            get { return Get("SortCommit"); }
        }

        /// <summary>UPTIME ▲ (newest first)</summary>
        public static string SortUptime
        {
            get { return Get("SortUptime"); }
        }

        /// <summary>PDH local</summary>
        public static string SourceLocal
        {
            get { return Get("SourceLocal"); }
        }

        /// <summary>simulated</summary>
        public static string SourceMock
        {
            get { return Get("SourceMock"); }
        }

        /// <summary>MOCK</summary>
        public static string TitleMock
        {
            get { return Get("TitleMock"); }
        }

        /// <summary>MOCK · chaos {0}</summary>
        public static string TitleMockChaos
        {
            get { return Get("TitleMockChaos"); }
        }

        /// <summary>{0} {1} process(es) hidden from WMI: run elevated.</summary>
        public static string StatusHidden
        {
            get { return Get("StatusHidden"); }
        }

        /// <summary>Pool names (WMI): {0}</summary>
        public static string StatusPoolNames
        {
            get { return Get("StatusPoolNames"); }
        }

        /// <summary>Sample: {0}</summary>
        public static string StatusSample
        {
            get { return Get("StatusSample"); }
        }

        /// <summary>{0} not responding ({1} s)…</summary>
        public static string StatusNotResponding
        {
            get { return Get("StatusNotResponding"); }
        }

        /// <summary>{0}: no data for {1} s, reconnecting every 30 s… {2}</summary>
        public static string StatusReconnecting
        {
            get { return Get("StatusReconnecting"); }
        }

        /// <summary>Theme: {0}</summary>
        public static string NoticeTheme
        {
            get { return Get("NoticeTheme"); }
        }

        /// <summary>Theme: {0}  ·  font {1} {2}</summary>
        public static string NoticeThemeFont
        {
            get { return Get("NoticeThemeFont"); }
        }

        /// <summary>Theme: {0}  ·  colors only: Windows Terminal keeps fonts in its own settings</summary>
        public static string NoticeThemeNoFont
        {
            get { return Get("NoticeThemeNoFont"); }
        }

        /// <summary>Theme: {0}  ·  font {1} isn't installed, colors only</summary>
        public static string NoticeThemeFontMissing
        {
            get { return Get("NoticeThemeFontMissing"); }
        }

        /// <summary>Exported {0} samples, {1} pools: {2}  (open it in PerfMon: View Log Data)</summary>
        public static string NoticeExported
        {
            get { return Get("NoticeExported"); }
        }

        /// <summary>Export failed: {0}</summary>
        public static string NoticeExportFailed
        {
            get { return Get("NoticeExportFailed"); }
        }

        /// <summary>Logging live: {0} samples, {1} pools in {2}  (open it in PerfMon any time: View Log Data)</summary>
        public static string NoticeLiveExport
        {
            get { return Get("NoticeLiveExport"); }
        }

        /// <summary>The export file was in use by another program; logging continues in {0}</summary>
        public static string NoticeExportMoved
        {
            get { return Get("NoticeExportMoved"); }
        }

        /// <summary>Live export stopped: {0}. Raw logs (samples.csv, machine.csv) take over.</summary>
        public static string NoticeLiveFailed
        {
            get { return Get("NoticeLiveFailed"); }
        }

        /// <summary>{0} · {1} · {2} cores · every {3}s</summary>
        public static string TitleRight
        {
            get { return Get("TitleRight"); }
        }

        /// <summary>PAUSED</summary>
        public static string TitlePaused
        {
            get { return Get("TitlePaused"); }
        }

        /// <summary>no rows</summary>
        public static string RangeNone
        {
            get { return Get("RangeNone"); }
        }

        /// <summary>showing all {0}</summary>
        public static string RangeAll
        {
            get { return Get("RangeAll"); }
        }

        /// <summary>rows {0}-{1} of {2}</summary>
        public static string RangeSome
        {
            get { return Get("RangeSome"); }
        }

        /// <summary>CPU</summary>
        public static string CardCpu
        {
            get { return Get("CardCpu"); }
        }

        /// <summary>Memory</summary>
        public static string CardMemory
        {
            get { return Get("CardMemory"); }
        }

        /// <summary>App pools</summary>
        public static string CardPools
        {
            get { return Get("CardPools"); }
        }

        /// <summary>mbpm.exe</summary>
        public static string CardMbpm
        {
            get { return Get("CardMbpm"); }
        }

        /// <summary>Cores</summary>
        public static string CardCores
        {
            get { return Get("CardCores"); }
        }

        /// <summary>All</summary>
        public static string CardCoresAll
        {
            get { return Get("CardCoresAll"); }
        }

        /// <summary> pools  </summary>
        public static string CardPoolsPools
        {
            get { return Get("CardPoolsPools"); }
        }

        /// <summary> workers</summary>
        public static string CardPoolsWorkers
        {
            get { return Get("CardPoolsWorkers"); }
        }

        /// <summary> w3wp CPU  </summary>
        public static string CardPoolsCpu
        {
            get { return Get("CardPoolsCpu"); }
        }

        /// <summary> changes</summary>
        public static string CardPoolsChanges
        {
            get { return Get("CardPoolsChanges"); }
        }

        /// <summary> CPU  </summary>
        public static string CardMbpmCpu
        {
            get { return Get("CardMbpmCpu"); }
        }

        /// <summary> missed</summary>
        public static string CardMbpmMissed
        {
            get { return Get("CardMbpmMissed"); }
        }

        /// <summary>delay {0:0} ms, max {1:0}</summary>
        public static string CardMbpmDelay
        {
            get { return Get("CardMbpmDelay"); }
        }

        /// <summary>sort {0}  ·  {1}</summary>
        public static string SortAndRange
        {
            get { return Get("SortAndRange"); }
        }

        /// <summary>Mem</summary>
        public static string MeterMem
        {
            get { return Get("MeterMem"); }
        }

        /// <summary>Commit</summary>
        public static string MeterCommit
        {
            get { return Get("MeterCommit"); }
        }

        /// <summary>{0} free</summary>
        public static string MeterFree
        {
            get { return Get("MeterFree"); }
        }

        /// <summary>APP POOL</summary>
        public static string ColPool
        {
            get { return Get("ColPool"); }
        }

        /// <summary>W3WP PID</summary>
        public static string ColPid
        {
            get { return Get("ColPid"); }
        }

        /// <summary>CPU</summary>
        public static string ColCpu
        {
            get { return Get("ColCpu"); }
        }

        /// <summary>USER</summary>
        public static string ColUser
        {
            get { return Get("ColUser"); }
        }

        /// <summary>Every {0}s</summary>
        public static string KeyInterval
        {
            get { return Get("KeyInterval"); }
        }

        /// <summary>Sampling every {0} s.</summary>
        public static string NoticeInterval
        {
            get { return Get("NoticeInterval"); }
        }

        /// <summary>The Cores view shows up to {1} cores; this machine has {0}.</summary>
        public static string NoticeCoresTooMany
        {
            get { return Get("NoticeCoresTooMany"); }
        }

        /// <summary>WS MB</summary>
        public static string ColWs
        {
            get { return Get("ColWs"); }
        }

        /// <summary>Private WS MB</summary>
        public static string ColPrivateWs
        {
            get { return Get("ColPrivateWs"); }
        }

        /// <summary>Commit MB</summary>
        public static string ColCommit
        {
            get { return Get("ColCommit"); }
        }

        /// <summary>GC MB</summary>
        public static string ColGc
        {
            get { return Get("ColGc"); }
        }

        /// <summary>≈ Native MB</summary>
        public static string ColNative
        {
            get { return Get("ColNative"); }
        }

        /// <summary>HTTP 500 %</summary>
        public static string ColHttp500
        {
            get { return Get("ColHttp500"); }
        }

        /// <summary>UPTIME</summary>
        public static string ColUptime
        {
            get { return Get("ColUptime"); }
        }

        /// <summary>(not in the sample yet)</summary>
        public static string RowNotInSample
        {
            get { return Get("RowNotInSample"); }
        }

        /// <summary>No worker changes since MBPM started.</summary>
        public static string EventsNone
        {
            get { return Get("EventsNone"); }
        }

        /// <summary>{0}d {1}</summary>
        public static string UptimeDays
        {
            get { return Get("UptimeDays"); }
        }

        /// <summary>Help</summary>
        public static string KeyHelp
        {
            get { return Get("KeyHelp"); }
        }

        /// <summary>Pause</summary>
        public static string KeyPause
        {
            get { return Get("KeyPause"); }
        }

        /// <summary>SortBy</summary>
        public static string KeySort
        {
            get { return Get("KeySort"); }
        }

        /// <summary>Cores</summary>
        public static string KeyCores
        {
            get { return Get("KeyCores"); }
        }

        /// <summary>CPU</summary>
        public static string KeyCpu
        {
            get { return Get("KeyCpu"); }
        }

        /// <summary>Export Logs</summary>
        public static string KeyExport
        {
            get { return Get("KeyExport"); }
        }

        /// <summary>Theme: {0}</summary>
        public static string KeyTheme
        {
            get { return Get("KeyTheme"); }
        }

        /// <summary>Quit</summary>
        public static string KeyQuit
        {
            get { return Get("KeyQuit"); }
        }

        /// <summary>Up/Down</summary>
        public static string KeyUpDown
        {
            get { return Get("KeyUpDown"); }
        }

        /// <summary>Select</summary>
        public static string KeySelect
        {
            get { return Get("KeySelect"); }
        }

        /// <summary>Live CPU and memory per IIS app pool, htop-style.</summary>
        public static string HelpAboutDescription
        {
            get { return Get("HelpAboutDescription"); }
        }

        /// <summary>Themes (F3 or T cycles; --theme &lt;name&gt; at startup)</summary>
        public static string HelpThemesHeading
        {
            get { return Get("HelpThemesHeading"); }
        }

        /// <summary>(current)</summary>
        public static string HelpThemeCurrent
        {
            get { return Get("HelpThemeCurrent"); }
        }

        /// <summary>Fonts change in the classic console window only; Windows Terminal keeps its own font se...</summary>
        public static string HelpThemesFontNote
        {
            get { return Get("HelpThemesFontNote"); }
        }

        /// <summary>more below</summary>
        public static string HelpScrollMore
        {
            get { return Get("HelpScrollMore"); }
        }

        /// <summary>end of help</summary>
        public static string HelpScrollEnd
        {
            get { return Get("HelpScrollEnd"); }
        }

        /// <summary> -- {0} ({1}/{2}): Up/Down, PgUp/PgDn to scroll, Esc to close --</summary>
        public static string HelpScrollFooter
        {
            get { return Get("HelpScrollFooter"); }
        }

        /// <summary> Keys</summary>
        public static string HelpBody
        {
            get { return Get("HelpBody"); }
        }

        /// <summary>started</summary>
        public static string Kind_started
        {
            get { return Get("Kind_started"); }
        }

        /// <summary>replaced</summary>
        public static string Kind_replaced
        {
            get { return Get("Kind_replaced"); }
        }

        /// <summary>overlap</summary>
        public static string Kind_overlap
        {
            get { return Get("Kind_overlap"); }
        }

        /// <summary>exited</summary>
        public static string Kind_exited
        {
            get { return Get("Kind_exited"); }
        }

        /// <summary>gone</summary>
        public static string Kind_gone
        {
            get { return Get("Kind_gone"); }
        }

        /// <summary>PID {0} → {1}</summary>
        public static string ChangeReplaced
        {
            get { return Get("ChangeReplaced"); }
        }

        /// <summary>PID {0}</summary>
        public static string ChangeStarted
        {
            get { return Get("ChangeStarted"); }
        }

        /// <summary>PID {0} started alongside {1}</summary>
        public static string ChangeOverlap
        {
            get { return Get("ChangeOverlap"); }
        }

        /// <summary>PID {0} ({1} continues)</summary>
        public static string ChangeExited
        {
            get { return Get("ChangeExited"); }
        }

        /// <summary>PID {0} (no worker now)</summary>
        public static string ChangeGone
        {
            get { return Get("ChangeGone"); }
        }

        /// <summary>(unknown)</summary>
        public static string UnknownPool
        {
            get { return Get("UnknownPool"); }
        }

        /// <summary>(pid {0})</summary>
        public static string UnnamedPool
        {
            get { return Get("UnnamedPool"); }
        }

        /// <summary>PDH {0} failed, {1}</summary>
        public static string PdhFailed
        {
            get { return Get("PdhFailed"); }
        }

        /// <summary>There is no samples.csv in {0}: MBPM writes raw logs only with --keep-raw. The live mbp...</summary>
        public static string ErrNoSamplesFile
        {
            get { return Get("ErrNoSamplesFile"); }
        }

        /// <summary>{0} needs "yyyy-MM-dd HH:mm[:ss]" or "HH:mm[:ss]", not '{1}'.</summary>
        public static string ErrBadTime
        {
            get { return Get("ErrBadTime"); }
        }

        /// <summary>no samples</summary>
        public static string ExportRangeNone
        {
            get { return Get("ExportRangeNone"); }
        }

        /// <summary>{0} to {1}</summary>
        public static string ExportRange
        {
            get { return Get("ExportRange"); }
        }

        /// <summary>   (requested: {0} to {1})</summary>
        public static string ExportRangeRequested
        {
            get { return Get("ExportRangeRequested"); }
        }

        /// <summary>start</summary>
        public static string ExportStart
        {
            get { return Get("ExportStart"); }
        }

        /// <summary>end</summary>
        public static string ExportEnd
        {
            get { return Get("ExportEnd"); }
        }

        /// <summary>{0}</summary>
        public static string ExportInfo
        {
            get { return Get("ExportInfo"); }
        }
    }
}
