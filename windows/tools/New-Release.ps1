<#
.SYNOPSIS
    Publishes, signs, verifies and hashes the Windows MSIX for a GitHub Releases or Store upload.

.DESCRIPTION
    Wraps tools/Publish-Release.ps1 (left untouched, so its W-29 artifact contract and tests stay valid) in the release
    steps that must not be skipped:

      1. Preflight. The manifest Identity and the App csproj <Version> must agree (<Version> + ".0" equals
         Identity/@Version), CHANGELOG.md must carry a "## [<Version>]" section, and the code-signing certificate must
         exist outside the repository.
      2. Publish. tools/Publish-Release.ps1 runs as a child process, exactly as a developer would run it.
      3. Sign. signtool sign /fd SHA256 /a /f <pfx> /p <password> <msix>. The password is never printed; it comes from
         PM_CERT_PASSWORD (preferred) or -CertificatePassword. -TimestampUrl is opt-in because it needs network.
      4. Verify and hash. Get-AuthenticodeSignature must report Valid (NotSigned is accepted only with -Unsigned), and
         only then the .sha256 file is written beside the package in sha256sum form: "<hex> *<file name>".

    Artifact contract, unchanged from Publish-Release.ps1:
        src/PasswordManager.App/bin/Release/<tfm>/<rid>/publish/PasswordManager.App.exe
        src/PasswordManager.App/AppPackages/<rid>/**/*_<arch>.msix
    plus the new <msix>.sha256 beside each package. Both folders are gitignored.

    The certificate and its password never enter the repository: a -CertificatePath inside the repository is refused
    before anything is published. The publisher check is verbatim (byte-for-byte), because the manifest Publisher and
    the certificate Subject must match exactly; on mismatch the script prints the exact Publisher="..." line to paste
    into Package.appxmanifest and stops before signing.

.PARAMETER RuntimeIdentifier
    One RID to release: win-x64 or win-arm64, or both (the default).

.PARAMETER CertificatePath
    Path to the code-signing .pfx, outside the repository. Required unless -Unsigned.

.PARAMETER CertificatePassword
    PFX password. Prefer the PM_CERT_PASSWORD environment variable; neither value is ever printed.

.PARAMETER TimestampUrl
    Optional RFC 3161 timestamp server. Adds /tr <url> /td SHA256; needs network, so it is off by default.

.PARAMETER Unsigned
    Explicit dry run: skip signing, expect NotSigned, still verify and write the hash.

.PARAMETER SkipPublish
    Re-sign and hash the package already under AppPackages/<rid>/ instead of publishing again.

.PARAMETER Clean
    Forwarded to Publish-Release.ps1: rebuild from a clean publish and package folder.

.EXAMPLE
    pwsh -NoProfile -File tools/New-Release.ps1 -Clean

.EXAMPLE
    pwsh -NoProfile -File tools/New-Release.ps1 -RuntimeIdentifier win-x64 -Unsigned

.EXAMPLE
    $env:PM_CERT_PASSWORD = Read-Host -MaskInput
    pwsh -NoProfile -File tools/New-Release.ps1 -RuntimeIdentifier win-x64 -SkipPublish -CertificatePath D:\keys\release.pfx
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64', 'both')]
    [string]$RuntimeIdentifier = 'both',
    [string]$CertificatePath,
    [string]$CertificatePassword,
    [string]$TimestampUrl,
    [switch]$Unsigned,
    [switch]$SkipPublish,
    [switch]$Clean
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$WindowsDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$ProjectPath = Join-Path $WindowsDir 'src\PasswordManager.App\PasswordManager.App.csproj'
$ManifestPath = Join-Path $WindowsDir 'src\PasswordManager.App\Package.appxmanifest'
$AppPackagesDir = Join-Path $WindowsDir 'src\PasswordManager.App\AppPackages'
$PublishScriptPath = Join-Path $PSScriptRoot 'Publish-Release.ps1'
$ChangelogPath = Join-Path $RepoRoot 'CHANGELOG.md'

$Targets = @(
    @{ Rid = 'win-x64';   Platform = 'x64';   MsixArch = 'x64' }
    @{ Rid = 'win-arm64'; Platform = 'ARM64'; MsixArch = 'arm64' }
)

$CodeSigningEku = '1.3.6.1.5.5.7.3.3'

function Format-Size {
    param([long]$Bytes)

    return '{0:N0} bytes ({1:N2} MiB)' -f $Bytes, ($Bytes / 1MB)
}

# PATH first, then the restored Microsoft.Windows.SDK.BuildTools package (the App already depends on it, so the restore
# is offline). Every path tried is named in the error: a package bump moves the signtool.exe folder.
function Resolve-SignTool {
    $onPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($onPath) {
        return $onPath.Source
    }

    $tried = [System.Collections.Generic.List[string]]::new()
    $tried.Add('PATH: signtool.exe not found')

    $packageRoots = @()
    if ($env:NUGET_PACKAGES) {
        $packageRoots += $env:NUGET_PACKAGES
    }
    if ($env:USERPROFILE) {
        $packageRoots += (Join-Path $env:USERPROFILE '.nuget\packages')
    }

    foreach ($root in $packageRoots) {
        $buildToolsDir = Join-Path $root 'microsoft.windows.sdk.buildtools'
        $tried.Add($buildToolsDir)
        if (-not (Test-Path -LiteralPath $buildToolsDir -PathType Container)) {
            continue
        }

        $candidates = @(
            Get-ChildItem -LiteralPath $buildToolsDir -Directory |
                ForEach-Object { Get-ChildItem -Path (Join-Path $_.FullName 'bin\*\x64\signtool.exe') -File -ErrorAction SilentlyContinue } |
                Sort-Object -Property LastWriteTime -Descending
        )
        if ($candidates.Count -gt 0) {
            return $candidates[0].FullName
        }
    }

    throw ("signtool.exe was not found. Tried:`n  {0}`nRestore the App (Microsoft.Windows.SDK.BuildTools) or put signtool.exe on PATH." -f
        ($tried -join "`n  "))
}

# ---- Preflight: certificate requirement first, so a missing certificate fails before anything else is touched ----

$password = $env:PM_CERT_PASSWORD
if ([string]::IsNullOrEmpty($password)) {
    $password = $CertificatePassword
}

if (-not $Unsigned -and [string]::IsNullOrWhiteSpace($CertificatePath)) {
    throw ("No code-signing certificate was given: pass -CertificatePath <pfx outside the repository> " +
        "(the PFX password comes from PM_CERT_PASSWORD or -CertificatePassword), or run with -Unsigned for an unsigned dry run.")
}

if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
    throw "The package manifest was not found: $ManifestPath"
}
if (-not (Test-Path -LiteralPath $ProjectPath -PathType Leaf)) {
    throw "The App project was not found: $ProjectPath"
}
if (-not (Test-Path -LiteralPath $PublishScriptPath -PathType Leaf)) {
    throw "The publish script was not found: $PublishScriptPath"
}

# ---- Preflight: the version pair (<Version> in the csproj, Identity/@Version in the manifest) ----

$project = [xml](Get-Content -LiteralPath $ProjectPath -Raw)
$versionNode = $project.SelectSingleNode('/Project/PropertyGroup/Version')
if (-not $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
    throw "$ProjectPath has no <Version>; it is the single human-facing version source."
}
$Version = $versionNode.InnerText.Trim()

$manifest = [xml](Get-Content -LiteralPath $ManifestPath -Raw)
# The manifest carries the appx default namespace, so select by local name instead of a hardcoded prefix.
$identity = $manifest.SelectSingleNode('/*[local-name()="Package"]/*[local-name()="Identity"]')
if (-not $identity) {
    throw "$ManifestPath has no Identity element."
}
$Publisher = [string]$identity.Publisher
$manifestVersion = [string]$identity.Version

if ([string]::IsNullOrWhiteSpace($Publisher)) {
    throw "$ManifestPath has no Identity/@Publisher."
}
if ($manifestVersion -ne "$Version.0") {
    throw ("The versions disagree: <Version> is $Version, so the manifest must read Identity/@Version=`"$Version.0`", " +
        "it reads `"$manifestVersion`". Bump both together (PackageIdentityTests guards the pair).")
}

# ---- Preflight: a changelog section for this version ----

if (-not (Test-Path -LiteralPath $ChangelogPath -PathType Leaf)) {
    throw "CHANGELOG.md is missing at the repository root ($ChangelogPath): a release needs a '## [$Version]' section."
}
$changelog = Get-Content -LiteralPath $ChangelogPath -Raw
if ($changelog -notmatch ("(?m)^##\s+\[" + [System.Text.RegularExpressions.Regex]::Escape($Version) + "\]")) {
    throw "CHANGELOG.md has no '## [$Version]' section; the release notes are copied from it (Keep a Changelog shape)."
}

# ---- Preflight: the certificate exists, stays outside the repository and matches the manifest ----

$certificate = $null
$certFullPath = $null
if (-not $Unsigned) {
    $certFullPath = [System.IO.Path]::GetFullPath($CertificatePath)
    if (-not (Test-Path -LiteralPath $certFullPath -PathType Leaf)) {
        throw "The certificate was not found: $certFullPath"
    }

    # A PFX inside the repository is one `git add` away from being committed (AGENTS.md section 9).
    $repoPrefix = $RepoRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if ($certFullPath.StartsWith($repoPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw ("The certificate is inside the repository: $certFullPath. Key material must never be committed " +
            "(AGENTS.md section 9); keep the .pfx outside $RepoRoot and pass its path with -CertificatePath.")
    }

    $keyFlags = [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
    try {
        $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($certFullPath, $password, $keyFlags)
    }
    catch {
        throw ("Could not load the certificate from $certFullPath. Check that it is a PFX and that PM_CERT_PASSWORD " +
            "is correct. $($_.Exception.Message)")
    }

    # Parse the EKU extension directly: PowerShell 7 wraps EnhancedKeyUsageList in its own representation type.
    $ekuExtension = $certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' } | Select-Object -First 1
    $hasCodeSigning = $false
    if ($ekuExtension) {
        $eku = [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($ekuExtension, $ekuExtension.Critical)
        $hasCodeSigning = $null -ne ($eku.EnhancedKeyUsages | Where-Object { $_.Value -eq $CodeSigningEku })
    }
    if (-not $hasCodeSigning) {
        throw ("The certificate is not a code-signing certificate: the required EKU " +
            "($CodeSigningEku, Code Signing) is missing. Subject: $($certificate.Subject)")
    }

    if ($certificate.NotAfter.ToUniversalTime() -le [System.DateTime]::UtcNow) {
        throw ("The certificate expired on {0:u}. Subject: {1}" -f $certificate.NotAfter.ToUniversalTime(), $certificate.Subject)
    }

    if (-not $certificate.HasPrivateKey) {
        throw ("The certificate carries no private key, so it cannot sign. Export the release certificate as a PFX " +
            "with its private key. Subject: $($certificate.Subject)")
    }

    # Verbatim comparison: the manifest Publisher and the certificate Subject must be byte-for-byte equal.
    if (-not [string]::Equals($certificate.Subject, $Publisher, [System.StringComparison]::Ordinal)) {
        throw ("The certificate subject does not match the manifest Identity/@Publisher.`n" +
            "  certificate Subject: $($certificate.Subject)`n" +
            "  manifest Publisher:  $Publisher`n" +
            "The comparison is verbatim. Replace the Identity line in Package.appxmanifest with:`n" +
            "    Publisher=`"$($certificate.Subject)`"")
    }
}

# ---- Publish (child process, so Publish-Release.ps1 keeps owning the recipe) ----

if (-not $SkipPublish) {
    $pwsh = Get-Command pwsh -ErrorAction SilentlyContinue
    $hostExe = if ($pwsh) { $pwsh.Source } else { (Get-Command powershell.exe -ErrorAction Stop).Source }

    $publishArguments = @('-NoProfile', '-File', $PublishScriptPath, '-RuntimeIdentifier', $RuntimeIdentifier)
    if ($Clean) {
        $publishArguments += '-Clean'
    }

    Write-Host "> pwsh $($publishArguments -join ' ')"
    & $hostExe @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Publish-Release.ps1 failed with exit code $LASTEXITCODE."
    }
}
else {
    Write-Host 'Skipping publish (-SkipPublish): signing and hashing the existing package(s).'
}

$tfNode = $project.SelectSingleNode('/Project/PropertyGroup/TargetFramework')
if (-not $tfNode -or [string]::IsNullOrWhiteSpace($tfNode.InnerText)) {
    throw "$ProjectPath has no TargetFramework; the publish artifact path cannot be derived."
}
$TargetFramework = $tfNode.InnerText.Trim()

$selected = if ($RuntimeIdentifier -eq 'both') { $Targets } else { @($Targets | Where-Object { $_.Rid -eq $RuntimeIdentifier }) }

$signToolPath = $null
if (-not $Unsigned) {
    $signToolPath = Resolve-SignTool
}

$results = @()

foreach ($target in $selected) {
    $ridPackageDir = Join-Path $AppPackagesDir $target.Rid
    $candidates = @(
        Get-ChildItem -LiteralPath $ridPackageDir -Filter "*_$($target.MsixArch).msix" -File -Recurse -ErrorAction SilentlyContinue |
            Sort-Object -Property LastWriteTime -Descending
    )
    if ($candidates.Count -eq 0) {
        throw "No .msix matching *_$($target.MsixArch).msix under $ridPackageDir. Run without -SkipPublish to publish it."
    }

    $msix = $candidates[0]
    $msixPath = $msix.FullName

    $publishDir = Join-Path $WindowsDir "src\PasswordManager.App\bin\Release\$TargetFramework\$($target.Rid)\publish"
    $publishBytes = [long]0
    $publishFiles = 0
    if (Test-Path -LiteralPath $publishDir -PathType Container) {
        foreach ($file in Get-ChildItem -LiteralPath $publishDir -File -Recurse) {
            if ($file.Extension -eq '.pdb') { continue }
            $publishBytes += $file.Length
            $publishFiles++
        }
    }

    # ---- Sizes ----
    Write-Host ''
    Write-Host "$($target.Rid):"
    if (Test-Path -LiteralPath $publishDir -PathType Container) {
        Write-Host "  publish: $publishDir ($publishFiles files, $(Format-Size $publishBytes), *.pdb excluded)"
    }
    else {
        Write-Host "  publish: $publishDir (not found)"
    }
    Write-Host "  msix:    $msixPath ($(Format-Size $msix.Length))"

    # ---- Sign (password never printed) ----
    if ($Unsigned) {
        Write-Host '  signing: skipped (-Unsigned)'
    }
    else {
        $signArguments = @('sign', '/fd', 'SHA256', '/a', '/f', $certFullPath, '/p', $password)
        if ($TimestampUrl) {
            $signArguments += @('/tr', $TimestampUrl, '/td', 'SHA256')
            Write-Host "  timestamp: $TimestampUrl (network)"
        }
        $signArguments += $msixPath

        Write-Host "> signtool sign /fd SHA256 /a /f $certFullPath /p *** $msixPath"
        & $signToolPath @signArguments
        if ($LASTEXITCODE -ne 0) {
            throw "signtool sign failed with exit code $LASTEXITCODE for $msixPath."
        }
    }

    # ---- Verify, then hash (never hash a package whose signature was not verified) ----
    $signature = Get-AuthenticodeSignature -LiteralPath $msixPath
    $accepted = $signature.Status -eq 'Valid' -or ($Unsigned -and $signature.Status -eq 'NotSigned')
    if (-not $accepted) {
        throw ("The package signature is $($signature.Status), expected Valid{0}. {1}`n  package: $msixPath" -f
            $(if ($Unsigned) { ' or NotSigned (-Unsigned)' } else { '' }), $signature.StatusMessage)
    }
    Write-Host "  signature: $($signature.Status)"

    $hash = (Get-FileHash -LiteralPath $msixPath -Algorithm SHA256).Hash
    $hashPath = "$msixPath.sha256"
    [System.IO.File]::WriteAllText($hashPath, "$hash *$($msix.Name)`n", [System.Text.UTF8Encoding]::new($false))
    Write-Host "  sha256:  $hash"

    # ---- Artifact paths ----
    Write-Host "  artifacts: $msixPath"
    Write-Host "             $hashPath"

    $results += [pscustomobject]@{
        Rid       = $target.Rid
        MsixPath  = $msixPath
        HashPath  = $hashPath
        Hash      = $hash
        Signature = [string]$signature.Status
        MsixBytes = $msix.Length
        MsixMiB   = [Math]::Round($msix.Length / 1MB, 2)
    }
}

# ---- Summary ----

Write-Host ''
Write-Host ($results | Format-Table -AutoSize -Property Rid, Signature, Hash, MsixBytes, MsixMiB | Out-String).TrimEnd()
Write-Host ("Released {0}: signed={1}, timestamp={2}." -f
    (($results | ForEach-Object { $_.Rid }) -join ', '),
    (-not $Unsigned),
    [bool]$TimestampUrl)
