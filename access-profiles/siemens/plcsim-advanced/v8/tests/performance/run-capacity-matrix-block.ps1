[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet(1, 5, 10, 15)]
    [int]$ConfiguredPlcMinimumCycleTimeMs,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$WorkloadId,

    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 65535)]
    [int]$EventBufferLength,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string]$ExpectedWorkloadFingerprint,

    [switch]$PlcProgramPrepared,

    [switch]$ExclusiveCommunicationConfirmed
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Set-ProcessEnvironmentValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [AllowNull()]
        [string]$Value
    )

    [Environment]::SetEnvironmentVariable($Name, $Value, 'Process')
}

function Get-Sha256TextHash {
    param([Parameter(Mandatory = $true)][string]$Value)

    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
        return ([BitConverter]::ToString($algorithm.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function Get-RunStructuralFingerprint {
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)]$Contracts
    )

    $breakdown = @($Run.eventBreakdown)
    $suiteNames = @($breakdown |
            Where-Object { [int]$_.ownerScope -eq 20 -and -not [string]::IsNullOrWhiteSpace([string]$_.name) } |
            ForEach-Object { [string]$_.name } |
            Sort-Object -Unique)
    $testCaseNames = @($breakdown |
            Where-Object { [int]$_.ownerScope -eq 30 -and -not [string]::IsNullOrWhiteSpace([string]$_.name) } |
            ForEach-Object { [string]$_.name } |
            Sort-Object -Unique)
    if ($suiteNames.Count -eq 0 -or $testCaseNames.Count -eq 0) {
        throw 'A complete workload fingerprint requires observed TestSuite and TestCase event names.'
    }

    $canonical = @(
        "runControlContract=$($Contracts.runControlContractVersion)"
        "runControlFramework=$($Contracts.runControlFrameworkVersion)"
        "eventStreamContract=$($Contracts.eventStreamContractVersion)"
        "eventStreamFramework=$($Contracts.eventStreamFrameworkVersion)"
        'suites=' + ($suiteNames -join '|')
        'testCases=' + ($testCaseNames -join '|')
    ) -join "`n"

    return [pscustomobject]@{
        fingerprint = Get-Sha256TextHash -Value $canonical
        suiteNames = $suiteNames
        testCaseNames = $testCaseNames
    }
}

function Get-ReferenceWorkloadEvidence {
    param(
        [Parameter(Mandatory = $true)]$Evidence,
        [Parameter(Mandatory = $true)][string]$ExpectedWorkloadId,
        [Parameter(Mandatory = $true)][int]$ExpectedEventBufferLength
    )

    if ([string]$Evidence.workloadId -ne $ExpectedWorkloadId -or [string]$Evidence.measurementRole -ne 'Reference') {
        throw 'Reference evidence does not match the requested workload or measurement role.'
    }
    if ($null -eq $Evidence.contracts) {
        throw 'Reference evidence does not contain observed contract versions.'
    }

    $runs = @($Evidence.runs)
    if ($runs.Count -ne 1) {
        throw "Reference evidence must contain exactly one run; observed $($runs.Count)."
    }
    $run = $runs[0]
    if ([string]$run.outcome -ne 'Completed' -or [string]$run.runResult -ne 'Passed' -or [int]$run.maximumLostEvents -ne 0) {
        throw "Reference run must complete with Passed and no event loss; observed outcome=$($run.outcome), result=$($run.runResult), lost=$($run.maximumLostEvents)."
    }

    $structure = Get-RunStructuralFingerprint -Run $run -Contracts $Evidence.contracts
    $eventClassCounts = @{}
    foreach ($entry in @($run.eventBreakdown)) {
        $key = '{0}|{1}' -f [int]$entry.ownerScope, [int]$entry.eventType
        if (-not $eventClassCounts.ContainsKey($key)) {
            $eventClassCounts[$key] = 0
        }
        $eventClassCounts[$key] += [int]$entry.count
    }
    $eventClassDistribution = @($eventClassCounts.Keys |
            Sort-Object |
            ForEach-Object {
                $parts = $_.Split('|')
                [ordered]@{
                    ownerScope = [int]$parts[0]
                    eventType = [int]$parts[1]
                    count = [int]$eventClassCounts[$_]
                }
            })

    return [ordered]@{
        workloadId = $ExpectedWorkloadId
        structuralFingerprint = $structure.fingerprint
        eventBufferLength = $ExpectedEventBufferLength
        eventBufferLengthIsOperatorSupplied = $true
        contracts = $Evidence.contracts
        suiteCount = $structure.suiteNames.Count
        suiteNames = $structure.suiteNames
        testCaseCount = $structure.testCaseNames.Count
        testCaseNames = $structure.testCaseNames
        runControlResult = [string]$run.runResult
        eventCount = [int]$run.eventCount
        eventClassDistribution = $eventClassDistribution
    }
}

function Assert-CapacityPointEvidence {
    param(
        [Parameter(Mandatory = $true)]$Evidence,
        [Parameter(Mandatory = $true)][string]$ExpectedFingerprint,
        [Parameter(Mandatory = $true)][int]$ExpectedRunCount,
        [Parameter(Mandatory = $true)][int]$BacklogRepeatThreshold
    )

    $runs = @($Evidence.runs)
    if ($runs.Count -eq 0) {
        throw 'Capacity point did not produce any run evidence.'
    }

    $capacityLimitObserved = $false
    foreach ($run in $runs) {
        $outcome = [string]$run.outcome
        $runResult = [string]$run.runResult
        $lostEvents = [int]$run.maximumLostEvents
        if ($outcome -eq 'Completed') {
            if ($runResult -ne 'Passed') {
                throw "A completely observed run reported PLC result '$runResult'."
            }
            $fingerprint = (Get-RunStructuralFingerprint -Run $run -Contracts $Evidence.contracts).fingerprint
            if ($fingerprint -ne $ExpectedFingerprint) {
                throw "The observed workload fingerprint '$fingerprint' differs from '$ExpectedFingerprint'."
            }
        }
        elseif ($lostEvents -eq 0) {
            throw "Run outcome '$outcome' is incomplete without event loss and is not a capacity-limit result."
        }

        if ($lostEvents -gt 0 -or [int]$run.maximumObservedBacklog -gt $BacklogRepeatThreshold) {
            $capacityLimitObserved = $true
        }
    }

    if (-not $capacityLimitObserved -and $runs.Count -ne $ExpectedRunCount) {
        throw "Capacity point must contain $ExpectedRunCount complete runs when no capacity limit was observed; found $($runs.Count)."
    }

    return $capacityLimitObserved
}

if (-not $PlcProgramPrepared) {
    throw 'Confirm that the PLC program was downloaded with the selected configured minimum cycle time by passing -PlcProgramPrepared.'
}
if (-not $ExclusiveCommunicationConfirmed) {
    throw 'Confirm that TIA Portal is offline and no WPF/CLI client communicates with the runtime by passing -ExclusiveCommunicationConfirmed.'
}

$testProject = Join-Path $PSScriptRoot '..\PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.PlcsimAdvanced.V8.csproj'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..\..\..'))
$outputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
}
else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
}
$referenceDirectory = Join-Path $outputRoot 'reference'
$pointDirectory = Join-Path $outputRoot 'points'
$trxDirectory = Join-Path $outputRoot 'trx'
$monitoringIntervalsMs = @(5, 10, 25, 50, 100)
$intendedRunCount = 10
$backlogRepeatThreshold = $EventBufferLength - 32
$testFilter = 'FullyQualifiedName=PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.PlcsimAdvanced.V8.PlcsimAdvancedV8RealRuntimeTests.V8_binding_can_validate_real_existing_runtime_unit_test_communication_when_enabled'

if ($backlogRepeatThreshold -lt 0) {
    throw 'EventBufferLength must be at least maxEventsPerBatch (32).'
}
if ((Test-Path -LiteralPath $outputRoot) -and @(Get-ChildItem -LiteralPath $outputRoot -Force).Count -gt 0) {
    throw "OutputDirectory '$outputRoot' is not empty. Use a new directory so stale point evidence cannot enter the block result."
}

New-Item -ItemType Directory -Path $referenceDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $pointDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $trxDirectory -Force | Out-Null

$sourceRevision = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) {
    throw 'Could not determine the source revision.'
}

Write-Host 'Building the V8 test project in Release configuration...'
& dotnet build $testProject -c Release
if ($LASTEXITCODE -ne 0) {
    throw "Release build failed with exit code $LASTEXITCODE."
}

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
$originalEnvironment = @{}
foreach ($name in $environmentNames) {
    $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

function Invoke-CapacityEvidenceRun {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$OutputPath
    )

    Set-ProcessEnvironmentValue -Name 'PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_CAPACITY_OUTPUT' -Value $OutputPath
    Write-Host "Running $Name..."
    & dotnet test $testProject `
        -c Release `
        --no-build `
        --filter $testFilter `
        --results-directory $trxDirectory `
        --logger "trx;LogFileName=$Name.trx" | Out-Host
    $testExitCode = $LASTEXITCODE
    if ($testExitCode -ne 0) {
        throw "$Name failed with exit code $testExitCode."
    }
    if (-not (Test-Path -LiteralPath $OutputPath)) {
        throw "$Name did not produce '$OutputPath'."
    }
    return Get-Content -LiteralPath $OutputPath -Raw | ConvertFrom-Json
}

try {
    Set-ProcessEnvironmentValue -Name 'PLCSIM_UNITTEST_VALIDATE_PLCSIM_ADVANCED_V8_COMMUNICATION' -Value '1'
    Set-ProcessEnvironmentValue -Name 'PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_CAPACITY_MEASUREMENT' -Value '1'
    Set-ProcessEnvironmentValue -Name 'PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_DRAIN_INTERVAL_MS' -Value '1'
    Set-ProcessEnvironmentValue -Name 'PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_MAX_EVENTS_PER_BATCH' -Value '32'
    Set-ProcessEnvironmentValue -Name 'PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_RAW_EVENT_LOGGING' -Value '0'
    Set-ProcessEnvironmentValue -Name 'PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_CONFIGURED_PLC_MINIMUM_CYCLE_TIME_MS' -Value $ConfiguredPlcMinimumCycleTimeMs.ToString([Globalization.CultureInfo]::InvariantCulture)
    Set-ProcessEnvironmentValue -Name 'PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_CAPACITY_WORKLOAD_ID' -Value $WorkloadId
    Set-ProcessEnvironmentValue -Name 'PLCSIM_UNITTEST_SOURCE_REVISION' -Value $sourceRevision

    Set-ProcessEnvironmentValue -Name 'PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_CAPACITY_MEASUREMENT_ROLE' -Value 'Reference'
    Set-ProcessEnvironmentValue -Name 'PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_CAPACITY_RUN_COUNT' -Value '1'
    Set-ProcessEnvironmentValue -Name 'PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_OBSERVATION_STRATEGY' -Value 'Polling'
    Set-ProcessEnvironmentValue -Name 'PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_MONITORING_INTERVAL_MS' -Value '5'
    $referenceName = 'plc-{0:D3}ms-reference-polling-005ms' -f $ConfiguredPlcMinimumCycleTimeMs
    $referencePath = Join-Path $referenceDirectory ($referenceName + '.json')
    $referenceEvidence = Invoke-CapacityEvidenceRun -Name $referenceName -OutputPath $referencePath
    $workloadEvidence = Get-ReferenceWorkloadEvidence `
        -Evidence $referenceEvidence `
        -ExpectedWorkloadId $WorkloadId `
        -ExpectedEventBufferLength $EventBufferLength
    if (-not [string]::IsNullOrWhiteSpace($ExpectedWorkloadFingerprint) `
        -and $workloadEvidence.structuralFingerprint -ne $ExpectedWorkloadFingerprint) {
        throw "Workload fingerprint '$($workloadEvidence.structuralFingerprint)' differs from expected '$ExpectedWorkloadFingerprint'."
    }

    $matrixPoints = @([pscustomobject]@{
            strategy = 'PlcCycle'
            monitoringIntervalMs = $null
            name = 'plccycle-unthrottled'
        })
    foreach ($monitoringIntervalMs in $monitoringIntervalsMs) {
        $matrixPoints += [pscustomobject]@{
            strategy = 'Polling'
            monitoringIntervalMs = $monitoringIntervalMs
            name = 'polling-{0:D3}ms' -f $monitoringIntervalMs
        }
        $matrixPoints += [pscustomobject]@{
            strategy = 'PlcCycle'
            monitoringIntervalMs = $monitoringIntervalMs
            name = 'plccycle-{0:D3}ms' -f $monitoringIntervalMs
        }
    }

    Set-ProcessEnvironmentValue -Name 'PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_CAPACITY_MEASUREMENT_ROLE' -Value 'CapacityPoint'
    Set-ProcessEnvironmentValue -Name 'PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_CAPACITY_RUN_COUNT' -Value $intendedRunCount.ToString([Globalization.CultureInfo]::InvariantCulture)
    foreach ($point in $matrixPoints) {
        Set-ProcessEnvironmentValue -Name 'PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_OBSERVATION_STRATEGY' -Value $point.strategy
        $monitoringValue = if ($null -eq $point.monitoringIntervalMs) {
            $null
        }
        else {
            ([int]$point.monitoringIntervalMs).ToString([Globalization.CultureInfo]::InvariantCulture)
        }
        Set-ProcessEnvironmentValue -Name 'PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_MONITORING_INTERVAL_MS' -Value $monitoringValue

        $pointName = 'plc-{0:D3}ms-{1}' -f $ConfiguredPlcMinimumCycleTimeMs, $point.name
        for ($attempt = 1; $attempt -le 2; $attempt++) {
            $attemptName = '{0}-attempt-{1:D2}' -f $pointName, $attempt
            $pointPath = Join-Path $pointDirectory ($attemptName + '.json')
            $pointEvidence = Invoke-CapacityEvidenceRun -Name $attemptName -OutputPath $pointPath
            $requiresRepeat = Assert-CapacityPointEvidence `
                -Evidence $pointEvidence `
                -ExpectedFingerprint $workloadEvidence.structuralFingerprint `
                -ExpectedRunCount $intendedRunCount `
                -BacklogRepeatThreshold $backlogRepeatThreshold
            if (-not $requiresRepeat -or $attempt -eq 2) {
                break
            }
            Write-Warning "$pointName reached the repeat criterion (event loss or fewer than 32 free EventBuffer slots). Repeating once."
        }
    }
}
finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name], 'Process')
    }
}

$computerSystem = Get-CimInstance Win32_ComputerSystem
$operatingSystem = Get-CimInstance Win32_OperatingSystem
$pointResults = Get-ChildItem -LiteralPath $pointDirectory -Filter '*.json' |
    Sort-Object Name |
    ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json }
$blockEvidence = [ordered]@{
    schemaVersion = 2
    recordedAtUtc = [DateTimeOffset]::UtcNow
    sourceRevision = $sourceRevision
    configuredPlcMinimumCycleTimeMs = $ConfiguredPlcMinimumCycleTimeMs
    configuredCycleIsOperatorSuppliedLabel = $true
    workload = $workloadEvidence
    measurementConditions = [ordered]@{
        buildConfiguration = 'Release'
        observationStrategies = @('Polling', 'PlcCycle')
        pollingMonitoringIntervalsMs = $monitoringIntervalsMs
        plcCycleIncludesUnthrottledObservation = $true
        plcCycleMonitoringIntervalsMs = $monitoringIntervalsMs
        intendedRunsPerPoint = $intendedRunCount
        drainIntervalMs = 1
        maxEventsPerBatch = 32
        rawEventLoggingEnabled = $false
        tiaPortalOffline = $true
        noParallelWpfOrCliCommunication = $true
        plcProgramPrepared = $true
    }
    host = [ordered]@{
        machineName = $env:COMPUTERNAME
        operatingSystem = $operatingSystem.Caption
        operatingSystemVersion = $operatingSystem.Version
        processorCount = [Environment]::ProcessorCount
        totalPhysicalMemoryBytes = [long]$computerSystem.TotalPhysicalMemory
    }
    reference = $referenceEvidence
    points = @($pointResults)
}
$blockPath = Join-Path $outputRoot ('plc-{0:D3}ms-capacity-block.json' -f $ConfiguredPlcMinimumCycleTimeMs)
$blockEvidence | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $blockPath -Encoding utf8
Write-Host "Workload fingerprint: $($workloadEvidence.structuralFingerprint)"
Write-Host "Capacity block evidence written: $blockPath"
