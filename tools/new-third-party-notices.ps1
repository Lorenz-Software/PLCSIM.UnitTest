[CmdletBinding()]
param(
    [string[]]$AssetFiles = @(),

    [string]$PayloadRoot,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [string]$InventoryPath = "distribution\licenses\third-party-inventory.json",

    [switch]$IncludeInstallerComponents,

    [switch]$IncludeDotNetHost
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$inventoryFile = if ([System.IO.Path]::IsPathRooted($InventoryPath)) {
    [System.IO.Path]::GetFullPath($InventoryPath)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot $InventoryPath))
}
$outputFile = [System.IO.Path]::GetFullPath($OutputPath)
$utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)

function Get-Sha256 {
    param([string]$Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}

if (-not (Test-Path -LiteralPath $inventoryFile -PathType Leaf)) {
    throw "Third-party inventory '$inventoryFile' does not exist."
}

$inventory = Get-Content -LiteralPath $inventoryFile -Raw | ConvertFrom-Json
if ($inventory.schemaVersion -ne 1) {
    throw "Unsupported third-party inventory schema version '$($inventory.schemaVersion)'."
}

$licenseById = @{}
foreach ($license in @($inventory.licenses)) {
    $key = ([string]$license.id).ToLowerInvariant()
    if ($licenseById.ContainsKey($key)) {
        throw "Third-party license ids must be unique."
    }
    if ($license.includeText -isnot [bool] -or [string]::IsNullOrWhiteSpace($license.reason)) {
        throw "License '$($license.id)' requires an explicit text-inclusion decision and reason."
    }
    $textPath = $null
    if ($license.includeText) {
        $textPath = Join-Path (Split-Path -Parent $inventoryFile) ([string]$license.textFile)
    }
    if ($license.includeText -and -not (Test-Path -LiteralPath $textPath -PathType Leaf)) {
        throw "License text '$textPath' does not exist."
    }
    $licenseById[$key] = [pscustomobject]@{
        Definition = $license
        TextPath = $textPath
    }
}

$packageByIdentity = @{}
foreach ($package in @($inventory.packages)) {
    if ($null -eq $package.PSObject.Properties['additionalTextFiles']) {
        throw "Package '$($package.id)' has no explicit additional-text decision."
    }
    $licenseKey = ([string]$package.license).ToLowerInvariant()
    if (-not $licenseById.ContainsKey($licenseKey)) {
        throw "Package '$($package.id)' references unknown license '$($package.license)'."
    }
    foreach ($version in @($package.versions)) {
        $identity = "$(($package.id).ToLowerInvariant())/$version"
        if ($packageByIdentity.ContainsKey($identity)) {
            throw "Third-party package identity '$identity' is classified more than once."
        }
        $packageByIdentity[$identity] = [pscustomobject]@{
            Id = [string]$package.id
            Version = [string]$version
            License = [string]$package.license
            ProjectUrl = [string]$package.projectUrl
            AdditionalTextFiles = @($package.additionalTextFiles)
        }
    }
}

$selectedPackages = @{}
$selectedNotices = @{}
$dependencyBinaryHashes = @{}
$payloadHashes = @{}
if (-not [string]::IsNullOrWhiteSpace($PayloadRoot)) {
    if (-not (Test-Path -LiteralPath $PayloadRoot -PathType Container)) {
        throw "Payload directory '$PayloadRoot' does not exist."
    }
    foreach ($binary in @(Get-ChildItem -LiteralPath $PayloadRoot -Recurse -File -Filter '*.dll' | Where-Object { $_.Name -notlike 'PLCSIM.UnitTest.*' })) {
        $payloadHashes[(Get-Sha256 $binary.FullName)] = $binary.FullName
    }
}
if ($AssetFiles.Count -eq 0 -and -not $IncludeInstallerComponents) {
    throw "At least one project.assets.json file is required for package notices."
}
foreach ($assetFileValue in $AssetFiles) {
    $assetFile = [System.IO.Path]::GetFullPath($assetFileValue)
    if (-not (Test-Path -LiteralPath $assetFile -PathType Leaf)) {
        throw "NuGet assets file '$assetFile' does not exist. Restore the release project before generating notices."
    }
    $assets = Get-Content -LiteralPath $assetFile -Raw | ConvertFrom-Json
    foreach ($library in $assets.libraries.PSObject.Properties) {
        if ([string]$library.Value.type -ne "package") {
            continue
        }
        $identity = $library.Name.ToLowerInvariant()
        if (-not $packageByIdentity.ContainsKey($identity)) {
            throw "Release dependency '$($library.Name)' is not classified in '$inventoryFile'."
        }
        $packageDirectory = $null
        foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
            $candidate = Join-Path $folder ([string]$library.Value.path)
            if (Test-Path -LiteralPath $candidate -PathType Container) {
                $packageDirectory = $candidate
                break
            }
        }
        if ($null -eq $packageDirectory) { throw "Restored package '$identity' cannot be found." }
        $shipped = [string]::IsNullOrWhiteSpace($PayloadRoot)
        if (-not $shipped) {
            foreach ($file in @($library.Value.files | Where-Object { [IO.Path]::GetExtension($_) -eq '.dll' })) {
                $binary = Join-Path $packageDirectory $file
                $binaryHash = Get-Sha256 $binary
                $dependencyBinaryHashes[$binaryHash] = $identity
                if ($payloadHashes.ContainsKey($binaryHash)) { $shipped = $true }
            }
        }
        if (-not $shipped) { continue }
        $selectedPackages[$identity] = $packageByIdentity[$identity]
        $nuspecPath = Join-Path $packageDirectory "$($packageByIdentity[$identity].Id).nuspec"
        [xml]$nuspec = [IO.File]::ReadAllText($nuspecPath)
        $metadata = $nuspec.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
        $copyright = $metadata.SelectSingleNode("*[local-name()='copyright']")
        $selectedPackages[$identity] | Add-Member -NotePropertyName Copyright -NotePropertyValue $(if ($copyright) { $copyright.InnerText } else { '' }) -Force
        foreach ($relativePath in $packageByIdentity[$identity].AdditionalTextFiles) {
            $canonical = Join-Path (Split-Path -Parent $inventoryFile) $relativePath
            if (-not (Test-Path -LiteralPath $canonical -PathType Leaf)) {
                throw "Required additional text for '$identity' is missing: $canonical."
            }
            $hash = Get-Sha256 $canonical
            $selectedNotices[$hash] = $canonical
        }
    }
}

$selectedInstallerComponents = if ($IncludeInstallerComponents) {
    @($inventory.installerComponents)
}
else {
    @()
}
if ($IncludeDotNetHost) {
    $selectedInstallerComponents = @($selectedInstallerComponents) + @($inventory.embeddedComponents)
}

if (-not [string]::IsNullOrWhiteSpace($PayloadRoot)) {
    foreach ($hash in $payloadHashes.Keys) {
        if (-not $dependencyBinaryHashes.ContainsKey($hash)) {
            throw "Payload binary '$($payloadHashes[$hash])' does not match a reviewed NuGet dependency."
        }
    }
}

$usedLicenseIds = @(
    @($selectedPackages.Values | ForEach-Object { $_.License }) +
    @($selectedInstallerComponents | ForEach-Object { [string]$_.license }) |
        Sort-Object -Unique
)
foreach ($licenseId in $usedLicenseIds) {
    if (-not $licenseById.ContainsKey($licenseId.ToLowerInvariant())) {
        throw "Installer component references unknown license '$licenseId'."
    }
}

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("PLCSIM.UnitTest Third-Party Notices")
$lines.Add("====================================")
$lines.Add("")
$lines.Add("This distribution contains third-party software listed below. Vendor APIs and")
$lines.Add("system runtimes that PLCSIM.UnitTest resolves from separately installed products")
$lines.Add("are prerequisites. License texts and additional notices below are selected")
$lines.Add("according to the reviewed obligations of the included components.")
$lines.Add("")
$lines.Add("Components")
$lines.Add("----------")
foreach ($package in @($selectedPackages.Values | Sort-Object Id, Version)) {
    $license = $licenseById[$package.License.ToLowerInvariant()].Definition
    $lines.Add("$($package.Id) $($package.Version)")
    $lines.Add("  License: $($license.displayName)")
    $lines.Add("  Project: $($package.ProjectUrl)")
    if (-not [string]::IsNullOrWhiteSpace($package.Copyright)) {
        $lines.Add("  $($package.Copyright)")
    }
}
foreach ($component in @($selectedInstallerComponents | Sort-Object id)) {
    $license = $licenseById[([string]$component.license).ToLowerInvariant()].Definition
    $lines.Add([string]$component.id)
    $lines.Add("  License: $($license.displayName)")
    $lines.Add("  Project: $($component.projectUrl)")
}

foreach ($licenseId in $usedLicenseIds) {
    $licenseEntry = $licenseById[$licenseId.ToLowerInvariant()]
    $license = $licenseEntry.Definition
    if (-not $license.includeText) { continue }
    $lines.Add("")
    $lines.Add("$($license.displayName)")
    $lines.Add(("-" * ([string]$license.displayName).Length))
    $lines.Add([string]$license.copyright)
    $lines.Add("")
    $licenseText = [IO.File]::ReadAllText($licenseEntry.TextPath).Trim()
    foreach ($line in ($licenseText -split "\r?\n")) {
        $lines.Add($line)
    }
}

$parent = Split-Path -Parent $outputFile
foreach ($hash in @($selectedNotices.Keys | Sort-Object)) {
    $lines.Add("")
    $lines.Add("Upstream notices: $([IO.Path]::GetFileName($selectedNotices[$hash]))")
    $lines.Add([IO.File]::ReadAllText($selectedNotices[$hash]).Trim().Replace("`r`n", "`n"))
}
if (-not [string]::IsNullOrWhiteSpace($parent)) {
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
}
[System.IO.File]::WriteAllText($outputFile, ($lines -join "`n") + "`n", $utf8WithoutBom)
Write-Host "Third-party notices created at '$outputFile'."
