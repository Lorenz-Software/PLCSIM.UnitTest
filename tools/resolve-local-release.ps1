<#
.SYNOPSIS
Resolves one pinned package or a complete pinned bundle from local package files.
.DESCRIPTION
Reads <LocalPackageRoot>/<package-name>/<version> without network access.
Validates exact manifests, identities, payload sizes and hashes before publishing
a new destination. Existing destinations are never overwritten. Bundle output
contains bundle-manifest.json and packages/<package-name>/<version>/.
#>
[CmdletBinding(DefaultParameterSetName = 'Bundle')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Bundle')]
    [string]$BundleManifest,

    [Parameter(Mandatory, ParameterSetName = 'Package')]
    [string]$PackageName,

    [Parameter(Mandatory, ParameterSetName = 'Package')]
    [string]$Version,

    [Parameter(Mandatory, ParameterSetName = 'Package')]
    [string]$ManifestSha256,

    [Parameter(Mandatory)]
    [string]$LocalPackageRoot,

    [Parameter(Mandatory)]
    [string]$DestinationRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'release-package-validation.ps1')

function Assert-NoLinks {
    param([string]$Path)
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Links/reparse points are not supported: $current"
            }
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}

$packageRoot = [IO.Path]::GetFullPath($LocalPackageRoot)
$destination = [IO.Path]::GetFullPath($DestinationRoot)
$separator = [IO.Path]::DirectorySeparatorChar
if ($destination.TrimEnd($separator) -eq [IO.Path]::GetPathRoot($destination).TrimEnd($separator)) {
    throw 'A filesystem root cannot be the destination.'
}
if ($destination.Equals($packageRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $destination.StartsWith($packageRoot.TrimEnd($separator) + $separator, [StringComparison]::OrdinalIgnoreCase) -or
    $packageRoot.StartsWith($destination.TrimEnd($separator) + $separator, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Package source and destination must not overlap.'
}
Assert-NoLinks $packageRoot
Assert-NoLinks $destination
if (Test-Path -LiteralPath $destination) { throw "Destination already exists: $destination" }

if ($PSCmdlet.ParameterSetName -eq 'Bundle') {
    $manifestPath = [IO.Path]::GetFullPath($BundleManifest)
    Assert-NoLinks $manifestPath
    $manifestHash = Get-LowerSha256 $manifestPath
    $validated = Read-ReleaseBundle $manifestPath $packageRoot
    $packages = @($validated.Packages)
}
else {
    $pin = [pscustomobject]@{
        package = $PackageName; version = $Version
        manifestSha256 = $ManifestSha256; destination = '.'
    }
    $packages = @(Read-PackageManifest $pin $packageRoot)
}

foreach ($package in $packages) {
    Assert-NoLinks $package.ManifestPath
    Assert-NoLinks $package.ArtifactPath
}

$parent = [IO.Path]::GetDirectoryName($destination)
[IO.Directory]::CreateDirectory($parent) | Out-Null
$stage = Join-Path $parent ('.local-release-' + [Guid]::NewGuid().ToString('N'))
try {
    [IO.Directory]::CreateDirectory($stage) | Out-Null
    foreach ($package in $packages) {
        $relative = Join-Path $package.Manifest.package.name $package.Manifest.package.version
        if ($PSCmdlet.ParameterSetName -eq 'Bundle') { $relative = Join-Path 'packages' $relative }
        $target = Join-Path $stage $relative
        [IO.Directory]::CreateDirectory($target) | Out-Null
        [IO.File]::Copy($package.ManifestPath, (Join-Path $target 'package-manifest.json'))
        [IO.File]::Copy($package.ArtifactPath, (Join-Path $target $package.Manifest.artifact.file))
    }

    # Revalidate copied bytes rather than relying on the input remaining unchanged.
    if ($PSCmdlet.ParameterSetName -eq 'Bundle') {
        $copiedManifest = Join-Path $stage 'bundle-manifest.json'
        [IO.File]::Copy($manifestPath, $copiedManifest)
        if ((Get-LowerSha256 $copiedManifest) -ne $manifestHash) { throw 'Bundle manifest changed during resolution.' }
        $null = Read-ReleaseBundle $copiedManifest (Join-Path $stage 'packages')
    }
    else {
        $null = Read-PackageManifest $pin $stage
    }
    Assert-NoLinks $destination
    [IO.Directory]::Move($stage, $destination)
    Write-Host "Resolved $($packages.Count) local package(s) to '$destination'."
}
finally {
    if (Test-Path -LiteralPath $stage) {
        $resolvedStage = [IO.Path]::GetFullPath($stage)
        if ([IO.Path]::GetDirectoryName($resolvedStage) -ne $parent -or
            [IO.Path]::GetFileName($resolvedStage) -notmatch '^\.local-release-[a-f0-9]{32}$') {
            throw 'Refusing cleanup outside the owned staging directory.'
        }
        Assert-NoLinks $resolvedStage
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}
