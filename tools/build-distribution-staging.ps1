[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ResolvedBundleRoot,

    [string]$OutputRoot = "artifacts\distribution-staging"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$bundleRoot = [System.IO.Path]::GetFullPath($ResolvedBundleRoot)
$fixedFileTimestampUtc = [DateTime]::new(1980, 1, 1, 0, 0, 0, [DateTimeKind]::Utc)
$outputRootPath = if ([System.IO.Path]::IsPathRooted($OutputRoot)) {
    [System.IO.Path]::GetFullPath($OutputRoot)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputRoot))
}
$manifestPath = Join-Path $bundleRoot "bundle-manifest.json"
. (Join-Path $PSScriptRoot "release-package-validation.ps1")
$noticeGeneratorPath = Join-Path $repoRoot "tools/new-bundle-third-party-notices.ps1"

function Test-IsBelowPath {
    param([string]$Path, [string]$Parent)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullParent = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    return $fullPath.StartsWith($fullParent, [StringComparison]::OrdinalIgnoreCase)
}

function Remove-VerifiedDirectory {
    param([string]$Path, [string]$RequiredParent)

    if (-not (Test-IsBelowPath $Path $RequiredParent)) {
        throw "Refusing to remove '$Path' because it is outside '$RequiredParent'."
    }
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function Assert-AllowedPayloadFile {
    param([string]$RelativePath)

    $normalized = $RelativePath.Replace('\', '/')
    $segments = @($normalized.Split('/'))
    $forbiddenSegments = @("bin", "obj", "tests", "testresults", ".tmp", "logs")
    foreach ($segment in $segments) {
        if ($forbiddenSegments -contains $segment.ToLowerInvariant()) {
            throw "Distribution payload contains forbidden path '$normalized'."
        }
    }

    $fileName = [System.IO.Path]::GetFileName($normalized)
    $forbiddenNames = @(
        "Siemens.Engineering.dll",
        "Siemens.Simatic.Simulation.Runtime.Api.x64.dll",
        "Siemens.Simatic.Simulation.Runtime.Api.x86.dll"
    )
    if ($forbiddenNames -contains $fileName) {
        throw "Distribution payload must not contain vendor artifact '$normalized'."
    }
    if ($fileName -match '(?i)(\.Tests?\.|\.Fake\.|ValidationWorker|PerformanceMeasurementWorker)' -or
        [System.IO.Path]::GetExtension($fileName).ToLowerInvariant() -in @(".pdb", ".log", ".trx", ".cache", ".user")) {
        throw "Distribution payload contains non-production artifact '$normalized'."
    }
}

function Expand-ValidatedPackage {
    param([object]$Package, [string]$StagingRoot, [hashtable]$OwnedFiles)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Package.ArtifactPath)
    try {
        $archiveEntries = @{}
        foreach ($entry in $archive.Entries) {
            $entryName = $entry.FullName.Replace('\', '/')
            if ([string]::IsNullOrWhiteSpace($entryName)) {
                throw "Package '$($Package.Manifest.package.name)' contains an empty ZIP entry."
            }
            if ($entryName.StartsWith('/') -or
                $entryName.Contains('//') -or
                $entryName.Contains(':') -or
                @($entryName.Split('/') | Where-Object { $_ -in @("", ".", "..") }).Count -ne 0) {
                throw "Package '$($Package.Manifest.package.name)' contains unsafe ZIP entry '$entryName'."
            }

            $entryKey = $entryName.ToLowerInvariant()
            if ($archiveEntries.ContainsKey($entryKey)) {
                throw "Package '$($Package.Manifest.package.name)' contains duplicate ZIP entry '$entryName'."
            }
            $archiveEntries[$entryKey] = $true

            $unixType = (($entry.ExternalAttributes -shr 16) -band 0xF000)
            if ($unixType -eq 0xA000) {
                throw "Package '$($Package.Manifest.package.name)' contains symbolic link '$entryName'."
            }
            if ([string]::IsNullOrEmpty($entry.Name)) {
                continue
            }

            foreach ($segment in $entryName.Split('/')) {
                if ($segment.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0) {
                    throw "Package '$($Package.Manifest.package.name)' contains invalid ZIP entry '$entryName'."
                }
            }

            Assert-AllowedPayloadFile $entryName
            $relativeTarget = if ($Package.Destination -eq ".") { $entryName } else { "$($Package.Destination)/$entryName" }
            if ($relativeTarget.Equals("distribution", [StringComparison]::OrdinalIgnoreCase) -or
                $relativeTarget.StartsWith("distribution/", [StringComparison]::OrdinalIgnoreCase)) {
                throw "Package '$($Package.Manifest.package.name)' uses reserved distribution inventory path '$relativeTarget'."
            }
            $targetKey = $relativeTarget.ToLowerInvariant()
            if ($OwnedFiles.ContainsKey($targetKey)) {
                throw "Package file collision at '$relativeTarget' between '$($OwnedFiles[$targetKey])' and '$($Package.Manifest.package.name)'."
            }

            $targetPath = Join-Path $StagingRoot $relativeTarget.Replace('/', '\')
            if (-not (Test-IsBelowPath $targetPath $StagingRoot)) {
                throw "Package entry '$entryName' resolves outside the distribution staging root."
            }
            New-Item -ItemType Directory -Path (Split-Path -Parent $targetPath) -Force | Out-Null

            $source = $entry.Open()
            try {
                $target = [System.IO.File]::Open($targetPath, [System.IO.FileMode]::CreateNew)
                try { $source.CopyTo($target) } finally { $target.Dispose() }
            }
            finally {
                $source.Dispose()
            }
            [System.IO.File]::SetLastWriteTimeUtc($targetPath, $fixedFileTimestampUtc)
            $OwnedFiles[$targetKey] = [string]$Package.Manifest.package.name
        }
    }
    finally {
        $archive.Dispose()
    }
}

$validatedBundle = Read-ReleaseBundle $manifestPath (Join-Path $bundleRoot "packages")
$bundle = $validatedBundle.Bundle
$packages = @($validatedBundle.Packages)

New-Item -ItemType Directory -Path $outputRootPath -Force | Out-Null
$distributionRoot = Join-Path (Join-Path $outputRootPath ([string]$bundle.distribution.name)) ([string]$bundle.distribution.version)
if (Test-Path -LiteralPath $distributionRoot) {
    throw "Distribution staging output '$distributionRoot' already exists."
}
$stagingRoot = Join-Path $outputRootPath (".staging-{0}-{1}" -f $PID, [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null

try {
    $ownedFiles = @{}
    foreach ($package in $packages) {
        Expand-ValidatedPackage $package $stagingRoot $ownedFiles
    }

    foreach ($package in $packages | Where-Object { $_.Manifest.package.kind -ne "application" }) {
        $runtimeManifestPath = Join-Path (Join-Path $stagingRoot $package.Destination.Replace('/', '\')) "manifest.json"
        if (-not (Test-Path -LiteralPath $runtimeManifestPath -PathType Leaf)) {
            throw "Package '$($package.Manifest.package.name)' does not contain its runtime manifest at the package root."
        }
        $runtimeManifest = Get-Content -LiteralPath $runtimeManifestPath -Raw | ConvertFrom-Json
        $runtimeVersion = if ($package.Manifest.package.kind -eq "profile") {
            [string]$runtimeManifest.profile.version
        }
        else {
            [string]$runtimeManifest.info.bindingVersion
        }
        if ($runtimeVersion -ne [string]$package.Manifest.package.version) {
            throw "Package '$($package.Manifest.package.name)' runtime version '$runtimeVersion' does not match release version '$($package.Manifest.package.version)'."
        }
    }

    $bundleInventoryRelativePath = "distribution/bundle-manifest.json"
    if ($ownedFiles.ContainsKey($bundleInventoryRelativePath)) {
        throw "Package '$($ownedFiles[$bundleInventoryRelativePath])' collides with reserved distribution inventory '$bundleInventoryRelativePath'."
    }
    $bundleInventoryPath = Join-Path $stagingRoot $bundleInventoryRelativePath.Replace('/', '\')
    New-Item -ItemType Directory -Path (Split-Path -Parent $bundleInventoryPath) -Force | Out-Null
    Copy-Item -LiteralPath $manifestPath -Destination $bundleInventoryPath
    [System.IO.File]::SetLastWriteTimeUtc($bundleInventoryPath, $fixedFileTimestampUtc)
    $ownedFiles[$bundleInventoryRelativePath] = [string]$bundle.distribution.name

    foreach ($package in $packages) {
        $packageInventoryRelativePath = "distribution/packages/$($package.Manifest.package.name)/package-manifest.json"
        if ($ownedFiles.ContainsKey($packageInventoryRelativePath)) {
            throw "Package '$($ownedFiles[$packageInventoryRelativePath])' collides with reserved package inventory '$packageInventoryRelativePath'."
        }
        $packageInventoryPath = Join-Path $stagingRoot $packageInventoryRelativePath.Replace('/', '\')
        New-Item -ItemType Directory -Path (Split-Path -Parent $packageInventoryPath) -Force | Out-Null
        Copy-Item -LiteralPath $package.ManifestPath -Destination $packageInventoryPath
        [System.IO.File]::SetLastWriteTimeUtc($packageInventoryPath, $fixedFileTimestampUtc)
        $ownedFiles[$packageInventoryRelativePath] = [string]$package.Manifest.package.name
    }

    $aggregateNoticeRelativePath = "distribution/THIRD-PARTY-NOTICES.txt"
    $aggregateNoticePath = Join-Path $stagingRoot $aggregateNoticeRelativePath.Replace('/', '\')
    & $noticeGeneratorPath `
        -DistributionRoot $stagingRoot `
        -OutputPath $aggregateNoticePath
    [System.IO.File]::SetLastWriteTimeUtc($aggregateNoticePath, $fixedFileTimestampUtc)
    $ownedFiles[$aggregateNoticeRelativePath] = [string]$bundle.distribution.name

    $stagingPrefix = $stagingRoot.TrimEnd('\') + '\'
    $finalFiles = @(Get-ChildItem -LiteralPath $stagingRoot -Recurse -File)
    foreach ($file in $finalFiles) {
        $relativePath = $file.FullName.Substring($stagingPrefix.Length).Replace('\', '/')
        Assert-AllowedPayloadFile $relativePath
        if (-not $ownedFiles.ContainsKey($relativePath.ToLowerInvariant())) {
            throw "Distribution staging contains unowned file '$relativePath'."
        }
    }
    if ($finalFiles.Count -ne $ownedFiles.Count) {
        throw "Distribution staging allowlist does not match the final file tree."
    }

    New-Item -ItemType Directory -Path (Split-Path -Parent $distributionRoot) -Force | Out-Null
    [System.IO.Directory]::Move($stagingRoot, $distributionRoot)
    Write-Host "Distribution staging created at '$distributionRoot'."
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-VerifiedDirectory $stagingRoot $outputRootPath
    }
}
