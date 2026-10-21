param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [ValidateSet("Real", "Fake")]
    [string]$Composition = "Real",

    [string[]]$TiaOpennessVersions,

    [string[]]$PlcsimAdvancedVersions
)

$ErrorActionPreference = "Stop"

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$profileDirectoryName = if ($Composition -eq "Fake") { "profiles-fake" } else { "profiles" }
$appTargets = @(
    @{
        Project = Join-Path $repoRoot "src\PLCSIM.UnitTest.App.Wpf\PLCSIM.UnitTest.App.Wpf.csproj"
        Output = Join-Path $repoRoot "src\PLCSIM.UnitTest.App.Wpf\bin\$Configuration\net8.0-windows"
    },
    @{
        Project = Join-Path $repoRoot "src\PLCSIM.UnitTest.App.Cli\PLCSIM.UnitTest.App.Cli.csproj"
        Output = Join-Path $repoRoot "src\PLCSIM.UnitTest.App.Cli\bin\$Configuration\net8.0"
    }
)
$contractArtifactsDirectory = Join-Path $repoRoot "lib\contracts"
$contractArtifactNames = @(
    "PLCSIM.UnitTest.Common.dll",
    "PLCSIM.UnitTest.Common.Application.dll",
    "PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts.dll"
)
$tiaOpennessBindings = [ordered]@{
    "v15_1" = "access-profiles\siemens\tia-openness\v15_1\src\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V15_1\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V15_1.csproj"
    "v17" = "access-profiles\siemens\tia-openness\v17\src\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V17\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V17.csproj"
    "v18" = "access-profiles\siemens\tia-openness\v18\src\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V18\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V18.csproj"
    "v19" = "access-profiles\siemens\tia-openness\v19\src\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V19\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V19.csproj"
    "v20" = "access-profiles\siemens\tia-openness\v20\src\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V20\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V20.csproj"
    "v21" = "access-profiles\siemens\tia-openness\v21\src\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V21\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V21.csproj"
}
$plcsimAdvancedBindings = [ordered]@{
    "v6" = "access-profiles\siemens\plcsim-advanced\v6\src\PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V6\PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V6.csproj"
    "v7" = "access-profiles\siemens\plcsim-advanced\v7\src\PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V7\PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V7.csproj"
    "v8" = "access-profiles\siemens\plcsim-advanced\v8\src\PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8\PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.csproj"
}

function Resolve-VersionSelection {
    param(
        [string]$ParameterName,
        [string[]]$RequestedVersions,
        [bool]$WasSpecified,
        [System.Collections.IDictionary]$AvailableBindings
    )

    if (-not $WasSpecified) {
        return @($AvailableBindings.Keys)
    }

    if ($null -eq $RequestedVersions -or $RequestedVersions.Count -eq 0) {
        throw "$ParameterName must select at least one version."
    }

    $selection = @()
    foreach ($requestedVersion in $RequestedVersions) {
        $normalizedVersion = if ($null -eq $requestedVersion) { "" } else { $requestedVersion.Trim().ToLowerInvariant() }
        if (-not $AvailableBindings.Contains($normalizedVersion)) {
            $availableVersions = $AvailableBindings.Keys -join ", "
            throw "Unknown $ParameterName value '$requestedVersion'. Available versions: $availableVersions."
        }

        if ($selection -notcontains $normalizedVersion) {
            $selection += $normalizedVersion
        }
    }

    return $selection
}

$tiaVersionFilterSpecified = $PSBoundParameters.ContainsKey("TiaOpennessVersions")
$plcsimVersionFilterSpecified = $PSBoundParameters.ContainsKey("PlcsimAdvancedVersions")
if ($Composition -eq "Fake" -and ($tiaVersionFilterSpecified -or $plcsimVersionFilterSpecified)) {
    throw "TIA Openness and PLCSIM Advanced version filters are only valid for a Real composition."
}

$selectedTiaOpennessVersions = @()
$selectedPlcsimAdvancedVersions = @()
if ($Composition -eq "Real") {
    $selectedTiaOpennessVersions = @(Resolve-VersionSelection `
        "TiaOpennessVersions" `
        $TiaOpennessVersions `
        $tiaVersionFilterSpecified `
        $tiaOpennessBindings)
    $selectedPlcsimAdvancedVersions = @(Resolve-VersionSelection `
        "PlcsimAdvancedVersions" `
        $PlcsimAdvancedVersions `
        $plcsimVersionFilterSpecified `
        $plcsimAdvancedBindings)

    Write-Host "Effective real plugin composition: TIA Openness [$($selectedTiaOpennessVersions -join ', ')]; PLCSIM Advanced [$($selectedPlcsimAdvancedVersions -join ', ')]."
}

function Invoke-DotNet {
    param([string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed with exit code $LASTEXITCODE."
    }
}

function Deploy-PluginPackage {
    param(
        [string]$Project,
        [string]$PackageSubDirectory,
        [string]$ProfileRoot
    )

    $packagePath = [System.IO.Path]::GetFullPath((Join-Path $ProfileRoot $PackageSubDirectory))
    $profileRootPrefix = $ProfileRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $packagePath.StartsWith($profileRootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Plugin package path '$packagePath' is outside '$ProfileRoot'."
    }

    if (Test-Path -LiteralPath $packagePath) {
        Remove-Item -LiteralPath $packagePath -Recurse -Force
    }

    Invoke-DotNet @(
        "msbuild",
        (Join-Path $repoRoot $Project),
        "/t:DeployPluginPackage",
        "/p:Configuration=$Configuration",
        "/p:PluginDeploymentRoot=$ProfileRoot",
        "/p:PluginPackageSubDir=$PackageSubDirectory"
    )
}

function Assert-DeployedContractArtifacts {
    param([string]$ProfileRoot)

    $deployedContracts = Get-ChildItem -LiteralPath $ProfileRoot -Recurse -File |
        Where-Object { $contractArtifactNames -contains $_.Name }

    foreach ($contractArtifactName in $contractArtifactNames) {
        if (-not ($deployedContracts | Where-Object { $_.Name -eq $contractArtifactName })) {
            throw "Deployed plugin composition does not contain required contract artifact '$contractArtifactName'."
        }
    }

    foreach ($deployedContract in $deployedContracts) {
        $canonicalContract = Join-Path $contractArtifactsDirectory $deployedContract.Name
        if (-not (Test-Path -LiteralPath $canonicalContract)) {
            throw "Canonical contract artifact '$canonicalContract' does not exist."
        }

        $canonicalHash = (Get-FileHash -LiteralPath $canonicalContract -Algorithm SHA256).Hash
        $deployedHash = (Get-FileHash -LiteralPath $deployedContract.FullName -Algorithm SHA256).Hash
        if ($canonicalHash -ne $deployedHash) {
            throw "Deployed contract artifact '$($deployedContract.FullName)' differs from '$canonicalContract'."
        }
    }
}

& (Join-Path $repoRoot "tools/contract-artifacts.ps1") -Mode Sync
if ($LASTEXITCODE -ne 0) {
    throw "Contract artifact synchronization failed with exit code $LASTEXITCODE."
}

foreach ($appTarget in $appTargets) {
    Invoke-DotNet @("build", $appTarget.Project, "--configuration", $Configuration)
}

foreach ($appTarget in $appTargets) {
    $profileRoot = [System.IO.Path]::GetFullPath((Join-Path $appTarget.Output $profileDirectoryName))
    if (Test-Path -LiteralPath $profileRoot) {
        Remove-Item -LiteralPath $profileRoot -Recurse -Force
    }

    New-Item -ItemType Directory -Path $profileRoot -Force | Out-Null

    Deploy-PluginPackage "access-profiles\siemens\src\PLCSIM.UnitTest.AccessProfiles.Siemens\PLCSIM.UnitTest.AccessProfiles.Siemens.csproj" "siemens" $profileRoot
    if ($Composition -eq "Fake") {
        Deploy-PluginPackage "access-profiles\fake\src\PLCSIM.UnitTest.AccessProfiles.Fake\PLCSIM.UnitTest.AccessProfiles.Fake.csproj" "fake" $profileRoot
        Deploy-PluginPackage "access-profiles\siemens\tia-openness\fake\src\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.Fake\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.Fake.csproj" "siemens\lib\bindings\tia-openness-fake" $profileRoot
        Deploy-PluginPackage "access-profiles\siemens\plcsim-advanced\fake\src\PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.Fake\PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.Fake.csproj" "siemens\lib\bindings\plcsim-advanced-fake" $profileRoot
    }
    else {
        foreach ($version in $selectedTiaOpennessVersions) {
            Deploy-PluginPackage $tiaOpennessBindings[$version] "siemens\lib\bindings\tia-openness-$version" $profileRoot
        }
        foreach ($version in $selectedPlcsimAdvancedVersions) {
            Deploy-PluginPackage $plcsimAdvancedBindings[$version] "siemens\lib\bindings\plcsim-advanced-$version" $profileRoot
        }
    }

    Assert-DeployedContractArtifacts $profileRoot
    Write-Host "$Composition plugin packages deployed to '$profileRoot'."
}
