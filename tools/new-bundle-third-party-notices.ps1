[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DistributionRoot,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath($DistributionRoot).TrimEnd('\')
$bundle = Get-Content -LiteralPath (Join-Path $root 'distribution\bundle-manifest.json') -Raw | ConvertFrom-Json
$outputFile = [IO.Path]::GetFullPath($OutputPath)
$parts = [Collections.Generic.List[string]]::new()
$parts.Add('PLCSIM.UnitTest distribution third-party notices')
$parts.Add('Includes all bundled selectable components, whether selected during installation or not.')
foreach ($pin in @($bundle.application) + @($bundle.components)) {
    $packageRoot = [IO.Path]::GetFullPath((Join-Path $root ([string]$pin.destination)))
    if ($packageRoot -ne $root -and -not $packageRoot.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Package destination is outside the distribution root."
    }
    foreach ($required in @('LICENSE.txt', 'THIRD-PARTY-NOTICES.txt')) {
        $path = Join-Path $packageRoot $required
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
            throw "Package '$($pin.package)' is missing a nonempty $required."
        }
    }
    $parts.Add("Package: $($pin.package) $($pin.version)")
    $parts.Add([IO.File]::ReadAllText((Join-Path $packageRoot 'THIRD-PARTY-NOTICES.txt')).Trim())
}

# Preserve notices from the pinned packages, including independently built plugins.
$installerNotice = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N') + '.txt')
try {
    & (Join-Path $PSScriptRoot 'new-third-party-notices.ps1') -IncludeInstallerComponents -OutputPath $installerNotice
    $parts.Add([IO.File]::ReadAllText($installerNotice).Trim())
}
finally {
    if (Test-Path -LiteralPath $installerNotice) { Remove-Item -LiteralPath $installerNotice }
}
New-Item -ItemType Directory -Path (Split-Path -Parent $outputFile) -Force | Out-Null
$text = ($parts -join "`n`n") -replace "`r`n", "`n"
[IO.File]::WriteAllText($outputFile, $text + "`n", [Text.UTF8Encoding]::new($false))
