<#
.SYNOPSIS
Authorizes deployed real TIA Openness binding workers for unattended use.
.DESCRIPTION
Invokes the package-owned authorization probe. Without -Path or -All, an
interactive list is shown. The script does not elevate or edit the AllowList.
.PARAMETER Path
One or more binding package directories containing manifest.json.
.PARAMETER All
Process all deployed real TIA Openness bindings advertising the probe.
.PARAMETER Help
Show usage.
.EXAMPLE
.\setup-siemens-openness.ps1 -p .\lib\bindings\tia-openness-v21
.EXAMPLE
.\setup-siemens-openness.ps1 -All
#>

#requires -Version 7.0
[CmdletBinding()]
param(
    [Alias("p")][string[]]$Path,
    [switch]$All,
    [Alias("h")][switch]$Help
)

$ErrorActionPreference = "Stop"
$exitCodes = @{
    Authorized=0; InvalidArguments=10; InvalidSelection=20; Unavailable=21
    Denied=30; Failed=30; TimedOut=31; CleanupFailed=50; UnexpectedFailure=99
}

function Show-Usage {
    Write-Host "Usage:"
    Write-Host "  .\setup-siemens-openness.ps1 -p <package-directory>[,<package-directory>...]"
    Write-Host "  .\setup-siemens-openness.ps1 -Path <package-directory>[,<package-directory>...]"
    Write-Host "  .\setup-siemens-openness.ps1 -All"
    Write-Host "  .\setup-siemens-openness.ps1 -h | -Help"
    Write-Host "Without -Path or -All, an interactive package selection is shown."
}

function Read-PackageDescriptor {
    param([Parameter(Mandatory)][string]$PackagePath)

    $root = [IO.Path]::GetFullPath($PackagePath)
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw "Binding package directory '$root' does not exist."
    }
    $manifestPath = Join-Path $root "manifest.json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Binding package '$root' does not contain manifest.json."
    }
    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    } catch {
        throw "Binding package manifest '$manifestPath' is invalid: $($_.Exception.Message)"
    }
    if ($manifest.schemaVersion -ne 1) {
        throw "Binding package manifest '$manifestPath' has an unsupported schema version."
    }
    if ($manifest.info.bindingKind -ne "tia-openness") {
        throw "Binding package '$root' is not a TIA Openness binding."
    }
    if ($manifest.setup.tiaOpennessAuthorizationProbe -ne $true) {
        throw "Binding package '$root' does not advertise authorization probing."
    }

    $assembly = [string]$manifest.entryPoint.assembly
    $typeName = [string]$manifest.entryPoint.typeName
    if ([string]::IsNullOrWhiteSpace($assembly) -or [string]::IsNullOrWhiteSpace($typeName)) {
        throw "Binding package manifest '$manifestPath' has no usable entry point."
    }
    if ([IO.Path]::IsPathRooted($assembly)) {
        throw "Binding package manifest '$manifestPath' defines an absolute entry assembly."
    }
    $assemblyPath = [IO.Path]::GetFullPath((Join-Path $root $assembly))
    $relative = [IO.Path]::GetRelativePath($root, $assemblyPath)
    if ([IO.Path]::IsPathRooted($relative) -or $relative -eq ".." -or
        $relative.StartsWith("..$([IO.Path]::DirectorySeparatorChar)")) {
        throw "Binding entry assembly is outside package '$root'."
    }
    if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
        throw "Binding package entry assembly '$assemblyPath' does not exist."
    }

    [pscustomobject]@{
        PackagePath=$root
        DisplayName=[string]$manifest.info.displayName
        BindingVersion=[string]$manifest.info.bindingVersion
        EntryAssemblyPath=$assemblyPath
        EntryType=$typeName
    }
}

function Get-DeployedPackages {
    $root = Join-Path $PSScriptRoot "lib\bindings"
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { return @() }
    $result = @()
    foreach ($directory in Get-ChildItem -LiteralPath $root -Directory) {
        try { $result += Read-PackageDescriptor $directory.FullName } catch { }
    }
    @($result | Sort-Object DisplayName,BindingVersion,PackagePath)
}

function Select-InteractivePackages {
    param([Parameter(Mandatory)][object[]]$Candidates)
    if ($Candidates.Count -eq 0) {
        throw "No deployed real TIA Openness binding advertises authorization probing."
    }
    Write-Host "Available TIA Openness binding packages:"
    for ($index=0; $index -lt $Candidates.Count; $index++) {
        $candidate=$Candidates[$index]
        Write-Host ("  [{0}] {1} ({2})" -f ($index+1),$candidate.DisplayName,$candidate.BindingVersion)
        Write-Host ("      {0}" -f $candidate.PackagePath)
    }
    $selection=Read-Host "Select package numbers (comma-separated)"
    if ([string]::IsNullOrWhiteSpace($selection)) { throw "No package was selected." }
    $result=@()
    foreach ($token in $selection.Split(",")) {
        $number=0
        if (-not [int]::TryParse($token.Trim(),[ref]$number) -or
            $number -lt 1 -or $number -gt $Candidates.Count) {
            throw "Interactive selection '$($token.Trim())' is invalid."
        }
        $candidate=$Candidates[$number-1]
        if (-not ($result | Where-Object PackagePath -eq $candidate.PackagePath)) {
            $result += $candidate
        }
    }
    $result
}

function Invoke-AuthorizationProbe {
    param([Parameter(Mandatory)]$Package,[Parameter(Mandatory)][Type]$RunnerType)

    Write-Host ("Probing {0} ({1})..." -f $Package.DisplayName,$Package.BindingVersion)
    $localData=[Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    $logDirectory=Join-Path $localData "PLCSIM.UnitTest\logs"
    try {
        $method=$RunnerType.GetMethod("ProbeAsync",
            [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::Static)
        if ($null -eq $method) { throw "Authorization setup runner method was not found." }
        $arguments = [object[]]::new(5)
        $arguments[0] = [string]$Package.PackagePath
        $arguments[1] = [string]$Package.EntryAssemblyPath
        $arguments[2] = [string]$Package.EntryType
        $arguments[3] = [string]$logDirectory
        $arguments[4] = [Threading.CancellationToken]::None
        $task=$method.Invoke($null,$arguments)
        $result=$task.GetAwaiter().GetResult()
        $status=$result.Status.ToString()
        foreach ($diagnostic in $result.Diagnostics) {
            Write-Host ("  [{0}] {1}: {2}" -f
                $diagnostic.Severity,$diagnostic.Code,$diagnostic.Message)
        }
        foreach ($evidence in $result.Evidence) {
            Write-Verbose ("{0}: {1}" -f $evidence.Kind,$evidence.Summary)
        }
        Write-Host ("  Result: {0}" -f $status)
        [pscustomobject]@{
            Status=$status
            ExitCode=if ($exitCodes.ContainsKey($status)) {
                $exitCodes[$status]
            } else {
                $exitCodes.UnexpectedFailure
            }
        }
    } catch {
        $failure=$_.Exception
        if ($failure.InnerException) { $failure=$failure.InnerException }
        Write-Host ("  [Error] {0}" -f $failure.GetBaseException().Message)
        [pscustomobject]@{ Status="Failed"; ExitCode=$exitCodes.UnexpectedFailure }
    }
}

if ($Help) { Show-Usage; exit 0 }
if ($All -and $Path.Count -gt 0) {
    [Console]::Error.WriteLine("-Path and -All are mutually exclusive.")
    Show-Usage
    exit $exitCodes.InvalidArguments
}

try {
    if ($Path.Count -gt 0) {
        $packages=@()
        foreach ($packagePath in $Path) {
            $packages += Read-PackageDescriptor $packagePath
        }
    } else {
        $candidates=@(Get-DeployedPackages)
        $packages=if ($All) { $candidates } else {
            @(Select-InteractivePackages $candidates)
        }
    }
    if ($packages.Count -eq 0) { throw "No TIA Openness binding package was selected." }
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit $exitCodes.InvalidSelection
}

try {
    $assemblyPath=Join-Path $PSScriptRoot "PLCSIM.UnitTest.AccessProfiles.Siemens.dll"
    if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
        throw "Siemens access-profile assembly '$assemblyPath' does not exist."
    }
    $assembly=[Reflection.Assembly]::LoadFrom($assemblyPath)
    $runner=$assembly.GetType(
        "PLCSIM.UnitTest.AccessProfiles.Siemens.SiemensTiaOpennessAuthorizationSetupRunner",
        $true,$false)
    $results=@()
    foreach ($package in $packages) {
        $results += Invoke-AuthorizationProbe $package $runner
    }
    $exitCode=($results | Measure-Object -Property ExitCode -Maximum).Maximum
    exit [int]$exitCode
} catch {
    [Console]::Error.WriteLine($_.Exception.GetBaseException().Message)
    exit $exitCodes.UnexpectedFailure
}
