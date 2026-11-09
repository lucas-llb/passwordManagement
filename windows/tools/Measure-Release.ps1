<#
.SYNOPSIS
    Measures the Windows release artifacts: package and publish-folder size plus the warm startup time.

.DESCRIPTION
    Size: the .msix file length (when -MsixPath is given) and the publish folder total, *.pdb excluded, with the file
    count. These are the numbers docs/architecture.md §9.17 records.

    Startup (warm): every run starts the published exe, times from Process.Start to the first visible main window
    (Refresh() until MainWindowHandle is non-zero and IsWindowVisible is true, polled every 5 ms), then closes the window,
    waits up to 5 s and kills the process otherwise, and pauses 200 ms. The first -Warmup runs are discarded; the rest are
    printed one per line plus median, min and max. A run that never shows a visible window fails the script. The script
    refuses to start while another instance of the app is running.

    Cold startup cannot be measured here: dropping the file cache needs elevation. Reboot, then run with -Warmup 0
    -Runs 1 and record the number by hand.

    The script only reads the exe, the .msix and the publish folder; it writes nothing except the optional CSV, which must
    be outside the repository.

.PARAMETER ExePath
    The published PasswordManager.App.exe, typically
    src/PasswordManager.App/bin/Release/<tfm>/win-x64/publish/PasswordManager.App.exe.

.PARAMETER MsixPath
    Optional .msix produced by tools/Publish-Release.ps1 (locate it by the AppPackages glob *_<arch>.msix).

.PARAMETER Runs
    Measured runs after the warmup. Default 10.

.PARAMETER Warmup
    Runs discarded to warm the file cache. Default 2.

.PARAMETER CsvPath
    Optional path for a CSV of this run (sizes and every measured startup). Must be outside the repository: measured
    numbers are recorded in the docs, not committed.

.EXAMPLE
    pwsh -NoProfile -File tools/Measure-Release.ps1 -ExePath src/PasswordManager.App/bin/Release/net10.0-windows10.0.26100.0/win-x64/publish/PasswordManager.App.exe -MsixPath src/PasswordManager.App/AppPackages/PasswordManager.App_1.0.0.0_Test/PasswordManager.App_1.0.0.0_x64.msix -Runs 10

.EXAMPLE
    pwsh -NoProfile -File tools/Measure-Release.ps1 -ExePath $env:TEMP\pm-untrimmed\PasswordManager.App.exe -CsvPath $env:TEMP\pm-measure.csv
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ExePath,
    [string]$MsixPath,
    [ValidateRange(1, 1000)]
    [int]$Runs = 10,
    [ValidateRange(0, 1000)]
    [int]$Warmup = 2,
    [string]$CsvPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The repository root is two levels above windows/tools; a CSV must never land inside it.
$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))

function Format-Size {
    param([long]$Bytes)

    return '{0:N0} bytes ({1:N2} MiB)' -f $Bytes, ($Bytes / 1MB)
}

# The main window is only counted once the OS reports it visible: a handle alone can belong to a hidden host window.
if (-not ('ReleaseMeasurement.NativeWindow' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace ReleaseMeasurement
{
    public static class NativeWindow
    {
        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);
    }
}
'@
}

$ExePath = [System.IO.Path]::GetFullPath($ExePath)
if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
    throw "The published exe was not found: $ExePath. Build it first with tools/Publish-Release.ps1."
}

$PublishDir = Split-Path -Parent $ExePath

if ($MsixPath) {
    $MsixPath = [System.IO.Path]::GetFullPath($MsixPath)
    if (-not (Test-Path -LiteralPath $MsixPath -PathType Leaf)) {
        throw "The .msix was not found: $MsixPath"
    }
}

if ($CsvPath) {
    $CsvPath = [System.IO.Path]::GetFullPath($CsvPath)
    $repoPrefix = $RepoRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if ($CsvPath.StartsWith($repoPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "CsvPath must be outside the repository ($RepoRoot): $CsvPath"
    }

    $csvDir = Split-Path -Parent $CsvPath
    if (-not (Test-Path -LiteralPath $csvDir -PathType Container)) {
        throw "The CsvPath folder does not exist: $csvDir"
    }
}

# Two measurements on top of each other would count a window that is already open as a startup.
$processName = [System.IO.Path]::GetFileNameWithoutExtension($ExePath)
$running = @(Get-Process -Name $processName -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    throw ("{0} is already running (PID(s): {1}); close it before measuring." -f
        $processName, (($running | ForEach-Object { $_.Id }) -join ', '))
}

# ---- Size ----

$publishBytes = [long]0
$publishFiles = 0
foreach ($file in Get-ChildItem -LiteralPath $PublishDir -File -Recurse) {
    if ($file.Extension -eq '.pdb') { continue }
    $publishBytes += $file.Length
    $publishFiles++
}

$msixBytes = $null
if ($MsixPath) {
    $msixBytes = (Get-Item -LiteralPath $MsixPath).Length
}

Write-Host ''
Write-Host ("publish: {0} files, {1} (*.pdb excluded)" -f $publishFiles, (Format-Size $publishBytes))
if ($null -ne $msixBytes) {
    Write-Host ("msix:    {0}" -f (Format-Size $msixBytes))
}
else {
    Write-Host 'msix:    not measured (-MsixPath not given)'
}
Write-Host ''

# ---- Warm startup ----

$results = [System.Collections.Generic.List[object]]::new()
$measured = [System.Collections.Generic.List[double]]::new()
$total = $Warmup + $Runs

for ($run = 1; $run -le $total; $run++) {
    $isWarmup = $run -le $Warmup
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($ExePath)
    $startInfo.UseShellExecute = $false
    $startInfo.WorkingDirectory = $PublishDir

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $process = [System.Diagnostics.Process]::Start($startInfo)
    if (-not $process) {
        throw "Could not start $ExePath."
    }

    $milliseconds = 0.0
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while ($true) {
            if ($process.HasExited) {
                throw ("The app exited with code {0} before showing a visible window (run {1})." -f
                    $process.ExitCode, $run)
            }

            $process.Refresh()
            $handle = $process.MainWindowHandle
            if ($handle -ne [IntPtr]::Zero -and [ReleaseMeasurement.NativeWindow]::IsWindowVisible($handle)) {
                break
            }

            if ([DateTime]::UtcNow -ge $deadline) {
                throw "The app did not show a visible window within 30 s (run $run)."
            }

            [System.Threading.Thread]::Sleep(5)
        }

        $stopwatch.Stop()
        $milliseconds = [Math]::Round($stopwatch.Elapsed.TotalMilliseconds, 1)
    }
    finally {
        $stopwatch.Stop()
        if (-not $process.HasExited) {
            [void]$process.CloseMainWindow()
            if (-not $process.WaitForExit(5000)) {
                $process.Kill()
                [void]$process.WaitForExit(5000)
            }
        }

        $process.Dispose()
    }

    $results.Add([pscustomobject]@{ Run = $run; Warmup = $isWarmup; Milliseconds = $milliseconds })
    if (-not $isWarmup) {
        $measured.Add($milliseconds)
    }

    Write-Host ("run {0,2}{1}: {2,8:N1} ms" -f $run, $(if ($isWarmup) { ' (warmup)' } else { '         ' }), $milliseconds)
    [System.Threading.Thread]::Sleep(200)
}

# ---- Summary ----

$sorted = @($measured | Sort-Object)
$count = $sorted.Count
if ($count -eq 0) {
    throw 'No measured runs: increase -Runs.'
}

$median = if ($count % 2 -eq 1) {
    $sorted[[int](($count - 1) / 2)]
}
else {
    [Math]::Round(($sorted[[int]($count / 2) - 1] + $sorted[[int]($count / 2)]) / 2, 1)
}
$minimum = $sorted[0]
$maximum = $sorted[$count - 1]

Write-Host ''
Write-Host ("Warm startup: median {0:N1} ms, min {1:N1} ms, max {2:N1} ms over {3} runs ({4} warmup discarded)." -f
    $median, $minimum, $maximum, $count, $Warmup)
Write-Host 'Cold startup is manual: reboot, then run with -Warmup 0 -Runs 1 (dropping the file cache needs elevation).'

if ($CsvPath) {
    $rows = [System.Collections.Generic.List[object]]::new()
    $rows.Add([pscustomobject]@{ Category = 'size'; Item = 'publish-bytes'; Value = $publishBytes })
    $rows.Add([pscustomobject]@{ Category = 'size'; Item = 'publish-mib'; Value = [Math]::Round($publishBytes / 1MB, 2) })
    $rows.Add([pscustomobject]@{ Category = 'size'; Item = 'publish-files'; Value = $publishFiles })
    if ($null -ne $msixBytes) {
        $rows.Add([pscustomobject]@{ Category = 'size'; Item = 'msix-bytes'; Value = $msixBytes })
        $rows.Add([pscustomobject]@{ Category = 'size'; Item = 'msix-mib'; Value = [Math]::Round($msixBytes / 1MB, 2) })
    }

    foreach ($result in $results) {
        $label = if ($result.Warmup) { "run-$($result.Run)-warmup-ms" } else { "run-$($result.Run)-ms" }
        $rows.Add([pscustomobject]@{ Category = 'startup'; Item = $label; Value = $result.Milliseconds })
    }

    $rows.Add([pscustomobject]@{ Category = 'startup'; Item = 'median-ms'; Value = $median })
    $rows.Add([pscustomobject]@{ Category = 'startup'; Item = 'min-ms'; Value = $minimum })
    $rows.Add([pscustomobject]@{ Category = 'startup'; Item = 'max-ms'; Value = $maximum })

    $rows | Export-Csv -LiteralPath $CsvPath -NoTypeInformation -Encoding utf8
    Write-Host "CSV: $CsvPath"
}

Write-Host ("Measured {0}: {1} measured runs, exe {2}." -f $processName, $count, $ExePath)
