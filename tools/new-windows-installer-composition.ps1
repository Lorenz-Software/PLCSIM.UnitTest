[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DistributionRoot,

    [Parameter(Mandatory = $true)]
    [string]$ComponentsFile,

    [Parameter(Mandatory = $true)]
    [string]$FilesFile
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$distributionPath = [System.IO.Path]::GetFullPath($DistributionRoot)
$componentsOutputPath = [System.IO.Path]::GetFullPath($ComponentsFile)
$filesOutputPath = [System.IO.Path]::GetFullPath($FilesFile)
$bundleManifestPath = Join-Path $distributionPath "distribution\bundle-manifest.json"
$utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)

function Assert-Condition {
    param([bool]$Condition, [string]$Message)

    if (-not $Condition) {
        throw $Message
    }
}

function Convert-ToInnoPath {
    param([string]$Value)

    return $Value.Replace('/', '\')
}

function Convert-ToInnoString {
    param([string]$Value)

    Assert-Condition (-not [string]::IsNullOrWhiteSpace($Value)) "Inno Setup text values must not be empty."
    Assert-Condition (-not $Value.Contains("`r") -and -not $Value.Contains("`n")) "Inno Setup text values must not contain line breaks."
    return $Value.Replace('"', '""')
}

function Convert-ToComponentSegment {
    param([string]$Id)

    Assert-Condition ($Id -match '^[a-z0-9]+(?:[._-][a-z0-9]+)*$') "Component id '$Id' is not a lowercase flat identity."
    return "component_" + [regex]::Replace($Id, '[^a-z0-9]', '_')
}

function Get-ParentId {
    param([object]$Component)

    $property = $Component.PSObject.Properties["parent"]
    if ($null -eq $property) {
        return ""
    }
    return [string]$property.Value
}

function Resolve-ComponentName {
    param(
        [object]$Component,
        [hashtable]$ComponentsById,
        [hashtable]$NamesById,
        [System.Collections.Generic.HashSet[string]]$ActiveIds)

    $id = [string]$Component.id
    $key = $id.ToLowerInvariant()
    if ($NamesById.ContainsKey($key)) {
        return [string]$NamesById[$key]
    }
    Assert-Condition ($ActiveIds.Add($key)) "Component parent graph contains a cycle at '$id'."
    try {
        $segment = Convert-ToComponentSegment $id
        $parentId = Get-ParentId $Component
        $name = if ([string]::IsNullOrEmpty($parentId)) {
            $segment
        }
        else {
            $parentKey = $parentId.ToLowerInvariant()
            Assert-Condition ($ComponentsById.ContainsKey($parentKey)) "Component '$id' references missing parent '$parentId'."
            (Resolve-ComponentName $ComponentsById[$parentKey] $ComponentsById $NamesById $ActiveIds) + "\" + $segment
        }
        $NamesById[$key] = $name
        return $name
    }
    finally {
        $null = $ActiveIds.Remove($key)
    }
}

function Get-RelativeChildDestination {
    param([string]$ParentDestination, [string]$ChildDestination)

    $prefix = $ParentDestination.TrimEnd('/') + '/'
    Assert-Condition ($ChildDestination.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) "Child destination '$ChildDestination' is not below '$ParentDestination'."
    return $ChildDestination.Substring($prefix.Length)
}

Assert-Condition (Test-Path -LiteralPath $bundleManifestPath -PathType Leaf) "Distribution bundle manifest '$bundleManifestPath' does not exist."
$bundle = Get-Content -LiteralPath $bundleManifestPath -Raw | ConvertFrom-Json
Assert-Condition ($bundle.schemaVersion -eq 1) "Unsupported bundle schema version '$($bundle.schemaVersion)'."
Assert-Condition ([string]$bundle.application.destination -eq ".") "The application package must own distribution destination '.'."

$components = @($bundle.components)
$componentsById = @{}
$componentSegmentOwners = @{}
$packageOwners = @{}
foreach ($component in $components) {
    $id = [string]$component.id
    $key = $id.ToLowerInvariant()
    Assert-Condition (-not $componentsById.ContainsKey($key)) "Bundle component ids must be unique."
    $segment = Convert-ToComponentSegment $id
    Assert-Condition (-not $componentSegmentOwners.ContainsKey($segment)) "Component ids '$id' and '$($componentSegmentOwners[$segment])' map to the same Inno Setup identifier '$segment'."
    Assert-Condition ($component.selectedByDefault -is [bool]) "Component '$id' selectedByDefault must be Boolean."
    $destination = [string]$component.destination
    $destinationSegments = @($destination.Split('/'))
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($destination) -and
        $destination -eq $destination.Trim() -and
        -not $destination.Contains('\') -and
        -not [System.IO.Path]::IsPathRooted($destination) -and
        $destinationSegments.Count -ne 0 -and
        @($destinationSegments | Where-Object { [string]::IsNullOrWhiteSpace($_) -or $_ -in @('.', '..') -or $_ -match '[\\:*?"<>|]' }).Count -eq 0) "Component '$id' has unsafe or non-normalized destination '$destination'."
    $packageName = [string]$component.package
    Assert-Condition ($packageName -match '^[a-z0-9]+(?:[._-][a-z0-9]+)*$') "Component '$id' has invalid package identity '$packageName'."
    Assert-Condition (-not $packageOwners.ContainsKey($packageName)) "Package '$packageName' is assigned to more than one installer component."
    $componentsById[$key] = $component
    $componentSegmentOwners[$segment] = $id
    $packageOwners[$packageName] = $id
}

$namesById = @{}
$activeIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($component in $components) {
    $null = Resolve-ComponentName $component $componentsById $namesById $activeIds
}
Assert-Condition (@($namesById.Values | Sort-Object -Unique).Count -eq $components.Count) "Generated Inno Setup component names must be unique."

foreach ($component in $components) {
    $parentId = Get-ParentId $component
    if ([string]::IsNullOrEmpty($parentId)) {
        continue
    }
    $parent = $componentsById[$parentId.ToLowerInvariant()]
    $null = Get-RelativeChildDestination ([string]$parent.destination) ([string]$component.destination)
    Assert-Condition (-not $component.selectedByDefault -or $parent.selectedByDefault) "Default-selected component '$($component.id)' has a parent that is not selected by default."
}
for ($leftIndex = 0; $leftIndex -lt $components.Count; $leftIndex++) {
    for ($rightIndex = $leftIndex + 1; $rightIndex -lt $components.Count; $rightIndex++) {
        $left = $components[$leftIndex]
        $right = $components[$rightIndex]
        $leftDestination = ([string]$left.destination).TrimEnd('/')
        $rightDestination = ([string]$right.destination).TrimEnd('/')
        $overlaps = $leftDestination.Equals($rightDestination, [StringComparison]::OrdinalIgnoreCase) -or
            $leftDestination.StartsWith($rightDestination + '/', [StringComparison]::OrdinalIgnoreCase) -or
            $rightDestination.StartsWith($leftDestination + '/', [StringComparison]::OrdinalIgnoreCase)
        if (-not $overlaps) {
            continue
        }
        $directParent = (Get-ParentId $left).Equals([string]$right.id, [StringComparison]::OrdinalIgnoreCase) -or
            (Get-ParentId $right).Equals([string]$left.id, [StringComparison]::OrdinalIgnoreCase)
        Assert-Condition $directParent "Component destinations '$leftDestination' and '$rightDestination' overlap without a direct parent relationship."
    }
}

$packageDataByComponentId = @{}
foreach ($component in $components) {
    $packageName = [string]$component.package
    $inventoryPath = Join-Path $distributionPath "distribution\packages\$packageName\package-manifest.json"
    Assert-Condition (Test-Path -LiteralPath $inventoryPath -PathType Leaf) "Package inventory '$inventoryPath' does not exist."
    $manifest = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
    Assert-Condition ($manifest.schemaVersion -eq 1 -and $manifest.package.name -eq $packageName -and $manifest.package.version -eq [string]$component.version) "Package inventory identity does not match component '$($component.id)'."
    Assert-Condition ($manifest.package.kind -in @("profile", "plugin")) "Component '$($component.id)' package kind '$($manifest.package.kind)' cannot be represented by the installer."
    $packageDataByComponentId[([string]$component.id).ToLowerInvariant()] = $manifest
}

$applicationPackage = [string]$bundle.application.package
$applicationInventoryPath = Join-Path $distributionPath "distribution\packages\$applicationPackage\package-manifest.json"
Assert-Condition (Test-Path -LiteralPath $applicationInventoryPath -PathType Leaf) "Application package inventory '$applicationInventoryPath' does not exist."
$applicationManifest = Get-Content -LiteralPath $applicationInventoryPath -Raw | ConvertFrom-Json
Assert-Condition ($applicationManifest.package.kind -eq "application" -and $applicationManifest.package.name -eq $applicationPackage -and $applicationManifest.package.version -eq [string]$bundle.application.version) "Application package inventory does not match the bundle application pin."

$componentLines = New-Object System.Collections.Generic.List[string]
$applicationDescription = '{0} - {1}' -f @(
    [string]$applicationManifest.package.displayName,
    [string]$applicationManifest.package.version)
$componentLines.Add(('Name: "application"; Description: "{0}"; Types: full custom; Flags: fixed disablenouninstallwarning' -f (Convert-ToInnoString $applicationDescription)))
foreach ($component in $components) {
    $id = [string]$component.id
    $key = $id.ToLowerInvariant()
    $manifest = $packageDataByComponentId[$key]
    $description = '{0} - {1}' -f @(
        [string]$manifest.package.displayName,
        [string]$manifest.package.version)
    $line = 'Name: "{0}"; Description: "{1}"' -f @(
        $namesById[$key],
        (Convert-ToInnoString $description))
    if ($component.selectedByDefault) {
        $line += '; Types: full'
    }
    $flags = @()
    if (@($components | Where-Object { (Get-ParentId $_) -eq $id }).Count -ne 0) {
        $flags += 'checkablealone'
    }
    $flags += 'disablenouninstallwarning'
    $line += '; Flags: ' + ($flags -join ' ')
    $componentLines.Add($line)
}

$rootComponents = @($components | Where-Object { [string]::IsNullOrEmpty((Get-ParentId $_)) })
$applicationExcludes = @("distribution\*") + @($rootComponents | ForEach-Object {
    (Convert-ToInnoPath ([string]$_.destination)).TrimEnd('\') + "\*"
})
$fileLines = New-Object System.Collections.Generic.List[string]
$fileLines.Add(('Source: "{{#SourceRoot}}\*"; DestDir: "{{app}}"; Excludes: "{0}"; Flags: ignoreversion recursesubdirs createallsubdirs' -f ($applicationExcludes -join ',')))

foreach ($component in $components) {
    $id = [string]$component.id
    $key = $id.ToLowerInvariant()
    $destination = [string]$component.destination
    $innoDestination = Convert-ToInnoPath $destination
    $children = @($components | Where-Object { (Get-ParentId $_) -eq $id })
    $excludes = @($children | ForEach-Object {
        (Convert-ToInnoPath (Get-RelativeChildDestination $destination ([string]$_.destination))).TrimEnd('\') + "\*"
    })
    $line = 'Source: "{{#SourceRoot}}\{0}\*"; DestDir: "{{app}}\{0}"' -f $innoDestination
    if ($excludes.Count -ne 0) {
        $line += '; Excludes: "' + ($excludes -join ',') + '"'
    }
    $line += '; Components: ' + $namesById[$key] + '; Flags: ignoreversion recursesubdirs createallsubdirs'
    $fileLines.Add($line)
}

$fileLines.Add('Source: "{#SourceRoot}\distribution\bundle-manifest.json"; DestDir: "{app}\distribution"; Flags: ignoreversion')
$fileLines.Add('Source: "{#SourceRoot}\distribution\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}\distribution"; Flags: ignoreversion')
$fileLines.Add(('Source: "{{#SourceRoot}}\distribution\packages\{0}\package-manifest.json"; DestDir: "{{app}}\distribution\packages\{0}"; Flags: ignoreversion' -f $applicationPackage))
foreach ($component in $components) {
    $key = ([string]$component.id).ToLowerInvariant()
    $packageName = [string]$component.package
    $fileLines.Add(('Source: "{{#SourceRoot}}\distribution\packages\{0}\package-manifest.json"; DestDir: "{{app}}\distribution\packages\{0}"; Components: {1}; Flags: ignoreversion' -f $packageName, $namesById[$key]))
}

foreach ($outputPath in @($componentsOutputPath, $filesOutputPath)) {
    $parent = Split-Path -Parent $outputPath
    if (-not [string]::IsNullOrEmpty($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
}
[System.IO.File]::WriteAllText($componentsOutputPath, ($componentLines -join "`n") + "`n", $utf8WithoutBom)
[System.IO.File]::WriteAllText($filesOutputPath, ($fileLines -join "`n") + "`n", $utf8WithoutBom)

Write-Host "Generated $($componentLines.Count) installer component(s) from '$bundleManifestPath'."
