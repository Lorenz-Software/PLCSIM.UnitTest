[CmdletBinding()]
param(
    [ValidateSet("Polling", "PlcCycle")]
    [string]$ObservationStrategy = "Polling",

    [ValidateRange(1, [int]::MaxValue)]
    [int]$MonitoringIntervalMs = 10,

    [ValidateRange(1, [int]::MaxValue)]
    [int]$DrainIntervalMs = 1,

    [ValidateRange(1, 255)]
    [int]$MaxEventsPerBatch = 32,

    [ValidateRange(1, [int]::MaxValue)]
    [int]$Seed = 122,

    [string]$OutputPath = ".tmp\issue-122\abort-lifecycle-validation.json",

    [switch]$UnthrottledPlcCycle,

    [switch]$RawEventLogging,

    [switch]$PlcProgramPrepared,

    [switch]$ExclusiveCommunicationConfirmed
)

$ErrorActionPreference = "Stop"

if (-not $PlcProgramPrepared) {
    throw "Confirm that the UnitTest PLC program is downloaded by passing -PlcProgramPrepared."
}

if (-not $ExclusiveCommunicationConfirmed) {
    throw "Confirm that WPF, CLI, TIA online observation, and other PLCSIM clients are disconnected by passing -ExclusiveCommunicationConfirmed."
}

if ($UnthrottledPlcCycle -and $ObservationStrategy -ne "PlcCycle") {
    throw "-UnthrottledPlcCycle requires -ObservationStrategy PlcCycle."
}

$testsDirectory = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $testsDirectory "PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.PlcsimAdvanced.V8.csproj"
$fullOutputPath = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $OutputPath))
$outputDirectory = Split-Path -Parent $fullOutputPath
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null

$variables = @{
    PLCSIM_UNITTEST_VALIDATE_PLCSIM_ADVANCED_V8_ABORT = "1"
    PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_ABORT_SEED = $Seed.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_ABORT_OUTPUT = $fullOutputPath
    PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_OBSERVATION_STRATEGY = $ObservationStrategy
    PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_DRAIN_INTERVAL_MS = $DrainIntervalMs.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_MAX_EVENTS_PER_BATCH = $MaxEventsPerBatch.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_RAW_EVENT_LOGGING = $(if ($RawEventLogging) { "1" } else { "0" })
}

if (-not $UnthrottledPlcCycle) {
    $variables.PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_MONITORING_INTERVAL_MS = $MonitoringIntervalMs.ToString([System.Globalization.CultureInfo]::InvariantCulture)
}

$previous = @{}
try {
    foreach ($entry in $variables.GetEnumerator()) {
        $previous[$entry.Key] = [System.Environment]::GetEnvironmentVariable($entry.Key, "Process")
        [System.Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, "Process")
    }

    if ($UnthrottledPlcCycle) {
        $name = "PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_MONITORING_INTERVAL_MS"
        $previous[$name] = [System.Environment]::GetEnvironmentVariable($name, "Process")
        [System.Environment]::SetEnvironmentVariable($name, $null, "Process")
    }

    dotnet test $projectPath `
        --configuration Release `
        --nologo `
        --filter "FullyQualifiedName~PlcsimAdvancedV8AbortRealRuntimeTests"
    if ($LASTEXITCODE -ne 0) {
        throw "Abort lifecycle validation failed with exit code $LASTEXITCODE. Evidence: $fullOutputPath"
    }
}
finally {
    foreach ($entry in $previous.GetEnumerator()) {
        [System.Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, "Process")
    }
}

Write-Host "Abort lifecycle validation completed: $fullOutputPath"
