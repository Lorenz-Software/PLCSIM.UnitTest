$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Assert($condition, $message) {
    if (-not $condition) { throw $message }
}

$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'test-windows-release.ps1'), [ref]$tokens, [ref]$errors)
Assert ($errors.Count -eq 0) 'Release verifier does not parse.'

# Exercise the production helpers without running registration or an installer.
foreach ($name in @('Assert-Condition', 'Get-ProfileDataDirectory', 'Get-SmokeProfiles', 'Remove-SmokeProfileData')) {
    $function = $ast.Find({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    Assert ($null -ne $function) "Missing function: $name"
    . ([scriptblock]::Create($function.Extent.Text))
}

$fixtureRoot = Join-Path $repoRoot ('.tmp\release-profile-smoke-' + [Guid]::NewGuid().ToString('N'))
$oldLocalAppData = $env:LOCALAPPDATA
try {
    $env:LOCALAPPDATA = Join-Path $fixtureRoot 'user-data'
    $layoutPath = Join-Path $fixtureRoot 'layout'
    $components = @(
        [pscustomobject]@{ package = 'access-profiles.siemens'; destination = 'profiles/siemens'; kind = 'profile'; id = 'siemens'; version = '2.0.0' }
        [pscustomobject]@{ package = 'access-profiles.beckhoff-tc3-4026'; destination = 'profiles/beckhoff-tc3-4026'; kind = 'profile'; id = 'beckhoff-tc3-4026'; version = '1.0.0' }
        [pscustomobject]@{ package = 'plugins.siemens.tia-openness.v21'; destination = 'profiles/siemens/lib/bindings/tia-openness-v21'; kind = 'plugin'; id = ''; version = '2.0.0' }
    )
    foreach ($component in $components) {
        $inventoryRoot = Join-Path $layoutPath "distribution\packages\$($component.package)"
        New-Item -ItemType Directory -Path $inventoryRoot -Force | Out-Null
        @{ package = @{ kind = $component.kind } } | ConvertTo-Json -Depth 3 |
            Set-Content -LiteralPath (Join-Path $inventoryRoot 'package-manifest.json') -Encoding UTF8
        if ($component.kind -eq 'profile') {
            $profileRoot = Join-Path $layoutPath $component.destination
            New-Item -ItemType Directory -Path $profileRoot -Force | Out-Null
            @{ profile = @{ id = $component.id; version = $component.version } } | ConvertTo-Json -Depth 3 |
                Set-Content -LiteralPath (Join-Path $profileRoot 'manifest.json') -Encoding UTF8
        }
    }

    $bundle = [pscustomobject]@{ components = $components }
    $profiles = @(Get-SmokeProfiles $bundle $layoutPath)
    Assert ($profiles.Count -eq 2) 'Plugin incorrectly counted as profile.'
    Assert ($profiles[0].Id -eq 'siemens' -and $profiles[0].Version -eq '2.0.0') 'Siemens release identity was not read from manifest.'
    Assert ($profiles[1].Id -eq 'beckhoff-tc3-4026') 'Beckhoff profile missing.'
    Assert (@($profiles | Where-Object { $_.DataDirectoryExisted }).Count -eq 0) 'Fresh fixture is not isolated.'
    Assert (@(Get-SmokeProfiles ([pscustomobject]@{ components = @() }) $layoutPath).Count -eq 0) 'Empty composition failed.'
    Assert (@(Get-SmokeProfiles ([pscustomobject]@{ components = @($components[1]) }) $layoutPath).Count -eq 1) 'Beckhoff-only composition failed.'

    # Model registration stopping after only the first profile wrote state.
    New-Item -ItemType Directory -Path $profiles[0].DataDirectory -Force | Out-Null
    Remove-SmokeProfileData $profiles $layoutPath
    Assert (-not (Test-Path -LiteralPath $profiles[0].DataDirectory)) 'Partial-registration state was retained.'

    New-Item -ItemType Directory -Path $profiles[0].DataDirectory -Force | Out-Null
    $sentinel = Join-Path $profiles[0].DataDirectory 'user-owned.txt'
    Set-Content -LiteralPath $sentinel -Value 'keep' -Encoding UTF8
    $profiles = @(Get-SmokeProfiles $bundle $layoutPath)
    Assert $profiles[0].DataDirectoryExisted 'Existing state was not detected.'
    New-Item -ItemType Directory -Path $profiles[1].DataDirectory -Force | Out-Null
    Remove-SmokeProfileData $profiles $layoutPath
    Assert ((Get-Content -LiteralPath $sentinel -Raw).Trim() -eq 'keep') 'Existing state changed.'
    Assert (-not (Test-Path -LiteralPath $profiles[1].DataDirectory)) 'New Beckhoff state was retained.'

    $profiles[1].DataDirectory = Join-Path $fixtureRoot 'unrelated'
    New-Item -ItemType Directory -Path $profiles[1].DataDirectory | Out-Null
    $rejected = $false
    try { Remove-SmokeProfileData @($profiles[1]) $layoutPath } catch { $rejected = $true }
    Assert $rejected 'Unexpected data path accepted for recursive cleanup.'
    Assert (Test-Path -LiteralPath $profiles[1].DataDirectory) 'Unrelated path deleted.'
    $profiles[1].Path = Join-Path $fixtureRoot 'foreign-package'
    $profiles[1].DataDirectory = Get-ProfileDataDirectory $profiles[1].Id $profiles[1].Version $profiles[1].Path
    $rejected = $false
    try { Remove-SmokeProfileData @($profiles[1]) $layoutPath } catch { $rejected = $true }
    Assert $rejected 'Foreign package accepted for cleanup.'

    Write-Host 'PASS: combined/Beckhoff-only/empty composition, manifest versions, partial registration, existing-state preservation and cleanup path containment.'
}
finally {
    $env:LOCALAPPDATA = $oldLocalAppData
    $resolvedRoot = [System.IO.Path]::GetFullPath($fixtureRoot)
    $allowedPrefix = [System.IO.Path]::GetFullPath((Join-Path $repoRoot '.tmp')).TrimEnd('\') + '\'
    if (-not $resolvedRoot.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing fixture cleanup outside '$allowedPrefix'."
    }
    if (Test-Path -LiteralPath $resolvedRoot) {
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
    }
}
