<#
.SYNOPSIS
    Generates steady load against the test applications created by Setup-TestPools.ps1,
    optionally recycling a random pool on a schedule to create PID churn.

.DESCRIPTION
    Each application gets its own request loop(s). A request makes the server spin for -WorkMs
    milliseconds, then the client waits -ThinkMs before the next one, so each loop keeps roughly
    WorkMs / (WorkMs + ThinkMs) of one core busy in that app's w3wp.

    -Skewed spreads the work from 0.25x to 2x -WorkMs across the apps, so pools show
    clearly different CPU instead of all looking the same.

    Run on the IIS server (elevated), it finds the apps itself. To keep the load generator's own
    CPU off the server, run it from another machine with -BaseUrl and -Count instead; no IIS
    module or admin rights are needed there, but -RecycleEverySeconds is then unavailable.

.EXAMPLE
    .\Start-TestLoad.ps1 -DurationSeconds 300 -Skewed

.EXAMPLE
    .\Start-TestLoad.ps1 -BaseUrl http://iisbox -Count 20 -Skewed
    Remote mode: targets http://iisbox/pooltest01 .. /pooltest20.

.EXAMPLE
    .\Start-TestLoad.ps1 -RecycleEverySeconds 30
    Recycles a random pool every 30 s, so worker PIDs come and go during the run.
#>
[CmdletBinding()]
param(
    [string]$SiteName = 'Default Web Site',
    [string]$Prefix = 'pooltest',
    [string]$BaseUrl,
    [int]$Count = 0,
    [int]$DurationSeconds = 120,
    [ValidateRange(1, 5000)]
    [int]$WorkMs = 50,
    [ValidateRange(0, 60000)]
    [int]$ThinkMs = 200,
    [ValidateRange(1, 16)]
    [int]$ThreadsPerApp = 1,
    [switch]$Skewed,
    [int]$RecycleEverySeconds = 0
)

$ErrorActionPreference = 'Stop'

function Get-SiteBaseUrl([string]$Site) {
    $binding = Get-WebBinding -Name $Site -Protocol http | Select-Object -First 1
    if (-not $binding) { throw "Site '$Site' has no http binding; pass -BaseUrl." }
    $null, $port, $hostHeader = $binding.bindingInformation -split ':'
    $hostName = if ($hostHeader) { $hostHeader } else { 'localhost' }
    "http://${hostName}:$port"
}

$remote = $BaseUrl -and $Count -gt 0

if ($remote) {
    # Names follow Setup-TestPools.ps1: app and pool are both <Prefix>NN.
    if ($RecycleEverySeconds -gt 0) { throw '-RecycleEverySeconds needs to run on the IIS server itself.' }
    $BaseUrl = $BaseUrl.TrimEnd('/')
    $apps = @(1..$Count | ForEach-Object {
        $name = '{0}{1:D2}' -f $Prefix, $_
        [pscustomobject]@{ path = "/$name"; applicationPool = $name }
    })
}
else {
    Import-Module WebAdministration
    $apps = @(Get-WebApplication -Site $SiteName |
              Where-Object { $_.path -like "/$Prefix*" } |
              Sort-Object path)
    if ($apps.Count -eq 0) {
        throw "No applications matching '/$Prefix*' in site '$SiteName'. Run Setup-TestPools.ps1 first (elevated)."
    }
    if (-not $BaseUrl) { $BaseUrl = Get-SiteBaseUrl $SiteName }
}

# .NET Framework allows only 2 connections per host by default; lift it for this process.
[System.Net.ServicePointManager]::DefaultConnectionLimit = 1000

$deadline = [DateTime]::UtcNow.AddSeconds($DurationSeconds)
$stats    = [hashtable]::Synchronized(@{})   # key "app#thread" -> @(ok, errors), each written by one thread

$worker = {
    param($Url, $Deadline, $ThinkMs, $Stats, $Key)
    $client = New-Object System.Net.WebClient
    $ok = 0; $err = 0
    while ([DateTime]::UtcNow -lt $Deadline) {
        try   { [void]$client.DownloadString($Url); $ok++ }
        catch { $err++; Start-Sleep -Milliseconds 500 }   # back off while a pool is recycling or down
        $Stats[$Key] = @($ok, $err)
        if ($ThinkMs -gt 0) { Start-Sleep -Milliseconds $ThinkMs }
    }
    $client.Dispose()
}

$totalThreads = $apps.Count * $ThreadsPerApp
$runspacePool = [runspacefactory]::CreateRunspacePool(1, $totalThreads)
$runspacePool.Open()
$jobs = New-Object System.Collections.Generic.List[object]

Write-Host ("Load: {0} apps x {1} thread(s), {2} for {3}s against {4}" -f
    $apps.Count, $ThreadsPerApp,
    $(if ($Skewed) { "work 0.25x-2x of ${WorkMs}ms" } else { "work ${WorkMs}ms" }),
    $DurationSeconds, $BaseUrl)

for ($i = 0; $i -lt $apps.Count; $i++) {
    $app = $apps[$i]
    $ms  = if ($Skewed -and $apps.Count -gt 1) {
               [int]($WorkMs * (0.25 + 1.75 * $i / ($apps.Count - 1)))
           } else { $WorkMs }
    $url = "$BaseUrl$($app.path)/burn.aspx?ms=$ms"
    Write-Host ("  {0,-12} pool {1,-12} {2,5} ms/request" -f $app.path.TrimStart('/'), $app.applicationPool, $ms)

    for ($t = 0; $t -lt $ThreadsPerApp; $t++) {
        $ps = [powershell]::Create()
        $ps.RunspacePool = $runspacePool
        [void]$ps.AddScript($worker).
            AddArgument($url).AddArgument($deadline).AddArgument($ThinkMs).
            AddArgument($stats).AddArgument("$($app.path)#$t")
        $jobs.Add([pscustomobject]@{ PowerShell = $ps; Handle = $ps.BeginInvoke() })
    }
}

$poolNames   = @($apps | ForEach-Object { $_.applicationPool } | Sort-Object -Unique)
$nextRecycle = if ($RecycleEverySeconds -gt 0) { [DateTime]::UtcNow.AddSeconds($RecycleEverySeconds) } else { [DateTime]::MaxValue }

try {
    while ([DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Seconds 5

        # Copy under the table's lock so workers can't modify it mid-enumeration.
        [System.Threading.Monitor]::Enter($stats.SyncRoot)
        try     { $snapshot = @($stats.Values) }
        finally { [System.Threading.Monitor]::Exit($stats.SyncRoot) }

        $ok = 0; $err = 0
        foreach ($value in $snapshot) { $ok += $value[0]; $err += $value[1] }
        $left = [int]($deadline - [DateTime]::UtcNow).TotalSeconds
        Write-Host ("{0:HH:mm:ss}  requests {1,8}  errors {2,5}  {3,4}s left" -f (Get-Date), $ok, $err, [Math]::Max($left, 0))

        if ([DateTime]::UtcNow -ge $nextRecycle) {
            $pool = Get-Random -InputObject $poolNames
            Restart-WebAppPool -Name $pool
            Write-Host "           recycled $pool" -ForegroundColor Yellow
            $nextRecycle = [DateTime]::UtcNow.AddSeconds($RecycleEverySeconds)
        }
    }
}
finally {
    # Runs on normal exit and on Ctrl-C: stop every loop and release the runspaces.
    foreach ($job in $jobs) {
        if (-not $job.Handle.IsCompleted) { $job.PowerShell.Stop() }
        $job.PowerShell.Dispose()
    }
    $runspacePool.Close()
    $runspacePool.Dispose()
    Write-Host 'Load stopped.'
}
