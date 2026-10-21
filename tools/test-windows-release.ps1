[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ResolvedBundleRoot,

    [Parameter(Mandatory = $true)]
    [string]$DistributionRoot,

    [Parameter(Mandatory = $true)]
    [string]$InstallerRoot,

    [switch]$RunWpfStartup
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$resolvedBundlePath = [System.IO.Path]::GetFullPath($ResolvedBundleRoot)
$distributionPath = [System.IO.Path]::GetFullPath($DistributionRoot)
$installerPath = [System.IO.Path]::GetFullPath($InstallerRoot)
$bundleManifestPath = Join-Path $resolvedBundlePath "bundle-manifest.json"
$stagedBundleManifestPath = Join-Path $distributionPath "distribution\bundle-manifest.json"
$publishedBundleManifestPath = Join-Path $installerPath "bundle-manifest.json"
$registrationLauncherPath = Join-Path $repoRoot "installer\windows\RegisterAccessProfiles.ps1"
$innoDefinitionPath = Join-Path $repoRoot "installer\windows\PLCSIM.UnitTest.iss"
$compositionGeneratorPath = Join-Path $repoRoot "tools/new-windows-installer-composition.ps1"
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("PLCSIM.UnitTest.ReleaseSmoke-" + [Guid]::NewGuid().ToString("N"))

function Assert-Condition {
    param([bool]$Condition, [string]$Message)

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-FileExists {
    param([string]$Path, [string]$Label)

    Assert-Condition (Test-Path -LiteralPath $Path -PathType Leaf) "$Label '$Path' does not exist."
}

function Get-LowerSha256 {
    param([string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-DirectoryFingerprint {
    param([string]$Path)

    $rootPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\') + '\'
    $entries = @(
        Get-ChildItem -LiteralPath $Path -File -Recurse |
            Sort-Object FullName |
            ForEach-Object {
                $relativePath = $_.FullName.Substring($rootPath.Length).Replace('\', '/')
                "$relativePath|$(Get-LowerSha256 $_.FullName)"
            }
    )
    return $entries -join "`n"
}

function Get-ProfileDataDirectory {
    param(
        [string]$ProfileId,
        [string]$ProfileVersion,
        [string]$ProfileSourcePath)

    $normalizedSourcePath = [System.IO.Path]::GetFullPath($ProfileSourcePath).TrimEnd('\')
    $identity = @(
        $ProfileId.Trim().ToUpperInvariant()
        $ProfileVersion.Trim().ToUpperInvariant()
        "DIRECTORYPACKAGE"
        $normalizedSourcePath.ToUpperInvariant()
    ) -join "`n"
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $sourceKey = -join ($sha256.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($identity)) | ForEach-Object { $_.ToString("x2") })
    }
    finally {
        $sha256.Dispose()
    }

    return Join-Path $env:LOCALAPPDATA "PLCSIM.UnitTest\profile-data\profile-$ProfileId\version-$ProfileVersion\$sourceKey"
}

function Assert-ByteEqual {
    param([string]$Left, [string]$Right, [string]$Label)

    Assert-FileExists $Left $Label
    Assert-FileExists $Right $Label
    Assert-Condition ((Get-LowerSha256 $Left) -eq (Get-LowerSha256 $Right)) "$Label differs between '$Left' and '$Right'."
}

function Get-SmokeProfiles {
    param([object]$Bundle, [string]$LayoutPath)

    foreach ($component in $Bundle.components) {
        $inventoryPath = Join-Path $LayoutPath "distribution\packages\$($component.package)\package-manifest.json"
        $inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
        if ($inventory.package.kind -ne "profile") {
            continue
        }

        $profilePath = [System.IO.Path]::GetFullPath((Join-Path $LayoutPath ([string]$component.destination).Replace('/', '\')))
        $runtimeManifest = Get-Content -LiteralPath (Join-Path $profilePath "manifest.json") -Raw | ConvertFrom-Json
        $id = [string]$runtimeManifest.profile.id
        $version = [string]$runtimeManifest.profile.version
        $dataDirectory = Get-ProfileDataDirectory $id $version $profilePath
        [pscustomobject]@{
            Id = $id
            Version = $version
            Path = $profilePath
            DataDirectory = $dataDirectory
            DataDirectoryExisted = Test-Path -LiteralPath $dataDirectory
        }
    }
}

function Remove-SmokeProfileData {
    param([object[]]$Profiles, [string]$LayoutPath)

    $layoutPrefix = [System.IO.Path]::GetFullPath($LayoutPath).TrimEnd('\') + '\'
    $dataPrefix = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA "PLCSIM.UnitTest\profile-data")).TrimEnd('\') + '\'
    foreach ($profile in $Profiles) {
        if ($profile.DataDirectoryExisted) {
            continue
        }
        $sourcePath = [System.IO.Path]::GetFullPath($profile.Path)
        $dataPath = [System.IO.Path]::GetFullPath($profile.DataDirectory)
        $expectedPath = Get-ProfileDataDirectory $profile.Id $profile.Version $sourcePath
        Assert-Condition ($sourcePath.StartsWith($layoutPrefix, [StringComparison]::OrdinalIgnoreCase)) "Refusing cleanup for profile outside the smoke layout: '$sourcePath'."
        Assert-Condition ($dataPath.StartsWith($dataPrefix, [StringComparison]::OrdinalIgnoreCase) -and $dataPath -eq $expectedPath) "Refusing cleanup of unexpected profile-data path '$dataPath'."
        if (Test-Path -LiteralPath $dataPath) {
            Remove-Item -LiteralPath $dataPath -Recurse -Force
        }
    }
}

function Assert-AllowedReleaseFile {
    param([string]$RelativePath)

    $normalized = $RelativePath.Replace('\', '/')
    $segments = @($normalized.Split('/') | ForEach-Object { $_.ToLowerInvariant() })
    $fileName = [System.IO.Path]::GetFileName($normalized)
    $forbidden =
        $segments -contains "bin" -or
        $segments -contains "obj" -or
        $segments -contains "tests" -or
        $segments -contains "testresults" -or
        $segments -contains ".tmp" -or
        $segments -contains "cache" -or
        $segments -contains "logs" -or
        [System.IO.Path]::GetExtension($fileName).ToLowerInvariant() -in @(".pdb", ".log", ".trx", ".cache", ".user") -or
        $fileName -match '(?i)(\.Tests?\.|\.Fake\.|ValidationWorker|PerformanceMeasurementWorker|Siemens\.Engineering\.dll|Siemens\.Simatic\.Simulation\.Runtime\.Api)'
    Assert-Condition (-not $forbidden) "Release content contains forbidden artifact '$normalized'."
}

function Get-PackagePins {
    param([object]$Bundle)

    return @($Bundle.application) + @($Bundle.components)
}

function Assert-PackageAndStaging {
    param([object]$Bundle)

    Add-Type -AssemblyName System.IO.Compression
    $expectedStagedFiles = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

    foreach ($pin in Get-PackagePins $Bundle) {
        $packageName = [string]$pin.package
        $packageVersion = [string]$pin.version
        $destination = [string]$pin.destination
        $packageRoot = Join-Path (Join-Path (Join-Path $resolvedBundlePath "packages") $packageName) $packageVersion
        $manifestPath = Join-Path $packageRoot "package-manifest.json"
        Assert-FileExists $manifestPath "Resolved package manifest"
        Assert-Condition ((Get-LowerSha256 $manifestPath) -eq ([string]$pin.manifestSha256).ToLowerInvariant()) "Package manifest hash mismatch for '$packageName/$packageVersion'."

        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        Assert-Condition ($manifest.package.name -eq $packageName -and $manifest.package.version -eq $packageVersion) "Package identity mismatch for '$packageName/$packageVersion'."
        $artifactPath = Join-Path $packageRoot ([string]$manifest.artifact.file)
        Assert-FileExists $artifactPath "Resolved package artifact"
        Assert-Condition ((Get-Item -LiteralPath $artifactPath).Length -eq [long]$manifest.artifact.size) "Package artifact size mismatch for '$packageName/$packageVersion'."
        Assert-Condition ((Get-LowerSha256 $artifactPath) -eq [string]$manifest.artifact.sha256) "Package artifact hash mismatch for '$packageName/$packageVersion'."

        $inventoryPath = Join-Path $distributionPath "distribution\packages\$packageName\package-manifest.json"
        Assert-ByteEqual $manifestPath $inventoryPath "Installed package inventory"
        $null = $expectedStagedFiles.Add("distribution/packages/$packageName/package-manifest.json")

        $stream = [System.IO.File]::OpenRead($artifactPath)
        try {
            $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Read, $false)
            try {
                $entryNames = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
                Assert-Condition ($entryNames -contains "LICENSE.txt") "Package '$packageName/$packageVersion' does not contain LICENSE.txt."
                Assert-Condition ($entryNames -contains "THIRD-PARTY-NOTICES.txt") "Package '$packageName/$packageVersion' does not contain THIRD-PARTY-NOTICES.txt."
                foreach ($entry in $archive.Entries) {
                    if ([string]::IsNullOrEmpty($entry.Name)) {
                        continue
                    }
                    $entryName = $entry.FullName.Replace('\', '/')
                    Assert-AllowedReleaseFile $entryName
                    $relativeTarget = if ($destination -eq ".") { $entryName } else { "$destination/$entryName" }
                    Assert-Condition ($expectedStagedFiles.Add($relativeTarget)) "Multiple packages own '$relativeTarget'."
                    $targetPath = Join-Path $distributionPath $relativeTarget.Replace('/', '\')
                    Assert-FileExists $targetPath "Staged package file"

                    $entryStream = $entry.Open()
                    try {
                        $sha = [System.Security.Cryptography.SHA256]::Create()
                        try {
                            $entryHash = [BitConverter]::ToString($sha.ComputeHash($entryStream)).Replace("-", "").ToLowerInvariant()
                        }
                        finally {
                            $sha.Dispose()
                        }
                    }
                    finally {
                        $entryStream.Dispose()
                    }
                    Assert-Condition ($entryHash -eq (Get-LowerSha256 $targetPath)) "Staged file '$relativeTarget' differs from its package payload."
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

    $null = $expectedStagedFiles.Add("distribution/bundle-manifest.json")
    $null = $expectedStagedFiles.Add("distribution/THIRD-PARTY-NOTICES.txt")
    $distributionPrefix = $distributionPath.TrimEnd('\') + '\'
    $actualFiles = @(Get-ChildItem -LiteralPath $distributionPath -Recurse -File | ForEach-Object {
        $_.FullName.Substring($distributionPrefix.Length).Replace('\', '/')
    })
    foreach ($relativePath in $actualFiles) {
        Assert-AllowedReleaseFile $relativePath
        Assert-Condition ($expectedStagedFiles.Contains($relativePath)) "Distribution contains unowned file '$relativePath'."
    }
    Assert-Condition ($actualFiles.Count -eq $expectedStagedFiles.Count) "Distribution file count does not match the package inventories."
}

function Convert-ToInstallerComponentSegment {
    param([string]$Id)

    return "component_" + [regex]::Replace($Id, '[^a-z0-9]', '_')
}

function Get-ComponentParentId {
    param([object]$Component)

    $property = $Component.PSObject.Properties["parent"]
    if ($null -eq $property) {
        return ""
    }
    return [string]$property.Value
}

function Resolve-InstallerComponentName {
    param([object]$Component, [hashtable]$ComponentsById, [hashtable]$NamesById)

    $id = [string]$Component.id
    if ($NamesById.ContainsKey($id)) {
        return [string]$NamesById[$id]
    }
    $parentId = Get-ComponentParentId $Component
    $name = Convert-ToInstallerComponentSegment $id
    if (-not [string]::IsNullOrEmpty($parentId)) {
        $name = (Resolve-InstallerComponentName $ComponentsById[$parentId] $ComponentsById $NamesById) + "\" + $name
    }
    $NamesById[$id] = $name
    return $name
}

function Assert-InnoComponentMapping {
    param([object]$Bundle)

    $definition = Get-Content -LiteralPath $innoDefinitionPath -Raw
    Assert-Condition $definition.Contains('#include GeneratedComponentsFile') "Inno definition does not include generated components."
    Assert-Condition $definition.Contains('#include GeneratedFilesFile') "Inno definition does not include generated files."
    Assert-Condition $definition.Contains('AppVersion={#DistributionVersion}') "Inno definition does not retain the distribution version as installer metadata."
    Assert-Condition $definition.Contains('AppVerName={#ProductName}') "Inno welcome text is not based on the version-neutral product name."
    Assert-Condition (-not $definition.Contains('AppVerName={#ProductName} {#DistributionVersion}')) "Inno welcome text still exposes the distribution version."
    $postInstallRegistrationLines = @(
        $definition -split "\r?\n" |
            Where-Object { $_.Contains("postinstall") -and $_.Contains("register-profiles") }
    )
    Assert-Condition ($postInstallRegistrationLines.Count -eq 1) "Inno definition must contain exactly one post-install registration action."
    Assert-Condition $postInstallRegistrationLines[0].Contains("runasoriginaluser") "Post-install registration must run in the original interactive user's context."
    Assert-Condition $postInstallRegistrationLines[0].Contains("{#CliExecutable}") "Post-install registration must invoke the installed CLI directly."
    Assert-Condition (-not $postInstallRegistrationLines[0].Contains("RegisterAccessProfiles.ps1")) "Post-install registration must not invoke the Start Menu maintenance launcher."
    Assert-Condition (-not $postInstallRegistrationLines[0].Contains("powershell.exe")) "Post-install registration must not depend on PowerShell."
    Assert-Condition $definition.Contains("RemoveInstallerOwnedInstallRoot := IsInstallerOwnedInstallRoot") "Uninstall does not capture installer-owned root status while the bundle marker is available."
    Assert-Condition $definition.Contains("if RemoveInstallerOwnedInstallRoot then") "Uninstall does not clean the installer-owned installation root."
    Assert-Condition $definition.Contains("CleanupError := CleanInstallRootContents") "Uninstall does not use the shared whole-tree cleanup implementation."
    Assert-Condition $definition.Contains('Type: dirifempty; Name: "{app}"') "Uninstall does not remove the empty installation root."

    $componentsPath = Join-Path $temporaryRoot "generated-components.iss"
    $filesPath = Join-Path $temporaryRoot "generated-files.iss"
    & $compositionGeneratorPath -DistributionRoot $distributionPath -ComponentsFile $componentsPath -FilesFile $filesPath
    $componentLines = @(Get-Content -LiteralPath $componentsPath)
    $fileLines = @(Get-Content -LiteralPath $filesPath)
    Assert-Condition ($componentLines.Count -eq (1 + @($Bundle.components).Count)) "Generated installer component count does not match the bundle plus its fixed application component."
    Assert-Condition ($fileLines.Count -eq (4 + (2 * @($Bundle.components).Count))) "Generated installer file mapping count does not match the bundle."
    Assert-Condition (@($fileLines | Where-Object { $_.Contains('distribution\THIRD-PARTY-NOTICES.txt') }).Count -eq 1) "Installer does not include the aggregate third-party notices."

    $applicationInventoryPath = Join-Path $distributionPath "distribution\packages\$($Bundle.application.package)\package-manifest.json"
    $applicationInventory = Get-Content -LiteralPath $applicationInventoryPath -Raw | ConvertFrom-Json
    $applicationLine = @($componentLines | Where-Object { $_ -like 'Name: "application";*' })
    Assert-Condition ($applicationLine.Count -eq 1) "Installer does not have exactly one fixed application component."
    $applicationDescription = 'Description: "{0} - {1}"' -f @(
        ([string]$applicationInventory.package.displayName).Replace('"', '""'),
        ([string]$applicationInventory.package.version).Replace('"', '""'))
    Assert-Condition $applicationLine[0].Contains($applicationDescription) "Installer application component does not use the pinned package display name and version."
    Assert-Condition $applicationLine[0].Contains('; Types: full custom') "Installer application component is not selected for every installation type."
    Assert-Condition $applicationLine[0].Contains('; Flags: fixed ') "Installer application component can be deselected."

    $componentsById = @{}
    foreach ($component in $Bundle.components) {
        $componentsById[[string]$component.id] = $component
    }
    $namesById = @{}
    foreach ($component in $Bundle.components) {
        $id = [string]$component.id
        $name = Resolve-InstallerComponentName $component $componentsById $namesById
        $inventoryPath = Join-Path $distributionPath "distribution\packages\$($component.package)\package-manifest.json"
        $inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
        $componentLine = @($componentLines | Where-Object { $_ -like "Name: `"$name`";*" })
        Assert-Condition ($componentLine.Count -eq 1) "Installer component '$id' does not have exactly one generated definition."
        $descriptionFragment = 'Description: "{0} - {1}"' -f @(
            ([string]$inventory.package.displayName).Replace('"', '""'),
            ([string]$inventory.package.version).Replace('"', '""'))
        Assert-Condition $componentLine[0].Contains($descriptionFragment) "Installer component '$id' does not use the pinned package display name and version."
        Assert-Condition ($componentLine[0].Contains('; Types: full') -eq [bool]$component.selectedByDefault) "Installer component '$id' has the wrong default selection."
        $hasChildren = @($Bundle.components | Where-Object { (Get-ComponentParentId $_) -eq $id }).Count -ne 0
        Assert-Condition ($componentLine[0].Contains('; Flags: checkablealone') -eq $hasChildren) "Installer component '$id' has the wrong hierarchy flags."
        Assert-Condition $componentLine[0].Contains('disablenouninstallwarning') "Installer component '$id' does not suppress the inapplicable deselection warning."

        $destination = ([string]$component.destination).Replace('/', '\')
        $payloadFragment = 'Source: "{{#SourceRoot}}\{0}\*"' -f $destination
        $inventoryFragment = "distribution\packages\$($component.package)\package-manifest.json"
        $componentFragment = "Components: $name;"
        Assert-Condition (@($fileLines | Where-Object { $_.Contains($payloadFragment) -and $_.Contains($componentFragment) }).Count -eq 1) "Installer payload mapping is missing for '$id'."
        Assert-Condition (@($fileLines | Where-Object { $_.Contains($inventoryFragment) -and $_.Contains($componentFragment) }).Count -eq 1) "Installer inventory mapping is missing for '$id'."
    }
}

function Invoke-Cli {
    param([string]$Executable, [string[]]$Arguments)

    $lines = @(& $Executable @Arguments 2>&1 | ForEach-Object { $_.ToString() })
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $lines -join "`n" }
}

function Invoke-RegistrationLauncher {
    param([string]$Launcher)

    $windowsPowerShell = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
    Assert-FileExists $windowsPowerShell "Windows PowerShell"
    $oldErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $lines = @(
            & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -File $Launcher -NoPause 2>&1 |
                ForEach-Object { $_.ToString() }
        )
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $lines -join "`n" }
    }
    finally {
        $ErrorActionPreference = $oldErrorActionPreference
    }
}

function Assert-ApplicationSmoke {
    param([object]$Bundle)

    $layoutPath = Join-Path $temporaryRoot "installed-layout"
    Copy-Item -LiteralPath $distributionPath -Destination $layoutPath -Recurse
    Copy-Item -LiteralPath $registrationLauncherPath -Destination $layoutPath

    $cliPath = Join-Path $layoutPath "PLCSIM.UnitTest.App.Cli.exe"
    $wpfPath = Join-Path $layoutPath "PLCSIM.UnitTest.App.Wpf.exe"
    Assert-FileExists $cliPath "Installed CLI"
    Assert-FileExists $wpfPath "Installed WPF application"
    Assert-FileExists (Join-Path $layoutPath "Help\index.html") "Installed offline help"
    Assert-FileExists (Join-Path $layoutPath "Examples\CI\README.md") "Installed CI starter documentation"
    Assert-FileExists (Join-Path $layoutPath "LICENSE.txt") "Installed application license"
    Assert-FileExists (Join-Path $layoutPath "THIRD-PARTY-NOTICES.txt") "Installed application third-party notices"
    Assert-FileExists (Join-Path $layoutPath "distribution\THIRD-PARTY-NOTICES.txt") "Installed aggregate third-party notices"
    $installedRegistrationLauncher = Join-Path $layoutPath "RegisterAccessProfiles.ps1"
    Assert-FileExists $installedRegistrationLauncher "Installed registration launcher"

    $launcherSource = Get-Content -LiteralPath $installedRegistrationLauncher -Raw
    foreach ($forbiddenElevationToken in @("-Elevated", "Test-IsAdministrator", "WindowsPrincipal", "Verb RunAs", "Start-Process")) {
        Assert-Condition ($launcherSource.IndexOf($forbiddenElevationToken, [StringComparison]::OrdinalIgnoreCase) -lt 0) "Generic registration launcher contains forbidden elevation behavior '$forbiddenElevationToken'."
    }

    $registryPath = Join-Path $temporaryRoot "application-data\application-profile-registry.json"
    $oldProfileRoot = $env:PLCSIM_UNITTEST_PROFILE_DISCOVERY_DIRECTORY
    $oldRegistryPath = $env:PLCSIM_UNITTEST_PROFILE_REGISTRY_PATH
    $expectedProfiles = @(Get-SmokeProfiles $Bundle $layoutPath)
    foreach ($profile in $expectedProfiles) {
        Assert-Condition (-not $profile.DataDirectoryExisted) "Smoke profile-data path already exists; refusing to modify existing data '$($profile.DataDirectory)'."
    }
    try {
        $env:PLCSIM_UNITTEST_PROFILE_DISCOVERY_DIRECTORY = Join-Path $layoutPath "profiles"
        $env:PLCSIM_UNITTEST_PROFILE_REGISTRY_PATH = $registryPath
        $profileTreeFingerprintBefore = Get-DirectoryFingerprint $env:PLCSIM_UNITTEST_PROFILE_DISCOVERY_DIRECTORY

        $help = Invoke-Cli $cliPath @("--help")
        Assert-Condition ($help.ExitCode -eq 0) "Installed CLI --help failed with exit code $($help.ExitCode)."
        foreach ($command in @("run", "check-config", "list-profiles", "register-profiles")) {
            Assert-Condition $help.Output.Contains($command) "Installed CLI help does not list '$command'."
        }

        $registration = Invoke-RegistrationLauncher $installedRegistrationLauncher
        Assert-Condition ($registration.ExitCode -eq 0) "Installed profile registration launcher failed: $($registration.Output)"
        $list = Invoke-Cli $cliPath @("list-profiles")
        Assert-Condition ($list.ExitCode -eq 0) "Installed profile listing failed: $($list.Output)"

        Assert-FileExists $registryPath "Application profile registry"
        Assert-Condition ((Get-DirectoryFingerprint $env:PLCSIM_UNITTEST_PROFILE_DISCOVERY_DIRECTORY) -eq $profileTreeFingerprintBefore) "Profile registration modified the installed profile package tree."
        $registry = Get-Content -LiteralPath $registryPath -Raw | ConvertFrom-Json
        Assert-Condition (@($registry.profiles).Count -eq $expectedProfiles.Count) "Installed registration profile count does not match the bundle."
        foreach ($expectedProfile in $expectedProfiles) {
            Assert-Condition (Test-Path -LiteralPath $expectedProfile.DataDirectory -PathType Container) "Profile registration did not create the expected per-user profile-data directory '$($expectedProfile.DataDirectory)'."
            $matches = @($registry.profiles | Where-Object {
                $_.profileId -eq $expectedProfile.Id -and $_.profileVersion -eq $expectedProfile.Version
            })
            Assert-Condition ($matches.Count -eq 1) "Installed profile '$($expectedProfile.Id)/$($expectedProfile.Version)' does not have exactly one registry entry."
            Assert-Condition ($matches[0].registration.state -eq "Registered") "Installed profile '$($expectedProfile.Id)' was not registered successfully."
            Assert-Condition ([System.IO.Path]::GetFullPath([string]$matches[0].source.path) -eq $expectedProfile.Path) "Profile registry source does not point to '$($expectedProfile.Path)'."
            Assert-Condition $list.Output.Contains($expectedProfile.Path) "CLI profile listing does not report '$($expectedProfile.Path)'."
        }

        if ($RunWpfStartup) {
            $process = Start-Process -FilePath $wpfPath -WorkingDirectory $layoutPath -PassThru
            try {
                Start-Sleep -Seconds 3
                Assert-Condition (-not $process.HasExited) "Installed WPF application exited during startup."
                $null = $process.CloseMainWindow()
                Assert-Condition ($process.WaitForExit(10000)) "Installed WPF application did not close after its main window was requested to close."
                Assert-Condition ($process.ExitCode -eq 0) "Installed WPF application exited with code $($process.ExitCode)."
            }
            finally {
                if (-not $process.HasExited) {
                    Stop-Process -Id $process.Id -Force
                    $process.WaitForExit()
                }
                $process.Dispose()
            }
        }
    }
    finally {
        $env:PLCSIM_UNITTEST_PROFILE_DISCOVERY_DIRECTORY = $oldProfileRoot
        $env:PLCSIM_UNITTEST_PROFILE_REGISTRY_PATH = $oldRegistryPath
        Remove-SmokeProfileData $expectedProfiles $layoutPath
    }
}

function Assert-InstallerPublication {
    param([object]$Bundle)

    $version = [string]$Bundle.distribution.version
    $setupFileName = "PLCSIM.UnitTest-Setup-$version.exe"
    $setupPath = Join-Path $installerPath $setupFileName
    $checksumPath = Join-Path $installerPath "SHA256SUMS"
    Assert-FileExists $setupPath "Windows installer"
    Assert-FileExists $checksumPath "Installer checksums"
    Assert-ByteEqual $bundleManifestPath $publishedBundleManifestPath "Published bundle manifest"

    $expectedChecksums = @{
        $setupFileName = Get-LowerSha256 $setupPath
        "bundle-manifest.json" = Get-LowerSha256 $publishedBundleManifestPath
    }
    $actualChecksums = @{}
    foreach ($line in Get-Content -LiteralPath $checksumPath) {
        if ($line -notmatch '^(?<hash>[0-9a-f]{64})  (?<file>[^\\/]+)$') {
            throw "Invalid SHA256SUMS entry '$line'."
        }
        Assert-Condition (-not $actualChecksums.ContainsKey($Matches.file)) "Duplicate SHA256SUMS entry '$($Matches.file)'."
        $actualChecksums[$Matches.file] = $Matches.hash
    }
    Assert-Condition ($actualChecksums.Count -eq $expectedChecksums.Count) "SHA256SUMS must contain exactly the installer and bundle manifest."
    foreach ($fileName in $expectedChecksums.Keys) {
        Assert-Condition ($actualChecksums[$fileName] -eq $expectedChecksums[$fileName]) "SHA-256 mismatch for '$fileName'."
    }
    $publishedFiles = @(Get-ChildItem -LiteralPath $installerPath -File | ForEach-Object { $_.Name })
    Assert-Condition ($publishedFiles.Count -eq 3) "Installer publication must contain exactly the setup, bundle manifest, and SHA256SUMS."
    foreach ($fileName in @($setupFileName, "bundle-manifest.json", "SHA256SUMS")) {
        Assert-Condition ($publishedFiles -contains $fileName) "Installer publication is missing '$fileName'."
    }

    $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($setupPath)
    Assert-Condition ($versionInfo.ProductName.Trim() -eq "PLCSIM.UnitTest") "Installer product name is incorrect."
    Assert-Condition ($versionInfo.ProductVersion.Trim() -eq $version) "Installer product version is incorrect."
    $numericVersion = [regex]::Match($version, '^[0-9]+\.[0-9]+\.[0-9]+').Value
    Assert-Condition ($versionInfo.FileVersion.Trim() -eq "$numericVersion.0") "Installer file version is incorrect."
}

Assert-FileExists $bundleManifestPath "Resolved bundle manifest"
Assert-FileExists $stagedBundleManifestPath "Staged bundle manifest"
Assert-FileExists $publishedBundleManifestPath "Published bundle manifest"
Assert-FileExists $registrationLauncherPath "Registration launcher"
Assert-FileExists $innoDefinitionPath "Inno Setup definition"
Assert-FileExists $compositionGeneratorPath "Installer composition generator"

$bundle = Get-Content -LiteralPath $bundleManifestPath -Raw | ConvertFrom-Json
Assert-Condition ($bundle.schemaVersion -eq 1 -and $bundle.distribution.name -eq "plcsim-unittest.windows") "Unsupported Windows bundle manifest."
Assert-ByteEqual $bundleManifestPath $stagedBundleManifestPath "Staged bundle manifest"

New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
try {
    Assert-PackageAndStaging $bundle
    $expectedNoticesPath = Join-Path $temporaryRoot "expected-third-party-notices.txt"
    & (Join-Path $repoRoot "tools/new-bundle-third-party-notices.ps1") `
        -DistributionRoot $distributionPath -OutputPath $expectedNoticesPath
    Assert-ByteEqual $expectedNoticesPath (Join-Path $distributionPath "distribution\THIRD-PARTY-NOTICES.txt") "Aggregate third-party notices"
    Assert-InnoComponentMapping $bundle
    Assert-ApplicationSmoke $bundle
    Assert-InstallerPublication $bundle
    Write-Host "Windows release verification passed for '$($bundle.distribution.name)/$($bundle.distribution.version)'."
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
