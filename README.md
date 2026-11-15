# MBPM  ·  A Much Better appPool Monitor

[![build](https://github.com/sector7g/MuchBetter/actions/workflows/build.yml/badge.svg)](https://github.com/sector7g/MuchBetter/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**Live CPU and memory per IIS application pool, htop-style, logged as you watch.**

Performance Monitor shows IIS worker processes as `w3wp`, `w3wp#1`, `w3wp#2`... with no hint of which app pool each serves. MBPM shows CPU and memory per app pool, live in the console, and logs the same data as a PerfMon log.

![MBPM monitoring 20 IIS app pools](docs/screens/live01.png)

[Key benefits](#key-benefits) · [Background](#background) · [Quick start](#quick-start) · [Output](#output) · [Requirements](#requirements) · [Building](#building) · [License](#license)

## Key benefits

> **1. In effect, a custom performance counter without creating one.** Per-pool data is logged in PerfMon's format under its own counter sets, `Much Better App Pool(<pool>)` and `Much Better Machine`. Performance Monitor, `relog` and tools such as Splunk read it like any capture. Nothing is registered: no manifest, no `lodctr`, no install.
>
> **2. Right as workers come and go.** PIDs are joined to pools in the same sample as the figures, so renumbered `w3wp#n` instances are never misattributed. Recycled workers stay in their pool's column; web gardens are summed.
>
> **3. Nothing installed or changed on the server.** One executable, run on the IIS server itself.
>
> **4. Logged live, like a data collector set.** One row per second, flushed as it goes; a crash loses nothing already written.
>
> **5. Low overhead, and honest about it.** One counter query per sample. MBPM's own CPU, sample delay and missed samples are shown and logged.

## Background

Windows can't attribute counters to an app pool on its own: `Process` instances carry no pool name, are renumbered whenever a worker starts or exits, and a data collector set only captures the workers present when it starts. The usual workaround logs `\Process(w3wp*)\ID Process`, maps PIDs to pools (WMI, `appcmd`, or IIS's `W3SVC_W3WP` counters named `PID_PoolName`) and merges after the fact ([Microsoft](https://learn.microsoft.com/en-us/archive/blogs/dpaulexchange/tips-and-tricks-how-to-determine-which-w3wp-exe-process-belongs-to-which-application-pool), [duplicate instance names](https://learn.microsoft.com/en-ie/Windows/Win32/perfctrs/handling-duplicate-instance-names)). MBPM does that join live, every sample.

**History:** `bpm`, a PowerShell script that polled counters and WMI, skewed the numbers under load. `ebpm`, in C#, sampled with a data collector set and merged offline. `mbpm` reads PDH directly with IIS's pool names in the same sample, and logs per pool as it goes.

## Quick start

```text
mbpm                          monitor this machine (run elevated)
mbpm --mock                   simulated server, 64 cores, 100 pools
mbpm --mock cores=8,pools=12  smaller simulation (1-64 cores, 1-100 pools)
mbpm -h                       all options
```

Keys: **F1** help, **F3** theme, **F4** interval, **F5** pause, **F6** sort, **F7** CPU / Cores, **F8** logs, **F10** quit.

## Output

Each run writes `%LOCALAPPDATA%\Sector 7G\mbpm\<machine>\<yyyyMMdd-HHmmss>\`:

| File | Contents |
| --- | --- |
| `mbpm_export_perfmon.csv` | Per-pool data in PerfMon's CSV log format (open via View Log Data, or `relog` to `.blg`). |
| `mbpm_export_cpu.csv` | Plain CSV for Excel: UTC and local timestamps, then CPU per pool. |
| `export_info.txt` | Source and how each column is computed. |
| `events.csv` | Worker changes. |

## Requirements

Windows with IIS, .NET Framework 4.8, a VT-capable console (Windows 10+; Windows Terminal recommended). Run elevated on the IIS server; MBPM monitors only the machine it runs on.

**Why .NET Framework 4.8:** it ships with Windows and Windows Server, so the tool is a small executable you copy and run, with nothing to install. PDH, WMI and the console APIs are all available. The cost is C# 7.3, which is enough here.

## Building

Open `mbpm.sln` in Visual Studio and build. See [BUILDING.md](BUILDING.md).

## License

[MIT](LICENSE). © 2017-2026 Sector 7G Consulting LLC.
