# Building and testing Much Better

To just run MBPM, there's no need to build: download a precompiled version from [Releases](https://github.com/sector7g/MuchBetter/releases).

## Prerequisites

- Windows 10 / Server 2016 or later, x64.
- One of these, both free:
  - **Visual Studio 2022 or later, Community edition** or higher, with the **.NET desktop development** workload.
  - **Build Tools for Visual Studio**, without the IDE, with the **.NET desktop build tools** workload. Enough to build from the command line, e.g. on a build server.
- The .NET Framework 4.8 targeting pack, included in either workload.
- For live tests: IIS with ASP.NET 4.x.

## Build

**In Visual Studio:** open `mbpm.sln`, choose **Release**, and build.

**Without Visual Studio:** from a Developer PowerShell for VS (installed with the Build Tools), or any prompt where `msbuild` is on the path:

```powershell
msbuild mbpm.sln -restore -p:Configuration=Release -p:Platform="Any CPU"
```

This is the same command the GitHub build runs.

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

**Mock:** `mbpm --mock [cores=<n>][,pools=<n>][,chaos=<n>]` runs the real app on a simulated server (1–64 cores, 1–100 pools; default 64 and 100). `chaos=1` to `10` adds CPU spikes, native and managed leaks that end in crashes, crashes with restart gaps, more recycles, HTTP 500 bursts and odd pool names (spaces, underscores, parentheses, non-ASCII, very long), to exercise worker tracking, the event line, the ≈ Native MB column, and display and export of unusual names. No IIS or admin rights needed. Mock sessions are labeled MOCK in the title, the log folder and the export.

**Real IIS load,** elevated, on a test server, in **Windows PowerShell 5.1** (`powershell.exe`), not PowerShell 7:

```powershell
.\tools\Setup-TestPools.ps1 -Count 20                  # /pooltest01../pooltest20
.\tools\Start-TestLoad.ps1 -DurationSeconds 600 -Skewed
.\tools\Start-TestLoad.ps1 -RecycleEverySeconds 30     # PID churn
.\tools\Setup-TestPools.ps1 -Remove
```

The scripts use the `WebAdministration` module and its `IIS:` drive. PowerShell 7 loads that module through Windows PowerShell compatibility, which passes its cmdlets through but not its drive, so it fails with "A drive with the name 'IIS' does not exist." From PowerShell 7, run a script with `powershell.exe -ExecutionPolicy Bypass -File .\tools\Setup-TestPools.ps1 -Count 20`.

Each pool's CPU should follow its load and stay in the right pool across recycles. Also check: the export opens in PerfMon; the classic console and Windows Terminal; `--ascii`, `--no-alt`; resizing.

## Conventions

- C# 7.3; UTF-8 with BOM, CRLF; each source file starts with the copyright line.
- User-facing text lives in `Strings.resx` with a typed accessor in `Strings.cs`. Product names stay in `Brand.cs`; CSV headers and counter names are not translated. Translations are satellite files, `Strings.<culture>.resx`.
- MBPM changes nothing on the machine it monitors.
- Per-sample code must stay cheap: MBPM logs its own CPU.
