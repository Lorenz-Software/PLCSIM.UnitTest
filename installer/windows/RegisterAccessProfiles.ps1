[CmdletBinding()]
param(
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$cliPath = Join-Path $PSScriptRoot "PLCSIM.UnitTest.App.Cli.exe"

function Wait-ForAcknowledgement {
    if (-not $NoPause) {
        Write-Host
        [void](Read-Host "Press Enter to close")
    }
}

if (-not (Test-Path -LiteralPath $cliPath -PathType Leaf)) {
    [Console]::Error.WriteLine("PLCSIM.UnitTest CLI was not found at '$cliPath'.")
    Wait-ForAcknowledgement
    exit 1
}

Write-Host "Registering installed PLCSIM.UnitTest access profiles..."
& $cliPath register-profiles
$exitCode = $LASTEXITCODE
if ($null -eq $exitCode) {
    $exitCode = 1
}

if ($exitCode -eq 0) {
    Write-Host "Access-profile registration completed."
}
else {
    [Console]::Error.WriteLine("Access-profile registration failed with exit code $exitCode.")
}

Wait-ForAcknowledgement
exit $exitCode
