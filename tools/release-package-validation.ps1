# Shared local package validation. This file defines functions only.

function Assert-ExactProperties {
    param([object]$Value, [string[]]$Expected, [string]$Path)

    if ($null -eq $Value) {
        throw "Bundle object '$Path' is missing."
    }
    $actual = @($Value.PSObject.Properties.Name | Sort-Object)
    $expectedSorted = @($Expected | Sort-Object)
    if (($actual -join "`n") -ne ($expectedSorted -join "`n")) {
        throw "Bundle object '$Path' must contain exactly: $($Expected -join ', ')."
    }
}

function Assert-SemanticVersion {
    param([string]$Value)

    if ($Value -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
        throw "Version '$Value' is not a normal semantic version."
    }
}

function Assert-FlatIdentity {
    param([string]$Value, [string]$Label)

    if ($Value -notmatch '^[a-z0-9]+(?:[._-][a-z0-9]+)*$') {
        throw "$Label '$Value' is not a lowercase flat identity."
    }
}

function Get-LowerSha256 {
    param([string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Read-PackageManifest {
    param([object]$Pin, [string]$PackageRoot)

    $packageName = [string]$Pin.package
    $version = [string]$Pin.version
    $expectedManifestHash = [string]$Pin.manifestSha256
    Assert-FlatIdentity $packageName "Package name"
    Assert-SemanticVersion $version
    if ($expectedManifestHash -notmatch '^[0-9a-f]{64}$') {
        throw "Package pin '$packageName/$version' has an invalid manifest SHA-256."
    }

    $packageDirectory = Join-Path (Join-Path $PackageRoot $packageName) $version
    $packageManifestPath = Join-Path $packageDirectory "package-manifest.json"
    if (-not (Test-Path -LiteralPath $packageManifestPath -PathType Leaf)) {
        throw "Resolved package '$packageName/$version' is missing package-manifest.json."
    }
    if ((Get-LowerSha256 $packageManifestPath) -ne $expectedManifestHash) {
        throw "Resolved package manifest hash mismatch for '$packageName/$version'."
    }

    $manifest = Get-Content -LiteralPath $packageManifestPath -Raw | ConvertFrom-Json
    Assert-ExactProperties $manifest @("schemaVersion", "package", "artifact", "source", "requires", "runtimeRequirements") "package:$packageName"
    Assert-ExactProperties $manifest.package @("name", "version", "kind", "displayName") "package:$packageName/package"
    Assert-ExactProperties $manifest.artifact @("file", "sha256", "size") "package:$packageName/artifact"
    Assert-ExactProperties $manifest.source @("revision") "package:$packageName/source"
    if ($manifest.schemaVersion -ne 1 -or
        $manifest.package.name -ne $packageName -or
        $manifest.package.version -ne $version) {
        throw "Resolved package identity does not match pin '$packageName/$version'."
    }
    if ($manifest.package.kind -notin @("application", "profile", "plugin")) {
        throw "Resolved package '$packageName/$version' has unsupported kind '$($manifest.package.kind)'."
    }
    if ([string]::IsNullOrWhiteSpace([string]$manifest.package.displayName) -or
        $manifest.source.revision -notmatch '^[0-9a-f]{40}$' -or
        $null -eq $manifest.requires -or
        $null -eq $manifest.runtimeRequirements) {
        throw "Resolved package '$packageName/$version' has invalid release metadata."
    }

    $artifactName = [string]$manifest.artifact.file
    $expectedArtifactName = switch ($manifest.package.kind) {
        "application" { "application.zip" }
        "profile" { "profile.zip" }
        "plugin" { "plugin.zip" }
    }
    if ($artifactName -ne $expectedArtifactName) {
        throw "Resolved package '$packageName/$version' requires payload '$expectedArtifactName'."
    }
    $artifactPath = Join-Path $packageDirectory $artifactName
    if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
        throw "Resolved package '$packageName/$version' is missing '$artifactName'."
    }
    $artifact = Get-Item -LiteralPath $artifactPath
    if (($manifest.artifact.size -isnot [long] -and $manifest.artifact.size -isnot [int]) -or
        [long]$manifest.artifact.size -lt 0 -or
        $manifest.artifact.sha256 -notmatch '^[0-9a-f]{64}$' -or
        $artifact.Length -ne [long]$manifest.artifact.size -or
        (Get-LowerSha256 $artifactPath) -ne $manifest.artifact.sha256) {
        throw "Resolved package payload integrity check failed for '$packageName/$version'."
    }

    return [pscustomobject]@{
        Pin = $Pin
        Manifest = $manifest
        ManifestPath = $packageManifestPath
        ArtifactPath = $artifactPath
        Destination = [string]$Pin.destination
    }
}

function Resolve-Destination {
    param([string]$Value, [string]$Label)

    if ([string]::IsNullOrWhiteSpace($Value) -or
        [System.IO.Path]::IsPathRooted($Value) -or
        $Value.Contains('\') -or
        $Value.EndsWith('/') -or
        $Value.Contains('//')) {
        throw "$Label destination '$Value' must be a normalized relative path using forward slashes."
    }

    $segments = @($Value.Split('/'))
    if ($segments.Count -eq 0 -or @($segments | Where-Object { $_ -in @("", ".", "..") }).Count -ne 0) {
        throw "$Label destination '$Value' contains an invalid path segment."
    }
    foreach ($segment in $segments) {
        if ($segment.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0) {
            throw "$Label destination '$Value' contains invalid path characters."
        }
    }
    return $segments -join '/'
}

function Read-ReleaseBundle {
    param([string]$ManifestPath, [string]$PackageRoot)

    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Bundle manifest '$ManifestPath' does not exist."
    }

    $bundle = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    Assert-ExactProperties $bundle @("schemaVersion", "distribution", "application", "components") "root"
    Assert-ExactProperties $bundle.distribution @("name", "version") "distribution"
    Assert-ExactProperties $bundle.application @("package", "version", "manifestSha256", "destination") "application"
    if ($bundle.schemaVersion -ne 1) {
        throw "Unsupported bundle schema version '$($bundle.schemaVersion)'."
    }
    Assert-FlatIdentity ([string]$bundle.distribution.name) "Distribution name"
    Assert-SemanticVersion ([string]$bundle.distribution.version)
    if ([string]$bundle.application.destination -ne ".") {
        throw "The application package must own the distribution root destination '.'."
    }

    $components = @($bundle.components)
    $componentById = @{}
    foreach ($component in $components) {
        $hasParent = $null -ne $component.PSObject.Properties["parent"]
        $expectedProperties = @("id", "package", "version", "manifestSha256", "destination", "selectedByDefault")
        if ($hasParent) { $expectedProperties += "parent" }
        Assert-ExactProperties $component $expectedProperties "component:$($component.id)"
        Assert-FlatIdentity ([string]$component.id) "Component id"
        if ($component.selectedByDefault -isnot [bool]) {
            throw "Component '$($component.id)' selectedByDefault must be Boolean."
        }
        $idKey = ([string]$component.id).ToLowerInvariant()
        if ($componentById.ContainsKey($idKey)) {
            throw "Bundle component ids must be unique."
        }
        $component.destination = Resolve-Destination ([string]$component.destination) "Component '$($component.id)'"
        $componentById[$idKey] = $component
    }

    foreach ($component in $components) {
        $parentProperty = $component.PSObject.Properties["parent"]
        if ($null -eq $parentProperty) { continue }
        $parentId = ([string]$parentProperty.Value).ToLowerInvariant()
        if (-not $componentById.ContainsKey($parentId)) {
            throw "Component '$($component.id)' references missing parent '$($parentProperty.Value)'."
        }
        $parentDestination = [string]$componentById[$parentId].destination
        if (-not ([string]$component.destination).StartsWith($parentDestination + '/', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Component '$($component.id)' destination must be below its parent '$($parentProperty.Value)'."
        }
        if ($component.selectedByDefault -and -not $componentById[$parentId].selectedByDefault) {
            throw "Component '$($component.id)' cannot be selected by default when parent '$($parentProperty.Value)' is not selected by default."
        }
    }

    for ($leftIndex = 0; $leftIndex -lt $components.Count; $leftIndex++) {
        for ($rightIndex = $leftIndex + 1; $rightIndex -lt $components.Count; $rightIndex++) {
            $left = $components[$leftIndex]
            $right = $components[$rightIndex]
            $leftPath = [string]$left.destination
            $rightPath = [string]$right.destination
            $overlap = $leftPath.Equals($rightPath, [StringComparison]::OrdinalIgnoreCase) -or
                $leftPath.StartsWith($rightPath + '/', [StringComparison]::OrdinalIgnoreCase) -or
                $rightPath.StartsWith($leftPath + '/', [StringComparison]::OrdinalIgnoreCase)
            if (-not $overlap) { continue }

            $leftParent = $left.PSObject.Properties["parent"]
            $rightParent = $right.PSObject.Properties["parent"]
            $isDeclaredParent = ($null -ne $leftParent -and [string]$leftParent.Value -eq [string]$right.id) -or
                ($null -ne $rightParent -and [string]$rightParent.Value -eq [string]$left.id)
            if (-not $isDeclaredParent) {
                throw "Component destinations '$leftPath' and '$rightPath' overlap without a direct parent relationship."
            }
        }
    }

    $pins = @($bundle.application) + $components
    $packages = @($pins | ForEach-Object { Read-PackageManifest $_ $PackageRoot })
    $packageNames = @($packages | ForEach-Object { [string]$_.Manifest.package.name })
    if (@($packageNames | Sort-Object -Unique).Count -ne $packageNames.Count) {
        throw "A bundle may contain each package identity only once."
    }
    if ($packages[0].Manifest.package.kind -ne "application") {
        throw "The bundle application pin must resolve to an application package."
    }
    foreach ($package in $packages) {
        foreach ($requirement in @($package.Manifest.requires)) {
            if ($packageNames -notcontains [string]$requirement) {
                throw "Package '$($package.Manifest.package.name)' requires missing package '$requirement'."
            }
        }
    }
    foreach ($package in $packages | Select-Object -Skip 1) {
        if ($package.Manifest.package.kind -notin @("profile", "plugin")) {
            throw "Component package '$($package.Manifest.package.name)' must be a profile or plugin."
        }

        $parentProperty = $package.Pin.PSObject.Properties["parent"]
        if ($package.Manifest.package.kind -eq "plugin" -and $null -eq $parentProperty) {
            throw "Plugin package '$($package.Manifest.package.name)' must declare a parent component."
        }
        if ($null -ne $parentProperty) {
            $parentComponent = $componentById[([string]$parentProperty.Value).ToLowerInvariant()]
            $parentPackage = @($packages | Where-Object { $_.Manifest.package.name -eq [string]$parentComponent.package })
            if ($parentPackage.Count -ne 1 -or $parentPackage[0].Manifest.package.kind -ne "profile") {
                throw "Plugin package '$($package.Manifest.package.name)' parent must resolve to one profile package."
            }
            if (@($package.Manifest.requires) -notcontains [string]$parentComponent.package) {
                throw "Package '$($package.Manifest.package.name)' must require its parent package '$($parentComponent.package)'."
            }
        }
    }

    return [pscustomobject]@{ Bundle = $bundle; Packages = $packages }
}
