[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [int]$ConfiguredPlcMinimumCycleTimeMs,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$WorkloadId,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [switch]$PlcProgramPrepared,

    [switch]$ExclusiveCommunicationConfirmed
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $PlcProgramPrepared) {
    throw 'Confirm that the PLC program is prepared by passing -PlcProgramPrepared.'
}

if (-not $ExclusiveCommunicationConfirmed) {
    throw 'Confirm that TIA Portal is offline and no WPF/CLI client communicates with the runtime by passing -ExclusiveCommunicationConfirmed.'
}

$testProject = Join-Path $PSScriptRoot '..\PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.PlcsimAdvanced.V8.csproj'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..\..\..'))
$outputRoot = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    [System.IO.Path]::GetFullPath($OutputDirectory)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
}
$testFilter = 'FullyQualifiedName=PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.PlcsimAdvanced.V8.PlcsimAdvancedV8RealRuntimeTests.V8_binding_can_validate_real_existing_runtime_unit_test_communication_when_enabled'
$scenarios = @(
    [pscustomobject]@{
        Name = 'polling-010ms'
        Strategy = 'Polling'
        MonitoringIntervalMs = 10
    },
    [pscustomobject]@{
        Name = 'plccycle-unthrottled'
        Strategy = 'PlcCycle'
        MonitoringIntervalMs = $null
    },
    [pscustomobject]@{
        Name = 'plccycle-010ms'
        Strategy = 'PlcCycle'
        MonitoringIntervalMs = 10
    }
)
$environmentNames = @(
    'PLCSIM_UNITTEST_VALIDATE_PLCSIM_ADVANCED_V8_COMMUNICATION',
    'PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_CAPACITY_MEASUREMENT',
    'PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_CAPACITY_RUN_COUNT',
    'PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_CAPACITY_WORKLOAD_ID',
    'PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_CAPACITY_MEASUREMENT_ROLE',
    'PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_OBSERVATION_STRATEGY',
    'PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_MONITORING_INTERVAL_MS',
    'PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_DRAIN_INTERVAL_MS',
    'PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_MAX_EVENTS_PER_BATCH',
    'PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_RAW_EVENT_LOGGING',
    'PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_CONFIGURED_PLC_MINIMUM_CYCLE_TIME_MS',
    'PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_CAPACITY_OUTPUT',
    'PLCSIM_UNITTEST_SOURCE_REVISION'
)

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$sourceRevision = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) {
    throw 'Could not determine the source revision.'
}

Write-Host 'Building the V8 test project and worker in Release configuration...'
& dotnet build $testProject -c Release
if ($LASTEXITCODE -ne 0) {
    throw "Release build failed with exit code $LASTEXITCODE."
}

$originalEnvironment = @{}
foreach ($name in $environmentNames) {
    $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

try {
    $env:PLCSIM_UNITTEST_VALIDATE_PLCSIM_ADVANCED_V8_COMMUNICATION = '1'
    $env:PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_CAPACITY_MEASUREMENT = '1'
    $env:PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_CAPACITY_RUN_COUNT = '10'
    $env:PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_CAPACITY_WORKLOAD_ID = $WorkloadId
    $env:PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_CAPACITY_MEASUREMENT_ROLE = 'StrategyComparison'
    $env:PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_DRAIN_INTERVAL_MS = '1'
    $env:PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_MAX_EVENTS_PER_BATCH = '32'
    $env:PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_RAW_EVENT_LOGGING = '0'
    $env:PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_CONFIGURED_PLC_MINIMUM_CYCLE_TIME_MS = $ConfiguredPlcMinimumCycleTimeMs.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:PLCSIM_UNITTEST_SOURCE_REVISION = $sourceRevision

    foreach ($scenario in $scenarios) {
        $pointPath = Join-Path $outputRoot ($scenario.Name + '.json')
        $env:PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_OBSERVATION_STRATEGY = $scenario.Strategy
        if ($null -eq $scenario.MonitoringIntervalMs) {
            Remove-Item Env:PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_MONITORING_INTERVAL_MS -ErrorAction SilentlyContinue
        }
        else {
            $env:PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_MONITORING_INTERVAL_MS = $scenario.MonitoringIntervalMs.ToString([Globalization.CultureInfo]::InvariantCulture)
        }
        $env:PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_CAPACITY_OUTPUT = $pointPath

        $intervalLabel = if ($null -eq $scenario.MonitoringIntervalMs) { 'every PLC cycle' } else { "$($scenario.MonitoringIntervalMs) ms" }
        Write-Host "Running ten real-runtime runs with $($scenario.Strategy) observation at $intervalLabel..."
        & dotnet test $testProject -c Release --no-build --filter $testFilter
        if ($LASTEXITCODE -ne 0) {
            throw "$($scenario.Name) comparison run failed with exit code $LASTEXITCODE."
        }
        if (-not (Test-Path -LiteralPath $pointPath)) {
            throw "$($scenario.Name) comparison run did not produce '$pointPath'."
        }
    }
}
finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name], 'Process')
    }
}

$points = foreach ($scenario in $scenarios) {
    Get-Content -LiteralPath (Join-Path $outputRoot ($scenario.Name + '.json')) -Raw | ConvertFrom-Json
}
$comparison = [ordered]@{
    schemaVersion = 2
    recordedAtUtc = [DateTimeOffset]::UtcNow
    sourceRevision = $sourceRevision
    measurementConditions = [ordered]@{
        buildConfiguration = 'Release'
        drainIntervalMs = 1
        maxEventsPerBatch = 32
        rawEventLoggingEnabled = $false
        configuredPlcMinimumCycleTimeMs = $ConfiguredPlcMinimumCycleTimeMs
        workloadId = $WorkloadId
        samePlcProgram = $true
        tiaPortalOffline = $true
        noParallelWpfOrCliCommunication = $true
    }
    strategies = @($points)
}
$comparisonPath = Join-Path $outputRoot 'observation-strategy-comparison.json'
$comparison | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $comparisonPath -Encoding utf8
Write-Host "Observation strategy comparison written: $comparisonPath"
