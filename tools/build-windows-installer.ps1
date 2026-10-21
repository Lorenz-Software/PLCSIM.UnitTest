[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DistributionRoot,

    [string]$OutputRoot = "artifacts\windows-installer",

    [string]$InnoCompilerPath = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$distributionRootPath = [System.IO.Path]::GetFullPath($DistributionRoot)
$outputRootPath = if ([System.IO.Path]::IsPathRooted($OutputRoot)) {
    [System.IO.Path]::GetFullPath($OutputRoot)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputRoot))
}
$definitionPath = Join-Path $repoRoot "installer\windows\PLCSIM.UnitTest.iss"
$compositionGeneratorPath = Join-Path $repoRoot "tools/new-windows-installer-composition.ps1"
$licensePath = Join-Path $distributionRootPath "LICENSE.txt"
$iconPath = Join-Path $repoRoot "src\PLCSIM.UnitTest.App.Wpf\Assets\PLCSIM.UnitTest.ico"
$wizardImagePath = Join-Path $repoRoot "installer\windows\assets\PLCSIM.UnitTest.Wizard.png"
$wizardSmallImagePath = Join-Path $repoRoot "installer\windows\assets\PLCSIM.UnitTest.WizardSmall.png"
$bundleManifestPath = Join-Path $distributionRootPath "distribution\bundle-manifest.json"
$utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)

function Assert-FileExists {
    param([string]$Path, [string]$Label)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label '$Path' does not exist."
    }
}

function Assert-SemanticVersion {
    param([string]$Value)

    if ($Value -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
        throw "Distribution version '$Value' is not a normal semantic version."
    }
}

function Remove-VerifiedDirectory {
    param([string]$Path, [string]$RequiredParent)

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $resolvedParent = [System.IO.Path]::GetFullPath($RequiredParent).TrimEnd('\') + '\'
    if (-not $resolvedPath.StartsWith($resolvedParent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove '$resolvedPath' because it is outside '$resolvedParent'."
    }
    if (Test-Path -LiteralPath $resolvedPath) {
        Remove-Item -LiteralPath $resolvedPath -Recurse -Force
    }
}

Assert-FileExists $InnoCompilerPath "Inno Setup compiler"
Assert-FileExists $definitionPath "Inno Setup definition"
Assert-FileExists $compositionGeneratorPath "Installer composition generator"
Assert-FileExists $licensePath "License file"
Assert-FileExists $iconPath "Setup icon"
Assert-FileExists $wizardImagePath "Setup wizard image"
Assert-FileExists $wizardSmallImagePath "Setup wizard small image"
Assert-FileExists $bundleManifestPath "Staged bundle manifest"

$bundle = Get-Content -LiteralPath $bundleManifestPath -Raw | ConvertFrom-Json
if ($bundle.schemaVersion -ne 1 -or
    $bundle.distribution.name -ne "plcsim-unittest.windows") {
    throw "The staged bundle is not a supported PLCSIM.UnitTest Windows distribution."
}
$distributionVersion = [string]$bundle.distribution.version
Assert-SemanticVersion $distributionVersion
$versionMatch = [regex]::Match($distributionVersion, '^(?<major>[0-9]+)\.(?<minor>[0-9]+)\.(?<patch>[0-9]+)')
$windowsVersion = "$($versionMatch.Groups['major'].Value).$($versionMatch.Groups['minor'].Value).$($versionMatch.Groups['patch'].Value).0"

$expectedFiles = @(
    "PLCSIM.UnitTest.App.Wpf.exe",
    "PLCSIM.UnitTest.App.Cli.exe",
    "Help\index.html",
    "LICENSE.txt",
    "THIRD-PARTY-NOTICES.txt",
    "distribution\THIRD-PARTY-NOTICES.txt",
    "Examples\CI\README.md",
    "distribution\packages\$($bundle.application.package)\package-manifest.json"
)
foreach ($relativePath in $expectedFiles) {
    Assert-FileExists (Join-Path $distributionRootPath $relativePath) "Required distribution file"
}

$distributionPrefix = $distributionRootPath.TrimEnd('\') + '\'
$forbiddenFiles = @(Get-ChildItem -LiteralPath $distributionRootPath -Recurse -File | Where-Object {
    $relativePath = $_.FullName.Substring($distributionPrefix.Length).Replace('\', '/')
    $segments = @($relativePath.Split('/') | ForEach-Object { $_.ToLowerInvariant() })
    $segments -contains "cache" -or
        $segments -contains "logs" -or
        $segments -contains ".tmp" -or
        $_.Extension.ToLowerInvariant() -in @(".pdb", ".log", ".trx", ".cache", ".user") -or
        $_.Name -match '(?i)(\.Tests?\.|\.Fake\.|ValidationWorker|PerformanceMeasurementWorker|Siemens\.Engineering\.dll|Siemens\.Simatic\.Simulation\.Runtime\.Api)'
})
if ($forbiddenFiles.Count -ne 0) {
    $relativeForbiddenFiles = $forbiddenFiles | ForEach-Object {
        $_.FullName.Substring($distributionPrefix.Length).Replace('\', '/')
    }
    throw "Distribution staging contains forbidden installer input: $($relativeForbiddenFiles -join ', ')."
}

$outputDirectory = Join-Path (Join-Path $outputRootPath ([string]$bundle.distribution.name)) $distributionVersion
if (Test-Path -LiteralPath $outputDirectory) {
    throw "Installer output '$outputDirectory' already exists."
}
New-Item -ItemType Directory -Path $outputRootPath -Force | Out-Null
$stagingDirectory = Join-Path $outputRootPath (".staging-{0}-{1}" -f $PID, [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null

try {
    $generatedComponentsPath = Join-Path $stagingDirectory "generated-components.iss"
    $generatedFilesPath = Join-Path $stagingDirectory "generated-files.iss"
    & $compositionGeneratorPath `
        -DistributionRoot $distributionRootPath `
        -ComponentsFile $generatedComponentsPath `
        -FilesFile $generatedFilesPath

    $arguments = @(
        "/DSourceRoot=$distributionRootPath",
        "/DDistributionVersion=$distributionVersion",
        "/DWindowsVersion=$windowsVersion",
        "/DOutputDirectory=$stagingDirectory",
        "/DLicenseFile=$licensePath",
        "/DSetupIconFile=$iconPath",
        "/DWizardImageFile=$wizardImagePath",
        "/DWizardSmallImageFile=$wizardSmallImagePath",
        "/DGeneratedComponentsFile=$generatedComponentsPath",
        "/DGeneratedFilesFile=$generatedFilesPath",
        $definitionPath
    )
    & $InnoCompilerPath @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup compiler failed with exit code $LASTEXITCODE."
    }
    Remove-Item -LiteralPath $generatedComponentsPath, $generatedFilesPath -Force

    $installerName = "PLCSIM.UnitTest-Setup-$distributionVersion.exe"
    $installerPath = Join-Path $stagingDirectory $installerName
    $publishedBundleManifestPath = Join-Path $stagingDirectory "bundle-manifest.json"
    Assert-FileExists $installerPath "Compiled installer"
    Copy-Item -LiteralPath $bundleManifestPath -Destination $publishedBundleManifestPath

    $checksumLines = @(
        "{0}  {1}" -f (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant(), $installerName
        "{0}  bundle-manifest.json" -f (Get-FileHash -LiteralPath $publishedBundleManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    )
    [System.IO.File]::WriteAllText(
        (Join-Path $stagingDirectory "SHA256SUMS"),
        ($checksumLines -join "`n") + "`n",
        $utf8WithoutBom)

    New-Item -ItemType Directory -Path (Split-Path -Parent $outputDirectory) -Force | Out-Null
    [System.IO.Directory]::Move($stagingDirectory, $outputDirectory)
    Write-Host "Windows installer created at '$outputDirectory'."
}
finally {
    if (Test-Path -LiteralPath $stagingDirectory) {
        Remove-VerifiedDirectory $stagingDirectory $outputRootPath
    }
}
