MBPM  ·  A Much Better appPool Monitor  (v0.1)
==============================================
© 2017-2026 Sector 7G Consulting LLC
Licensed under the MIT License (see LICENSE)

Live CPU and memory per IIS app pool, htop-style, logged as a PerfMon log. Monitors the
machine it runs on. Run elevated, in Windows Terminal or the classic console.

Read-only: apart from its log files, MBPM changes nothing, and has no stop, start, kill or
recycle actions. See README.md.

Not elevated: USER and the .NET columns (GC MB, ~ Native MB) show "-", and workers
IIS hasn't named yet show "(unknown)" because WMI can't read their command lines. Everything
else works.

Usage
-----
  mbpm [-c <cores>] [-i <sec>] [-p <sec>] [-l <log folder>] [--theme <name>]
       [--ascii] [--no-alt] [--no-splash] [--no-export] [--keep-raw] [--debug-logs]
  mbpm --mock [cores=<1-64>][,pools=<1-100>][,chaos=<0-10>]
  mbpm export <session folder> [--from <time>] [--to <time>]
  mbpm -h

Screen
------
  Title    host, source, cores, interval; MOCK or PAUSED when so.
  Cards    CPU (total bar, one cell per core) or, with F7, Cores (one bar per core, up to
           64 cores); Memory (RAM, commit); App pools (pools, workers, their CPU, worker
           changes); mbpm.exe (own CPU, missed samples, sample delay).
  Table    one row per worker: APP POOL, W3WP PID, CPU (share of the machine), USER (the
           worker's account), WS MB, Private WS MB, Commit MB, GC MB (.NET heap),
           ~ Native MB (Commit MB minus the .NET GC's committed memory: an estimate),
           HTTP 500 % (share of responses that were status 500), UPTIME. Narrow windows drop USER, then the .NET and HTTP 500 columns, then
           the memory columns. README.md lists each column's counter.
  Bottom   newest error, notice or worker change, with sort and row range; the key bar.

  Memory columns match Task Manager: Working set (memory), Memory (private working set),
  Commit size. Worker changes say what happened (started, replaced, overlap, exited, gone),
  not why.

Keys
----
  Arrows PgUp PgDn Home End   select
  F5 / Space                  pause the display (logging continues)
  F6 / S                      sort: CPU, pool, commit, uptime
  F7 / C                      CPU or Cores card
  F8 / E                      where the logs are
  F3 / T                      next theme
  F4 / I                      interval: 1, 2, 5, 10, 15, 30, 60 s
  F1 / H / ?                  help (Esc closes)
  F10 / Q / Ctrl-C            quit

Themes
------
  Sector 7G (default), Mono (greyscale), Metro Night, HTOP. Fonts change only in the classic
  console; Windows Terminal keeps its own. The original font is restored on exit.

Files
-----
  %LOCALAPPDATA%\Sector 7G\mbpm\<machine>\<yyyyMMdd-HHmmss>\, written live, flushed every second:
    mbpm_export_perfmon.csv  per-pool PerfMon CSV log
    mbpm_export_cpu.csv      plain CSV: utc_timestamp, local_timestamp, CPU per pool
    export_info.txt          source and column definitions
    events.csv               worker changes
  Raw per-worker logs (samples.csv, machine.csv) only with --keep-raw (to re-slice with
  'mbpm export --from/--to'), or instead of the export with --no-export. --debug-logs adds
  pid_map.csv. Empty sessions are removed.

  Plain CSVs carry UTC and local time with its offset. The PerfMon file uses PerfMon's own
  convention: local time, time zone in the header.

Export
------
  Counter sets (MBPM's own, computed from Windows counters):
    Much Better App Pool(<pool>)  CPU % of one core, CPU % of machine, WS MB, Private WS MB,
                                  Commit MB, GC MB, Native MB (approx), HTTP 500 %
    Much Better Machine           CPU %, Memory used/total MB, Commit MB, Commit limit MB,
                                  Much Better CPU %, Sample delay ms, Missed samples
  Columns are per pool: a replaced worker stays in its pool's column, web gardens are summed
  (HTTP 500 % takes the highest worker), blank means no worker. In counter paths, ( and ) in a
  pool name are written [ and ], and / \ # as _, as Windows does for its own instances. A pool that appears mid-session is added by rewriting the file once.

  Open it: perfmon.msc > View Log Data > Log files > Add (type CSV) > Data > Add. It also
  works with relog and tools that read PerfMon CSV.

Mock
----
  --mock simulates an IIS server (default 64 cores, 100 pools) with varying load and the
  occasional recycle. chaos=1 to 10 adds trouble in proportion: CPU spikes, memory leaks
  (native or managed) that end in a crash, crashes with a gap before the pool restarts, more
  recycles, and bursts of HTTP 500s. Every third pool also gets an odd name (spaces,
  underscores, parentheses, non-ASCII, very long). The title badge shows the level, e.g.
  MOCK · chaos 5. Sessions are labeled MOCK in the title, log folder and export.

How it works
------------
  One PDH query per sample reads \Process(w3wp*), \Processor Information(*), \Memory and
  \W3SVC_W3WP(*), whose "<PID>_<pool>" instances name the pools in the same sample. WMI is a
  fallback for workers IIS hasn't named yet. USER comes from each worker's process token,
  once per PID. Sampling runs on its own thread.
