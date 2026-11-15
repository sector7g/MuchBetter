# Building and testing Much Better

## Prerequisites

- Windows 10 / Server 2016 or later, x64.
- Visual Studio 2022+ with the **.NET desktop development** workload (or Build Tools for command-line builds).
- For live tests: IIS with ASP.NET 4.x.

## Build

Open `mbpm.sln` and build, or from a Developer PowerShell:

```powershell
msbuild mbpm.sln -restore -p:Configuration=Release
```

Output: `bin\Release\mbpm.exe`. To deploy, copy `mbpm.exe` and `mbpm.exe.config`.

## Source layout

Namespaces under `S7G.mbpm`, one folder each:

| Namespace | Files |
| --- | --- |
| root | `Program`, `Options`, `Brand`, `Strings` |
| `Core` | `Model`, `Correlator` (PIDs to pools), `WorkerTracker`, `Timestamps` |
| `Collection` | `Sampler`, `Collector`, `PdhQuery`, `PoolMapper` (WMI fallback), `ProcessOwner` |
| `Logging` | `LiveExport`, `Exporter`, `SessionLog` |
| `UI` | `App`, `View`, `Terminal`, `Theme`, `Splash`, `Logo` |
| `Mock` | `MockWorld`, `MockSource` |
| `Interop` | `NativeMethods` |

`tools/` holds the IIS test scripts.

## Testing

**Mock:** `mbpm --mock [cores=<n>][,pools=<n>]` runs the real app on a simulated server (1–64 cores, 1–100 pools; default 64 and 100). No IIS or admin rights needed. Mock sessions are labeled MOCK in the title, the log folder and the export.

**Real IIS load,** elevated, on a test server:

```powershell
.\tools\Setup-TestPools.ps1 -Count 20                  # /pooltest01../pooltest20
.\tools\Start-TestLoad.ps1 -DurationSeconds 600 -Skewed
.\tools\Start-TestLoad.ps1 -RecycleEverySeconds 30     # PID churn
.\tools\Setup-TestPools.ps1 -Remove
```

Each pool's CPU should follow its load and stay in the right pool across recycles. Also check: the export opens in PerfMon; the classic console and Windows Terminal; `--ascii`, `--no-alt`; resizing.

## Conventions

- C# 7.3; UTF-8 with BOM, CRLF; each source file starts with the copyright line.
- User-facing text lives in `Strings.resx` with a typed accessor in `Strings.cs`. Product names stay in `Brand.cs`; CSV headers and counter names are not translated. Translations are satellite files, `Strings.<culture>.resx`.
- MBPM changes nothing on the machine it monitors.
- Per-sample code must stay cheap: MBPM logs its own CPU.
