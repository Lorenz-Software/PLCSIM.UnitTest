param(
    [ValidateSet("Sync", "Verify")]
    [string]$Mode = "Verify"
)

$ErrorActionPreference = "Stop"

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$contractArtifactsDirectory = Join-Path $repoRoot "lib\contracts"
$contracts = @(
    @{
        Project = "src\PLCSIM.UnitTest.Common\PLCSIM.UnitTest.Common.csproj"
        Output = "src\PLCSIM.UnitTest.Common\bin\Release\netstandard2.0"
        Assembly = "PLCSIM.UnitTest.Common"
    },
    @{
        Project = "src\PLCSIM.UnitTest.Common.Application\PLCSIM.UnitTest.Common.Application.csproj"
        Output = "src\PLCSIM.UnitTest.Common.Application\bin\Release\net8.0"
        Assembly = "PLCSIM.UnitTest.Common.Application"
    },
    @{
        Project = "access-profiles\siemens\src\PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts\PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts.csproj"
        Output = "access-profiles\siemens\src\PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts\bin\Release\netstandard2.0"
        Assembly = "PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts"
    }
)

# Portable PDB checksums include the source bytes, including line endings.
# Git enforces LF on checkout; fail early for manually copied/edited CRLF input.
foreach ($contract in $contracts) {
    $projectDirectory = Split-Path -Parent (Join-Path $repoRoot $contract.Project)
    foreach ($source in Get-ChildItem -LiteralPath $projectDirectory -Filter '*.cs' -File -Recurse) {
        $relativePath = $source.FullName.Substring($projectDirectory.Length + 1)
        if ($relativePath -match '(^|[\\/])(bin|obj)[\\/]') {
            continue
        }
        if ([System.IO.File]::ReadAllText($source.FullName).Contains("`r")) {
            throw "Contract source '$($source.FullName)' must use LF line endings as required by .gitattributes. Convert it to LF before $Mode; canonical artifacts were not changed."
        }
    }
}

function Invoke-DotNet {
    param([string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed with exit code $LASTEXITCODE."
    }
}

function Assert-FileMatches {
    param(
        [string]$ExpectedPath,
        [string]$ActualPath
    )

    if (-not (Test-Path -LiteralPath $ExpectedPath)) {
        throw "Expected contract artifact '$ExpectedPath' does not exist."
    }

    if (-not (Test-Path -LiteralPath $ActualPath)) {
        throw "Canonical contract artifact '$ActualPath' does not exist."
    }

    $expectedHash = (Get-FileHash -LiteralPath $ExpectedPath -Algorithm SHA256).Hash
    $actualHash = (Get-FileHash -LiteralPath $ActualPath -Algorithm SHA256).Hash
    if ($expectedHash -ne $actualHash) {
        throw "Contract artifact '$ActualPath' differs from the Release build. Check source changes, LF line endings and the .NET SDK/toolchain before deliberately running '.\tools\contract-artifacts.ps1 -Mode Sync'."
    }
}

foreach ($contract in $contracts) {
    $projectPath = Join-Path $repoRoot $contract.Project
    if ($Mode -eq "Sync") {
        Invoke-DotNet @(
            "build",
            $projectPath,
            "--configuration",
            "Release",
            "-t:DeployContractArtifact"
        )
    }
    else {
        Invoke-DotNet @(
            "build",
            $projectPath,
            "--configuration",
            "Release"
        )
    }
}

if ($Mode -eq "Verify") {
    foreach ($contract in $contracts) {
        foreach ($extension in @(".dll", ".pdb")) {
            $fileName = $contract.Assembly + $extension
            Assert-FileMatches `
                (Join-Path (Join-Path $repoRoot $contract.Output) $fileName) `
                (Join-Path $contractArtifactsDirectory $fileName)
        }
    }
}

Write-Host "Contract artifacts $($Mode.ToLowerInvariant()) completed."
