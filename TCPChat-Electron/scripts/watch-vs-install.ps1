<#
.SYNOPSIS
    Watch a Visual Studio installer run to completion.

.DESCRIPTION
    The VS bootstrapper detaches: `vs_Setup.exe --wait` returns as soon as it has
    unpacked itself, so the shell comes back long before anything is installed and
    nothing in the console says how far along it is.

    This polls the three things that actually move - the installer processes, the
    package cache on disk, and the newest installer log - and exits when the
    installer is gone, reporting what landed.

    It is read-only. It never touches the install.

.PARAMETER IntervalSec
    Seconds between samples. Default 10.

.PARAMETER Once
    Print a single sample and exit. Useful for a quick look.

.PARAMETER TimeoutMin
    Give up after this many minutes. Default 180.

.PARAMETER Quiet
    Only print when something changes, plus the final summary.

.EXAMPLE
    .\watch-vs-install.ps1
    .\watch-vs-install.ps1 -Once
    .\watch-vs-install.ps1 -IntervalSec 30 -Quiet
#>
[CmdletBinding()]
param(
    [int]$IntervalSec = 10,
    [switch]$Once,
    [int]$TimeoutMin = 180,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

$InstallerDir = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
$Vswhere = Join-Path $InstallerDir 'vswhere.exe'
$PackageCache = "$env:ProgramData\Microsoft\VisualStudio\Packages"
$Vs18Root = "${env:ProgramFiles}\Microsoft Visual Studio\18"

function Get-InstallerProcesses {
    Get-Process -ErrorAction SilentlyContinue |
        # 'setup' and 'winsdksetup' are the ones that do the work; the
        # bootstrapper hands off and exits almost immediately. Leaving them out
        # reports 'idle' through the busiest part of an install.
        Where-Object { $_.ProcessName -match 'setup|vs_Setup|vs_setup_bootstrapper|vs_installer|VSIXInstaller' }
}

function Get-CacheSize {
    if (-not (Test-Path $PackageCache)) { return [pscustomobject]@{ Bytes = 0; Files = 0 } }
    $m = Get-ChildItem $PackageCache -Recurse -File -ErrorAction SilentlyContinue |
        Measure-Object -Property Length -Sum
    [pscustomobject]@{ Bytes = [int64]($m.Sum | ForEach-Object { if ($_) { $_ } else { 0 } }); Files = $m.Count }
}

function Get-NewestLog {
    Get-ChildItem $env:TEMP -Filter 'dd_*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
}

function Format-Size([int64]$bytes) {
    if ($bytes -ge 1GB) { return ('{0:N2} GB' -f ($bytes / 1GB)) }
    if ($bytes -ge 1MB) { return ('{0:N1} MB' -f ($bytes / 1MB)) }
    return ('{0:N0} kB' -f ($bytes / 1KB))
}

function Get-VsInstallations {
    if (-not (Test-Path $Vswhere)) { return @() }
    $names = & $Vswhere -all -products * -prerelease -property displayName 2>$null
    $vers = & $Vswhere -all -products * -prerelease -property installationVersion 2>$null
    $paths = & $Vswhere -all -products * -prerelease -property installationPath 2>$null
    for ($i = 0; $i -lt @($names).Count; $i++) {
        [pscustomobject]@{
            Name    = @($names)[$i]
            Version = @($vers)[$i]
            Path    = @($paths)[$i]
        }
    }
}

function Write-Sample($prev) {
    $procs = @(Get-InstallerProcesses)
    $cache = Get-CacheSize
    $log = Get-NewestLog

    $delta = if ($null -ne $prev) { $cache.Bytes - $prev.Bytes } else { 0 }
    $rate = if ($null -ne $prev -and $IntervalSec -gt 0) { $delta / $IntervalSec } else { 0 }

    $stamp = Get-Date -Format 'HH:mm:ss'
    $state = if ($procs.Count -gt 0) { "running ($($procs.Count) proc)" } else { 'idle' }
    $line = "  $stamp  $state  cache $(Format-Size $cache.Bytes) / $($cache.Files) files"
    if ($delta -ne 0) { $line += "  (+$(Format-Size $delta), $(Format-Size ([int64]$rate))/s)" }

    if (-not $Quiet -or $delta -ne 0 -or $procs.Count -eq 0) { Write-Host $line }

    if ($log) {
        $tail = Get-Content $log.FullName -Tail 1 -ErrorAction SilentlyContinue
        if ($tail -and -not $Quiet) { Write-Host "             log: $($tail.Trim())" -ForegroundColor DarkGray }
    }

    [pscustomobject]@{ Procs = $procs; Cache = $cache; Log = $log }
}

Write-Host ''
Write-Host '  Visual Studio installer watch' -ForegroundColor Cyan
Write-Host "  package cache : $PackageCache"
Write-Host "  interval      : ${IntervalSec}s   timeout: ${TimeoutMin} min"
Write-Host ''

$start = Get-Date
$prev = $null
$everSawInstaller = $false

while ($true) {
    $s = Write-Sample $prev
    $prev = $s.Cache

    if ($s.Procs.Count -gt 0) { $everSawInstaller = $true }
    elseif ($everSawInstaller) { break }          # it was running, now it is not
    elseif ($Once) { break }                       # nothing to watch
    elseif (((Get-Date) - $start).TotalMinutes -gt 2) {
        Write-Host ''
        Write-Host '  No installer is running (and none appeared in the first two minutes).' -ForegroundColor Yellow
        break
    }

    if ($Once) { break }
    if (((Get-Date) - $start).TotalMinutes -gt $TimeoutMin) {
        Write-Host ''
        Write-Host "  Timed out after ${TimeoutMin} minutes; the installer may still be running." -ForegroundColor Yellow
        break
    }
    Start-Sleep -Seconds $IntervalSec
}

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------

Write-Host ''
Write-Host '  --- installations ---' -ForegroundColor Cyan
$installs = Get-VsInstallations
if ($installs.Count -eq 0) {
    Write-Host '  (vswhere reports nothing)'
} else {
    foreach ($i in $installs) {
        Write-Host "  $($i.Version)  $($i.Name)"
        Write-Host "      $($i.Path)" -ForegroundColor DarkGray
    }
}

if (Test-Path $Vs18Root) {
    $sub = Get-ChildItem $Vs18Root -Directory -ErrorAction SilentlyContinue
    if ($sub) {
        Write-Host ''
        Write-Host '  --- VS 18 ---' -ForegroundColor Cyan
        foreach ($d in $sub) { Write-Host "  $($d.FullName)" }
    }
}

$log = Get-NewestLog
if ($log) {
    Write-Host ''
    Write-Host "  --- installer log: $($log.Name) ---" -ForegroundColor Cyan
    Get-Content $log.FullName -Tail 12 -ErrorAction SilentlyContinue |
        ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
}

$still = @(Get-InstallerProcesses)
if ($still.Count -gt 0) {
    Write-Host ''
    Write-Host '  Installer still running; re-run this script to keep watching.' -ForegroundColor Yellow
}
