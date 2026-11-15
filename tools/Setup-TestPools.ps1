#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Creates (or removes) N IIS applications under a site, each with its own app pool,
    plus a small CPU-burning page for generating load.

.EXAMPLE
    .\Setup-TestPools.ps1 -Count 20
    Creates /pooltest01../pooltest20 in "Default Web Site", each in its own pool.

.EXAMPLE
    .\Setup-TestPools.ps1 -Remove
    Removes every application and app pool whose name starts with the prefix, and the content folder.

.NOTES
    Requires IIS with ASP.NET 4.x (the page is a single inline .aspx file, no build step).
      Windows Server : Install-WindowsFeature Web-Asp-Net45
      Windows 10/11  : Enable-WindowsOptionalFeature -Online -FeatureName IIS-ASPNET45 -All
#>
[CmdletBinding()]
param(
    [string]$SiteName = 'Default Web Site',
    [ValidateRange(1, 200)]
    [int]$Count = 10,
    [string]$Prefix = 'pooltest',
    [string]$PhysicalRoot = "$env:SystemDrive\inetpub\$Prefix",
    [string]$BaseUrl,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
Import-Module WebAdministration

function Get-SiteBaseUrl([string]$Site) {
    $binding = Get-WebBinding -Name $Site -Protocol http | Select-Object -First 1
    if (-not $binding) { throw "Site '$Site' has no http binding; pass -BaseUrl." }
    $null, $port, $hostHeader = $binding.bindingInformation -split ':'
    $hostName = if ($hostHeader) { $hostHeader } else { 'localhost' }
    "http://${hostName}:$port"
}

if (-not (Get-Website -Name $SiteName)) { throw "IIS site '$SiteName' not found." }

if ($Remove) {
    Get-WebApplication -Site $SiteName |
        Where-Object { $_.path -like "/$Prefix*" } |
        ForEach-Object {
            Remove-WebApplication -Site $SiteName -Name $_.path.TrimStart('/')
            Write-Host "Removed application $($_.path)"
        }

    Get-ChildItem IIS:\AppPools |
        Where-Object { $_.Name -like "$Prefix*" } |
        ForEach-Object {
            Remove-WebAppPool -Name $_.Name
            Write-Host "Removed app pool $($_.Name)"
        }

    if (Test-Path $PhysicalRoot) {
        Start-Sleep -Seconds 2   # let worker processes exit and release files
        Remove-Item $PhysicalRoot -Recurse -Force
        Write-Host "Removed $PhysicalRoot"
    }
    return
}

# CPU-burning page: spins for ?ms=N milliseconds (default 50, max 5000) and reports
# which pool and PID served the request -- handy for checking a monitor's PID-to-pool mapping.
$page = @'
<%@ Page Language="C#" %>
<script runat="server">
protected void Page_Load(object sender, EventArgs e)
{
    int ms;
    if (!int.TryParse(Request.QueryString["ms"], out ms) || ms < 0) ms = 50;
    if (ms > 5000) ms = 5000;

    var sw = System.Diagnostics.Stopwatch.StartNew();
    double x = 0;
    while (sw.ElapsedMilliseconds < ms) { x += Math.Sqrt(sw.ElapsedTicks); }

    Response.ContentType = "text/plain";
    Response.Write(string.Format("pool={0} pid={1} ms={2} x={3:F0}",
        Environment.GetEnvironmentVariable("APP_POOL_ID"),
        System.Diagnostics.Process.GetCurrentProcess().Id,
        ms, x));
}
</script>
'@

if (-not $BaseUrl) { $BaseUrl = Get-SiteBaseUrl $SiteName }

foreach ($i in 1..$Count) {
    $name = '{0}{1:D2}' -f $Prefix, $i
    $dir  = Join-Path $PhysicalRoot $name

    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    Set-Content -Path (Join-Path $dir 'burn.aspx') -Value $page -Encoding UTF8

    $poolPath = "IIS:\AppPools\$name"
    if (-not (Test-Path $poolPath)) { New-WebAppPool -Name $name | Out-Null }
    Set-ItemProperty $poolPath -Name managedRuntimeVersion -Value 'v4.0'
    Set-ItemProperty $poolPath -Name managedPipelineMode   -Value 'Integrated'
    Set-ItemProperty $poolPath -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)  # don't idle out mid-test

    if (-not (Get-WebApplication -Site $SiteName -Name $name)) {
        New-WebApplication -Site $SiteName -Name $name -PhysicalPath $dir -ApplicationPool $name | Out-Null
    }

    # Warm up: starts the worker process and compiles the page.
    $url = "$BaseUrl/$name/burn.aspx?ms=1"
    try {
        $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 60
        Write-Host ("{0,-12} OK   {1}" -f $name, $response.Content)
    }
    catch {
        Write-Warning "$name : request to $url failed: $($_.Exception.Message)"
        Write-Warning 'If this is HTTP 404.3 or 500, ASP.NET 4.x is probably not installed (see the script notes).'
    }
}

Write-Host "`nCreated $Count applications under '$SiteName' at $BaseUrl/$Prefix##/burn.aspx"
