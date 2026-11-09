<#
.SYNOPSIS
    Publishes the Windows app self-contained for x64 and ARM64, with ReadyToRun and the unsigned MSIX.

.DESCRIPTION
    Runs, per runtime identifier, from the repository's windows/ folder:

        dotnet publish src/PasswordManager.App/PasswordManager.App.csproj -c Release -r <rid>
            -p:Platform=<x64|ARM64> -p:AppxPackageDir=AppPackages\<rid>\
            -p:RestorePackagesWithLockFile=false -p:NuGetLockFilePath=obj/publish-release.lock.json
            -p:PublishReadyToRun=true -p:PublishAppxPackage=true

    Trimming is OFF: D5 resolved against it because the Windows App SDK/WinRT assemblies are not trim-compatible. A
    real trim fails with IL2104 in Microsoft.Windows.SDK.NET and WinRT.Runtime, then NETSDK1144; the warnings are not
    suppressed. -Trim adds -p:PublishTrimmed=true for the record (and is expected to fail today); Core keeps its
    IsTrimmable compatibility, the App does not trim.

    The csproj's _EnsurePublishItemsForOptimization works around an MSIX ordering trap: EnableMsixTooling puts
    PrepareMsixPackage into PrepareForRunDependsOn, and the packaging snapshot runs BeforeTargets CreateReadyToRunImages
    and ILLink before ComputeResolvedFilesToPublishList has filled ResolvedFileToPublish. Without the target the
    optimizations run on an empty list, get cached as built, and the publish silently skips them. PublishAppxPackage
    hooks the package after Publish; GenerateAppxPackageOnBuild would run the packaging payload inside Build.

    AppendPlatformToOutputPath=false (csproj) keeps the artifacts under bin/Release/<tfm>/<rid>/publish (the SDK would
    add an x64/ segment for a non-AnyCPU Platform). AppxPackageDir is per RID because the packaging targets clean their
    package directory on every publish: a shared directory lets the second RID delete the first RID's .msix.
    RestorePackagesWithLockFile=false keeps Microsoft.NET.ILLink.Tasks out of the committed lockfile for this one path;
    NuGetLockFilePath moves the (unused) lock-file probe into obj/, because NuGet errors NU1005 when lock files are
    disabled while the committed ones exist. Every other package is still pinned by Directory.Packages.props.

    Each artifact is verified: the exe exists, its PE FileHeader.Machine is 0x8664 (win-x64) or 0xAA64 (win-arm64),
    and the publish folder carries the RID's native libsodium.dll. The script prints the RID's publish folder, its
    AppPackages .msix found by glob, and both sizes in bytes and MiB (.pdb excluded from the folder size), always from
    the fresh publish output. A final assertion re-checks that every requested RID still has its exe, publish folder
    and .msix after all publishes.

    Artifact contract (docs/architecture.md §9.17):
        src/PasswordManager.App/bin/Release/<tfm>/<rid>/publish/PasswordManager.App.exe
        src/PasswordManager.App/AppPackages/<rid>/**/*_<arch>.msix

    The script writes nothing outside bin/, obj/ and AppPackages/. ARM64 cross-builds here on x64 and cannot be
    executed on this machine; no signing key is involved, so the .msix is unsigned.

.PARAMETER RuntimeIdentifier
    One RID to publish: win-x64 or win-arm64, or both (the default).

.PARAMETER Trim
    Opt into PublishTrimmed=true. Known to fail today (IL2104 in Microsoft.Windows.SDK.NET/WinRT.Runtime, NETSDK1144);
    the warnings are not suppressed, so this exists to exercise the D5 result, not as a supported release path.

.PARAMETER NoReadyToRun
    Omit PublishReadyToRun=true.

.PARAMETER NoPackage
    Omit PublishAppxPackage=true: publish only, no .msix.

.PARAMETER Clean
    Run dotnet clean for both RIDs and delete their publish and AppPackages/<rid> folders first, so the reported sizes
    come from a fresh publish (dotnet clean alone leaves the publish folders behind).

.EXAMPLE
    pwsh -NoProfile -File tools/Publish-Release.ps1 -Clean

.EXAMPLE
    pwsh -NoProfile -File tools/Publish-Release.ps1 -RuntimeIdentifier win-x64 -NoPackage

.EXAMPLE
    pwsh -NoProfile -File tools/Publish-Release.ps1 -RuntimeIdentifier win-arm64 -NoPackage
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64', 'both')]
    [string]$RuntimeIdentifier = 'both',
    [switch]$Trim,
    [switch]$NoReadyToRun,
    [switch]$NoPackage,
    [switch]$Clean
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$WindowsDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$ProjectPath = Join-Path $WindowsDir 'src\PasswordManager.App\PasswordManager.App.csproj'
$AppPackagesDir = Join-Path $WindowsDir 'src\PasswordManager.App\AppPackages'

# The four facts each RID needs: the dotnet -r argument, the MSBuild Platform, the PE machine, the MSIX arch token.
$Targets = @(
    @{ Rid = 'win-x64';   Platform = 'x64';   PeMachine = 0x8664; MsixArch = 'x64' }
    @{ Rid = 'win-arm64'; Platform = 'ARM64'; PeMachine = 0xAA64; MsixArch = 'arm64' }
)

function Invoke-DotNet {
    param(
        [string[]]$Arguments,
        [string]$Description
    )

    Write-Host "> dotnet $($Arguments -join ' ')"
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

# e_lfanew from the DOS header, then the PE signature and FileHeader.Machine (0x8664 x64, 0xAA64 ARM64).
function Get-PeMachine {
    param([string]$Path)

    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
        $reader = [System.IO.BinaryReader]::new($stream)
        try {
            if ($stream.Length -lt 0x40) {
                throw "Not a PE file (shorter than the DOS header): $Path"
            }

            $stream.Position = 0x3C
            $peOffset = $reader.ReadInt32()
            if ($peOffset -lt 0 -or ($peOffset + 6) -gt $stream.Length) {
                throw "Not a PE file (e_lfanew $peOffset points outside the file): $Path"
            }

            $stream.Position = $peOffset
            if ($reader.ReadUInt32() -ne 0x00004550) {
                throw "Not a PE file (no PE signature): $Path"
            }

            return $reader.ReadUInt16()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Format-Size {
    param([long]$Bytes)

    return '{0:N0} bytes ({1:N2} MiB)' -f $Bytes, ($Bytes / 1MB)
}

if (-not (Test-Path -LiteralPath $ProjectPath -PathType Leaf)) {
    throw "Project not found: $ProjectPath"
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet was not found on PATH.'
}

# The publish path follows the project's TargetFramework; read it instead of duplicating it here.
$project = [xml](Get-Content -LiteralPath $ProjectPath -Raw)
$TargetFrameworkNode = $project.SelectSingleNode('/Project/PropertyGroup/TargetFramework')
if (-not $TargetFrameworkNode) {
    throw "$ProjectPath has no TargetFramework."
}
$TargetFramework = $TargetFrameworkNode.InnerText

$Selected = if ($RuntimeIdentifier -eq 'both') { $Targets } else { @($Targets | Where-Object { $_.Rid -eq $RuntimeIdentifier }) }

# ---- Clean (both RIDs, whatever was selected: stale exports of the other RID would skew the comparison) ----

if ($Clean) {
    foreach ($target in $Targets) {
        Invoke-DotNet -Description "dotnet clean ($($target.Rid))" -Arguments @(
            'clean', $ProjectPath, '-c', 'Release', '-r', $target.Rid, "-p:Platform=$($target.Platform)"
        )

        # dotnet clean leaves the publish folder behind; stale files skew the sizes and the -Clean guarantee.
        $publishDir = Join-Path $WindowsDir "src\PasswordManager.App\bin\Release\$TargetFramework\$($target.Rid)\publish"
        if (Test-Path -LiteralPath $publishDir) {
            Remove-Item -LiteralPath $publishDir -Recurse -Force
        }

        $ridPackageDir = Join-Path $AppPackagesDir $target.Rid
        if (Test-Path -LiteralPath $ridPackageDir) {
            Remove-Item -LiteralPath $ridPackageDir -Recurse -Force
        }
    }
}

# ---- Publish ----

$results = @()

foreach ($target in $Selected) {
    $ridPackageDir = Join-Path $AppPackagesDir $target.Rid
    $publishArguments = @(
        'publish', $ProjectPath,
        '-c', 'Release',
        '-r', $target.Rid,
        "-p:Platform=$($target.Platform)",
        "-p:AppxPackageDir=$ridPackageDir\",
        '-p:RestorePackagesWithLockFile=false',
        '-p:NuGetLockFilePath=obj/publish-release.lock.json'
    )
    if ($Trim) { $publishArguments += '-p:PublishTrimmed=true' }
    if (-not $NoReadyToRun) { $publishArguments += '-p:PublishReadyToRun=true' }
    if (-not $NoPackage) { $publishArguments += '-p:PublishAppxPackage=true' }

    Invoke-DotNet -Description "dotnet publish ($($target.Rid))" -Arguments $publishArguments

    $publishDir = Join-Path $WindowsDir "src\PasswordManager.App\bin\Release\$TargetFramework\$($target.Rid)\publish"
    $exePath = Join-Path $publishDir 'PasswordManager.App.exe'
    if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
        throw "Publish succeeded but the exe is missing: $exePath"
    }

    $machine = Get-PeMachine -Path $exePath
    if ($machine -ne $target.PeMachine) {
        throw ("The published exe is not {0}: PE machine is 0x{1:X4}, expected 0x{2:X4}." -f $target.Rid, $machine, $target.PeMachine)
    }

    $libsodium = @(Get-ChildItem -LiteralPath $publishDir -Filter 'libsodium.dll' -File -Recurse)
    if ($libsodium.Count -eq 0) {
        throw "The RID's native libsodium.dll is missing from $publishDir."
    }

    $publishBytes = [long]0
    $publishFiles = 0
    foreach ($file in Get-ChildItem -LiteralPath $publishDir -File -Recurse) {
        if ($file.Extension -eq '.pdb') { continue }
        $publishBytes += $file.Length
        $publishFiles++
    }

    $msixPath = $null
    $msixBytes = [long]0
    if (-not $NoPackage) {
        $candidates = @(
            Get-ChildItem -LiteralPath $ridPackageDir -Filter "*_$($target.MsixArch).msix" -File -Recurse -ErrorAction SilentlyContinue |
                Sort-Object -Property LastWriteTime -Descending
        )
        if ($candidates.Count -eq 0) {
            throw "No .msix matching *_$($target.MsixArch).msix under $ridPackageDir."
        }
        if ($candidates[0].LastWriteTime -lt (Get-Item -LiteralPath $exePath).LastWriteTime) {
            throw "The .msix under $ridPackageDir is older than the publish output: stale package, not this publish."
        }
        $msixPath = $candidates[0].FullName
        $msixBytes = $candidates[0].Length
    }

    Write-Host ''
    Write-Host ("{0}: PE machine 0x{1:X4}, libsodium {2}" -f $target.Rid, $machine, $libsodium[0].FullName)
    Write-Host "  exe:     $exePath"
    Write-Host "  publish: $publishDir ($publishFiles files, $(Format-Size $publishBytes), *.pdb excluded)"
    if ($msixPath) {
        Write-Host "  msix:    $msixPath ($(Format-Size $msixBytes), unsigned)"
    }
    else {
        Write-Host '  msix:    not built (-NoPackage)'
    }

    $results += [pscustomobject]@{
        Rid          = $target.Rid
        Machine      = '0x{0:X4}' -f $machine
        PublishDir   = $publishDir
        ExePath      = $exePath
        PublishBytes = $publishBytes
        PublishMiB   = [Math]::Round($publishBytes / 1MB, 2)
        PublishFiles = $publishFiles
        MsixPath     = $msixPath
        MsixBytes    = $msixBytes
        MsixMiB      = [Math]::Round($msixBytes / 1MB, 2)
        Package      = [bool]$msixPath
    }
}

# ---- Final assertion: a later RID must not remove an earlier RID's artifacts ----

$missing = @()
foreach ($result in $results) {
    if (-not (Test-Path -LiteralPath $result.PublishDir -PathType Container)) {
        $missing += "$($result.Rid): publish folder gone: $($result.PublishDir)"
    }
    if (-not (Test-Path -LiteralPath $result.ExePath -PathType Leaf)) {
        $missing += "$($result.Rid): exe gone: $($result.ExePath)"
    }
    if (-not $NoPackage -and -not (Test-Path -LiteralPath $result.MsixPath -PathType Leaf)) {
        $missing += "$($result.Rid): .msix gone: $($result.MsixPath)"
    }
}
if ($missing.Count -gt 0) {
    throw ("An artifact disappeared after all publishes:`n  {0}" -f ($missing -join "`n  "))
}

Write-Host ''
Write-Host 'Verified: every requested RID still has its publish folder, exe and .msix.'

# ---- Summary ----

Write-Host ''
Write-Host ($results | Format-Table -AutoSize -Property Rid, Machine, PublishFiles, PublishBytes, PublishMiB, MsixBytes, MsixMiB, Package | Out-String).TrimEnd()
Write-Host ("Published {0}: trimmed={1}, ReadyToRun={2}, packaged={3}." -f
    (($results | ForEach-Object { $_.Rid }) -join ', '),
    $Trim, (-not $NoReadyToRun), (-not $NoPackage))
