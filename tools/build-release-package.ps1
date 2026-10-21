[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet(
        "Application",
        "SiemensProfile",
        "BeckhoffTc34026Profile",
        "TiaOpennessV15_1",
        "TiaOpennessV17",
        "TiaOpennessV18",
        "TiaOpennessV19",
        "TiaOpennessV20",
        "TiaOpennessV21",
        "PlcsimAdvancedV6",
        "PlcsimAdvancedV7",
        "PlcsimAdvancedV8")]
    [string]$Package,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$OutputRoot = "artifacts\release-packages",

    [string]$SourceRevision,

    [switch]$AllowDirtySource
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$outputRootPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputRoot))
$nugetSource = "https://api.nuget.org/v3/index.json"
$licensePath = Join-Path $repoRoot "LICENSE"
$noticeGeneratorPath = Join-Path $repoRoot "tools/new-third-party-notices.ps1"
$utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
$fixedZipTimestamp = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)

if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
    throw "Version '$Version' is not a normal semantic version."
}

function Invoke-CheckedCommand {
    param(
        [string]$Command,
        [string[]]$Arguments
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command failed with exit code $LASTEXITCODE."
    }
}

function Resolve-SourceRevision {
    if ([string]::IsNullOrWhiteSpace($SourceRevision)) {
        $resolvedRevision = (& git -C $repoRoot rev-parse HEAD).Trim()
        if ($LASTEXITCODE -ne 0) {
            throw "Could not resolve the source revision."
        }
    }
    else {
        $resolvedRevision = $SourceRevision.Trim().ToLowerInvariant()
    }

    if ($resolvedRevision -notmatch '^[0-9a-f]{40}$') {
        throw "SourceRevision must be a full 40-character Git commit SHA."
    }

    return $resolvedRevision
}

function Assert-CleanSource {
    if ($AllowDirtySource) {
        return
    }

    $dirtyEntries = @(& git -C $repoRoot status --porcelain --untracked-files=all)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not inspect the source working tree."
    }

    if ($dirtyEntries.Count -ne 0) {
        throw "Release packages require a clean source tree. Use -AllowDirtySource only for local packaging verification."
    }
}

function Remove-VerifiedDirectory {
    param(
        [string]$Path,
        [string]$RequiredParent
    )

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $resolvedParent = [System.IO.Path]::GetFullPath($RequiredParent).TrimEnd('\') + '\'
    if (-not $resolvedPath.StartsWith($resolvedParent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove '$resolvedPath' because it is outside '$resolvedParent'."
    }

    if (Test-Path -LiteralPath $resolvedPath) {
        Remove-Item -LiteralPath $resolvedPath -Recurse -Force
    }
}

function Test-IsReleaseFile {
    param([System.IO.FileInfo]$File)

    $excludedExtensions = @(".pdb", ".log", ".trx", ".cache", ".user")
    return $excludedExtensions -notcontains $File.Extension.ToLowerInvariant()
}

function Assert-AllowedReleaseFile {
    param(
        [string]$RelativePath,
        [string]$PackageKind
    )

    $normalized = $RelativePath.Replace('\', '/')
    $segments = @($normalized.Split('/'))
    $forbiddenSegments = @("bin", "obj", "tests", "testresults", ".tmp", "logs")
    foreach ($segment in $segments) {
        if ($forbiddenSegments -contains $segment.ToLowerInvariant()) {
            throw "Release payload contains forbidden path '$normalized'."
        }
    }

    $fileName = [System.IO.Path]::GetFileName($normalized)
    $forbiddenNames = @(
        "Siemens.Engineering.dll",
        "Microsoft.VisualStudio.Interop.dll",
        "TCatSysManagerLib.dll",
        "Siemens.Simatic.Simulation.Runtime.Api.x64.dll",
        "Siemens.Simatic.Simulation.Runtime.Api.x86.dll"
    )
    if ($forbiddenNames -contains $fileName) {
        throw "Release payload must not ship vendor artifact '$normalized'."
    }

    if ($fileName -match '(?i)(\.Tests?\.|\.Fake\.|ValidationWorker|PerformanceMeasurementWorker)') {
        throw "Release payload contains non-production artifact '$normalized'."
    }

    if ($PackageKind -ne "application" -and $normalized -match '(?i)^profiles/') {
        throw "A profile or plugin package must not contain an application-level profiles root."
    }
}

function Copy-ReleaseTree {
    param(
        [string]$Source,
        [string]$Destination,
        [string]$PackageKind
    )

    $sourcePath = [System.IO.Path]::GetFullPath($Source).TrimEnd('\')
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null

    $files = @(Get-ChildItem -LiteralPath $sourcePath -Recurse -File |
        Where-Object { Test-IsReleaseFile $_ } |
        Sort-Object FullName)
    foreach ($file in $files) {
        $relativePath = $file.FullName.Substring($sourcePath.Length).TrimStart('\')
        Assert-AllowedReleaseFile $relativePath $PackageKind
        $destinationPath = Join-Path $Destination $relativePath
        $destinationDirectory = Split-Path -Parent $destinationPath
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null

        if (Test-Path -LiteralPath $destinationPath) {
            $sourceHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            $destinationHash = (Get-FileHash -LiteralPath $destinationPath -Algorithm SHA256).Hash
            if ($sourceHash -ne $destinationHash) {
                throw "Release outputs collide with different content at '$relativePath'."
            }

            continue
        }

        Copy-Item -LiteralPath $file.FullName -Destination $destinationPath
    }
}

function Assert-RuntimeManifestVersion {
    param(
        [string]$PayloadRoot,
        [string]$PackageKind,
        [string]$ExpectedVersion
    )

    if ($PackageKind -eq "application") {
        return
    }

    $manifestPath = Join-Path $PayloadRoot "manifest.json"
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw "Release payload does not contain manifest.json."
    }

    $runtimeManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $runtimeVersion = if ($PackageKind -eq "profile") {
        $runtimeManifest.profile.version
    }
    else {
        $runtimeManifest.info.bindingVersion
    }

    if ($runtimeVersion -ne $ExpectedVersion) {
        throw "Runtime manifest version '$runtimeVersion' does not match release package version '$ExpectedVersion'."
    }
}

function New-DeterministicZip {
    param(
        [string]$SourceDirectory,
        [string]$DestinationFile
    )

    Add-Type -AssemblyName System.IO.Compression
    $stream = [System.IO.File]::Open($DestinationFile, [System.IO.FileMode]::CreateNew)
    try {
        $archive = New-Object System.IO.Compression.ZipArchive(
            $stream,
            [System.IO.Compression.ZipArchiveMode]::Create,
            $false)
        try {
            $sourcePath = [System.IO.Path]::GetFullPath($SourceDirectory).TrimEnd('\')
            $files = @(Get-ChildItem -LiteralPath $sourcePath -Recurse -File | Sort-Object FullName)
            foreach ($file in $files) {
                $entryName = $file.FullName.Substring($sourcePath.Length).TrimStart('\').Replace('\', '/')
                $entry = $archive.CreateEntry($entryName, [System.IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $fixedZipTimestamp
                $entry.ExternalAttributes = 0

                $entryStream = $entry.Open()
                try {
                    $fileStream = [System.IO.File]::OpenRead($file.FullName)
                    try {
                        $fileStream.CopyTo($entryStream)
                    }
                    finally {
                        $fileStream.Dispose()
                    }
                }
                finally {
                    $entryStream.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Write-PackageManifest {
    param(
        [hashtable]$Definition,
        [string]$ArtifactPath,
        [string]$Revision,
        [string]$DestinationPath
    )

    $artifact = Get-Item -LiteralPath $ArtifactPath
    $artifactHash = (Get-FileHash -LiteralPath $ArtifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifest = [ordered]@{
        schemaVersion = 1
        package = [ordered]@{
            name = $Definition.Name
            version = $Version
            kind = $Definition.Kind
            displayName = $Definition.DisplayName
        }
        artifact = [ordered]@{
            file = $Definition.Artifact
            sha256 = $artifactHash
            size = $artifact.Length
        }
        source = [ordered]@{
            revision = $Revision
        }
        requires = @($Definition.Requires)
        runtimeRequirements = @($Definition.RuntimeRequirements)
    }

    $json = $manifest | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText($DestinationPath, $json + "`n", $utf8WithoutBom)
}

function Get-ProjectAssetsPath {
    param([string]$ProjectPath)

    return Join-Path (Split-Path -Parent ([System.IO.Path]::GetFullPath($ProjectPath))) "obj\project.assets.json"
}

$definitions = @{
    Application = @{
        Name = "plcsim-unittest"
        Kind = "application"
        DisplayName = "PLCSIM.UnitTest"
        Artifact = "application.zip"
        Requires = @()
        RuntimeRequirements = @(
            [ordered]@{ id = "dotnet-desktop"; version = "8.0"; architecture = "x64" }
        )
    }
    SiemensProfile = @{
        Name = "access-profiles.siemens"
        Kind = "profile"
        DisplayName = "Siemens Access Profile"
        Artifact = "profile.zip"
        Project = "access-profiles\siemens\src\PLCSIM.UnitTest.AccessProfiles.Siemens\PLCSIM.UnitTest.AccessProfiles.Siemens.csproj"
        Requires = @()
        RuntimeRequirements = @()
    }
    BeckhoffTc34026Profile = @{
        Name = "access-profiles.beckhoff-tc3-4026"
        Kind = "profile"
        DisplayName = "Beckhoff TwinCAT 3 (4026) Access Profile"
        Artifact = "profile.zip"
        Project = "access-profiles\beckhoff-tc3-4026\src\PLCSIM.UnitTest.AccessProfiles.Beckhoff\PLCSIM.UnitTest.AccessProfiles.Beckhoff.csproj"
        LockedRestore = $true
        Requires = @()
        RuntimeRequirements = @()
    }
    TiaOpennessV15_1 = @{
        Name = "plugins.siemens.tia-openness.v15_1"
        Kind = "plugin"
        DisplayName = "TIA Openness V15.1 Plugin"
        Artifact = "plugin.zip"
        Project = "access-profiles\siemens\tia-openness\v15_1\src\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V15_1\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V15_1.csproj"
        WorkerProject = "access-profiles\siemens\tia-openness\v15_1\worker\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V15_1.Worker\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V15_1.Worker.csproj"
        Requires = @("access-profiles.siemens")
        RuntimeRequirements = @([ordered]@{ id = "net-framework"; version = "4.8" })
    }
    TiaOpennessV17 = @{
        Name = "plugins.siemens.tia-openness.v17"
        Kind = "plugin"
        DisplayName = "TIA Openness V17 Plugin"
        Artifact = "plugin.zip"
        Project = "access-profiles\siemens\tia-openness\v17\src\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V17\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V17.csproj"
        WorkerProject = "access-profiles\siemens\tia-openness\v17\worker\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V17.Worker\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V17.Worker.csproj"
        Requires = @("access-profiles.siemens")
        RuntimeRequirements = @([ordered]@{ id = "net-framework"; version = "4.8" })
    }
    TiaOpennessV18 = @{
        Name = "plugins.siemens.tia-openness.v18"
        Kind = "plugin"
        DisplayName = "TIA Openness V18 Plugin"
        Artifact = "plugin.zip"
        Project = "access-profiles\siemens\tia-openness\v18\src\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V18\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V18.csproj"
        WorkerProject = "access-profiles\siemens\tia-openness\v18\worker\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V18.Worker\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V18.Worker.csproj"
        Requires = @("access-profiles.siemens")
        RuntimeRequirements = @([ordered]@{ id = "net-framework"; version = "4.8" })
    }
    TiaOpennessV19 = @{
        Name = "plugins.siemens.tia-openness.v19"
        Kind = "plugin"
        DisplayName = "TIA Openness V19 Plugin"
        Artifact = "plugin.zip"
        Project = "access-profiles\siemens\tia-openness\v19\src\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V19\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V19.csproj"
        WorkerProject = "access-profiles\siemens\tia-openness\v19\worker\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V19.Worker\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V19.Worker.csproj"
        Requires = @("access-profiles.siemens")
        RuntimeRequirements = @([ordered]@{ id = "net-framework"; version = "4.8" })
    }
    TiaOpennessV20 = @{
        Name = "plugins.siemens.tia-openness.v20"
        Kind = "plugin"
        DisplayName = "TIA Openness V20 Plugin"
        Artifact = "plugin.zip"
        Project = "access-profiles\siemens\tia-openness\v20\src\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V20\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V20.csproj"
        WorkerProject = "access-profiles\siemens\tia-openness\v20\worker\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V20.Worker\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V20.Worker.csproj"
        Requires = @("access-profiles.siemens")
        RuntimeRequirements = @([ordered]@{ id = "net-framework"; version = "4.8" })
    }
    TiaOpennessV21 = @{
        Name = "plugins.siemens.tia-openness.v21"
        Kind = "plugin"
        DisplayName = "TIA Openness V21 Plugin"
        Artifact = "plugin.zip"
        Project = "access-profiles\siemens\tia-openness\v21\src\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V21\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V21.csproj"
        WorkerProject = "access-profiles\siemens\tia-openness\v21\worker\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V21.Worker\PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V21.Worker.csproj"
        Requires = @("access-profiles.siemens")
        RuntimeRequirements = @(
            [ordered]@{ id = "net-framework"; version = "4.8" }
        )
    }
    PlcsimAdvancedV6 = @{
        Name = "plugins.siemens.plcsim-advanced.v6"
        Kind = "plugin"
        DisplayName = "PLCSIM Advanced V6 Plugin"
        Artifact = "plugin.zip"
        Project = "access-profiles\siemens\plcsim-advanced\v6\src\PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V6\PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V6.csproj"
        WorkerProject = "access-profiles\siemens\plcsim-advanced\v6\worker\PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V6.Worker\PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V6.Worker.csproj"
        Requires = @("access-profiles.siemens")
        RuntimeRequirements = @([ordered]@{ id = "net-framework"; version = "4.8" })
    }
    PlcsimAdvancedV7 = @{
        Name = "plugins.siemens.plcsim-advanced.v7"
        Kind = "plugin"
        DisplayName = "PLCSIM Advanced V7 Plugin"
        Artifact = "plugin.zip"
        Project = "access-profiles\siemens\plcsim-advanced\v7\src\PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V7\PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V7.csproj"
        WorkerProject = "access-profiles\siemens\plcsim-advanced\v7\worker\PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V7.Worker\PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V7.Worker.csproj"
        Requires = @("access-profiles.siemens")
        RuntimeRequirements = @([ordered]@{ id = "net-framework"; version = "4.8" })
    }
    PlcsimAdvancedV8 = @{
        Name = "plugins.siemens.plcsim-advanced.v8"
        Kind = "plugin"
        DisplayName = "PLCSIM Advanced V8 Plugin"
        Artifact = "plugin.zip"
        Project = "access-profiles\siemens\plcsim-advanced\v8\src\PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8\PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.csproj"
        WorkerProject = "access-profiles\siemens\plcsim-advanced\v8\worker\PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.Worker\PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.Worker.csproj"
        Requires = @("access-profiles.siemens")
        RuntimeRequirements = @(
            [ordered]@{ id = "net-framework"; version = "4.8" }
        )
    }
}

$definition = $definitions[$Package]
$revision = Resolve-SourceRevision
Assert-CleanSource

$packageOutputDirectory = Join-Path (Join-Path $outputRootPath $definition.Name) $Version
if (Test-Path -LiteralPath $packageOutputDirectory) {
    throw "Package output '$packageOutputDirectory' already exists. Published package versions are immutable."
}

New-Item -ItemType Directory -Path $outputRootPath -Force | Out-Null
$stagingRoot = Join-Path $outputRootPath (".staging-{0}-{1}" -f $PID, [Guid]::NewGuid().ToString("N"))
$payloadRoot = Join-Path $stagingRoot "payload"
$buildRoot = Join-Path $stagingRoot "build"
New-Item -ItemType Directory -Path $payloadRoot -Force | Out-Null
New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null

try {
    $noticeAssetFiles = @()
    if ($Package -eq "Application") {
        $wpfOutput = Join-Path $buildRoot "wpf"
        $cliOutput = Join-Path $buildRoot "cli"
        Invoke-CheckedCommand "dotnet" @(
            "restore",
            (Join-Path $repoRoot "src\PLCSIM.UnitTest.App.Wpf\PLCSIM.UnitTest.App.Wpf.csproj"),
            "--runtime", "win-x64",
            "--source", $nugetSource
        )
        Invoke-CheckedCommand "dotnet" @(
            "restore",
            (Join-Path $repoRoot "src\PLCSIM.UnitTest.App.Cli\PLCSIM.UnitTest.App.Cli.csproj"),
            "--runtime", "win-x64",
            "--source", $nugetSource
        )
        Invoke-CheckedCommand "dotnet" @(
            "publish",
            (Join-Path $repoRoot "src\PLCSIM.UnitTest.App.Wpf\PLCSIM.UnitTest.App.Wpf.csproj"),
            "--configuration", "Release",
            "--runtime", "win-x64",
            "--self-contained", "false",
            "--no-restore",
            "--output", $wpfOutput,
            "/p:Version=$Version"
        )
        Invoke-CheckedCommand "dotnet" @(
            "publish",
            (Join-Path $repoRoot "src\PLCSIM.UnitTest.App.Cli\PLCSIM.UnitTest.App.Cli.csproj"),
            "--configuration", "Release",
            "--runtime", "win-x64",
            "--self-contained", "false",
            "--no-restore",
            "--output", $cliOutput,
            "/p:Version=$Version"
        )
        Copy-ReleaseTree $wpfOutput $payloadRoot $definition.Kind
        Copy-ReleaseTree $cliOutput $payloadRoot $definition.Kind
        $noticeAssetFiles = @(
            Get-ProjectAssetsPath (Join-Path $repoRoot "src\PLCSIM.UnitTest.App.Wpf\PLCSIM.UnitTest.App.Wpf.csproj")
            Get-ProjectAssetsPath (Join-Path $repoRoot "src\PLCSIM.UnitTest.App.Cli\PLCSIM.UnitTest.App.Cli.csproj")
        )
    }
    else {
        $deploymentRoot = Join-Path $buildRoot "deployment"
        $restoreArguments = @(
            "restore",
            (Join-Path $repoRoot $definition.Project),
            "--source", $nugetSource
        )
        if ($definition.ContainsKey("LockedRestore") -and $definition.LockedRestore) {
            $restoreArguments += "--locked-mode"
        }
        Invoke-CheckedCommand "dotnet" $restoreArguments
        if ($definition.ContainsKey("WorkerProject")) {
            Invoke-CheckedCommand "dotnet" @(
                "restore",
                (Join-Path $repoRoot $definition.WorkerProject),
                "--source", $nugetSource
            )
        }
        Invoke-CheckedCommand "dotnet" @(
            "msbuild",
            (Join-Path $repoRoot $definition.Project),
            "/t:DeployPluginPackage",
            "/p:Configuration=Release",
            "/p:Version=$Version",
            "/p:RestoreIgnoreFailedSources=false",
            "/p:PluginDeploymentRoot=$deploymentRoot",
            "/p:PluginPackageSubDir=package"
        )
        Copy-ReleaseTree (Join-Path $deploymentRoot "package") $payloadRoot $definition.Kind
        $noticeAssetFiles = @(Get-ProjectAssetsPath (Join-Path $repoRoot $definition.Project))
        if ($definition.ContainsKey("WorkerProject")) {
            $noticeAssetFiles += Get-ProjectAssetsPath (Join-Path $repoRoot $definition.WorkerProject)
        }
    }

    Copy-Item -LiteralPath $licensePath -Destination (Join-Path $payloadRoot "LICENSE.txt")
    & $noticeGeneratorPath `
        -AssetFiles $noticeAssetFiles `
        -PayloadRoot $payloadRoot `
        -IncludeDotNetHost:($Package -eq "Application") `
        -OutputPath (Join-Path $payloadRoot "THIRD-PARTY-NOTICES.txt")

    $payloadFiles = @(Get-ChildItem -LiteralPath $payloadRoot -Recurse -File)
    if ($payloadFiles.Count -eq 0) {
        throw "Release payload is empty."
    }

    Assert-RuntimeManifestVersion $payloadRoot $definition.Kind $Version

    New-Item -ItemType Directory -Path $packageOutputDirectory -Force | Out-Null
    $artifactPath = Join-Path $packageOutputDirectory $definition.Artifact
    New-DeterministicZip $payloadRoot $artifactPath
    Write-PackageManifest $definition $artifactPath $revision (Join-Path $packageOutputDirectory "package-manifest.json")

    Write-Host "Release package created at '$packageOutputDirectory'."
}
catch {
    if (Test-Path -LiteralPath $packageOutputDirectory) {
        Remove-VerifiedDirectory $packageOutputDirectory $outputRootPath
    }
    throw
}
finally {
    Remove-VerifiedDirectory $stagingRoot $outputRootPath
}
