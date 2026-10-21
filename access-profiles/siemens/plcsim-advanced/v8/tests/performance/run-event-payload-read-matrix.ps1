[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [ValidateRange(2, 100)]
    [int]$SamplesPerPoint = 10,

    [int[]]$TargetEventCounts = @(1, 2, 4, 8, 16, 32, 64, 128, 192, 255),

    [switch]$SkipNaturalReference,

    [switch]$PlcProgramPrepared,

    [switch]$ExclusiveCommunicationConfirmed
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $PlcProgramPrepared) {
    throw 'Confirm that the PLC program was downloaded with a 1 ms configured minimum cycle time by passing -PlcProgramPrepared.'
}
if (-not $ExclusiveCommunicationConfirmed) {
    throw 'Confirm that TIA Portal is offline and no WPF/CLI client communicates with the runtime by passing -ExclusiveCommunicationConfirmed.'
}

$targets = @($TargetEventCounts | Sort-Object -Unique)
if ($targets.Count -eq 0 -or @($targets | Where-Object { $_ -lt 1 -or $_ -gt 255 }).Count -gt 0) {
    throw 'TargetEventCounts must contain values from 1 through 255.'
}

$testProject = Join-Path $PSScriptRoot '..\PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.PlcsimAdvanced.V8.csproj'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..\..\..'))
$outputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
}
else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
}
$sampleDirectory = Join-Path $outputRoot 'samples'
$trxDirectory = Join-Path $outputRoot 'trx'
$testFilter = 'FullyQualifiedName=PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.PlcsimAdvanced.V8.PlcsimAdvancedV8RealRuntimeTests.V8_binding_can_measure_real_adaptive_event_payload_read_when_enabled'
$environmentNames = @(
    'PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_EVENT_READ_MEASUREMENT',
    'PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_EVENT_READ_MEASUREMENT_MODE',
    'PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_EVENT_READ_MEASUREMENT_TARGET',
    'PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_EVENT_READ_MEASUREMENT_OUTPUT',
    'PLCSIM_UNITTEST_SOURCE_REVISION'
)

if ((Test-Path -LiteralPath $outputRoot) -and @(Get-ChildItem -LiteralPath $outputRoot -Force).Count -gt 0) {
    throw "OutputDirectory '$outputRoot' is not empty. Use a new directory so stale samples cannot enter the result."
}

New-Item -ItemType Directory -Path $sampleDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $trxDirectory -Force | Out-Null

$sourceRevision = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) {
    throw 'Could not determine the source revision.'
}

Write-Host 'Building the V8 real-runtime test project in Release configuration...'
& dotnet build $testProject -c Release
if ($LASTEXITCODE -ne 0) {
    throw "Release build failed with exit code $LASTEXITCODE."
}

function Set-MeasurementEnvironment {
    param(
        [Parameter(Mandatory = $true)][string]$Mode,
        [Parameter(Mandatory = $true)][int]$Target,
        [Parameter(Mandatory = $true)][string]$OutputPath
    )

    [Environment]::SetEnvironmentVariable('PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_EVENT_READ_MEASUREMENT', '1', 'Process')
    [Environment]::SetEnvironmentVariable('PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_EVENT_READ_MEASUREMENT_MODE', $Mode, 'Process')
    [Environment]::SetEnvironmentVariable('PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_EVENT_READ_MEASUREMENT_TARGET', $Target.ToString([Globalization.CultureInfo]::InvariantCulture), 'Process')
    [Environment]::SetEnvironmentVariable('PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_EVENT_READ_MEASUREMENT_OUTPUT', $OutputPath, 'Process')
    [Environment]::SetEnvironmentVariable('PLCSIM_UNITTEST_SOURCE_REVISION', $sourceRevision, 'Process')
}

function Invoke-MeasurementSample {
    param(
        [Parameter(Mandatory = $true)][string]$Mode,
        [Parameter(Mandatory = $true)][int]$Target,
        [Parameter(Mandatory = $true)][int]$SampleIndex
    )

    $pointName = '{0}-{1:D3}-sample-{2:D2}' -f $Mode.ToLowerInvariant(), $Target, $SampleIndex
    $samplePath = Join-Path $sampleDirectory ($pointName + '.json')
    $trxName = $pointName + '.trx'
    Set-MeasurementEnvironment -Mode $Mode -Target $Target -OutputPath $samplePath

    Write-Host "Measuring mode=$Mode target=$Target sample=$SampleIndex/$SamplesPerPoint..."
    & dotnet test $testProject -c Release --no-build --filter $testFilter --logger "trx;LogFileName=$trxName" --results-directory $trxDirectory | Out-Host
    $testExitCode = $LASTEXITCODE
    if ($testExitCode -ne 0) {
        throw "Measurement '$pointName' failed with exit code $testExitCode."
    }
    if (-not (Test-Path -LiteralPath $samplePath)) {
        throw "Measurement '$pointName' did not write '$samplePath'."
    }

    $sample = Get-Content -LiteralPath $samplePath -Raw | ConvertFrom-Json
    if ([int]$sample.TargetEventCount -ne $Target -or [int]$sample.EventCount -ne $Target) {
        throw "Measurement '$pointName' did not read exactly $Target events."
    }
    if ([string]$sample.Mode -ne $Mode) {
        throw "Measurement '$pointName' reported mode '$($sample.Mode)'."
    }

    return $sample
}

function Get-NearestRankPercentile {
    param(
        [Parameter(Mandatory = $true)][double[]]$Values,
        [Parameter(Mandatory = $true)][double]$Percentile
    )

    $ordered = @($Values | Sort-Object)
    $rank = [math]::Ceiling($Percentile * $ordered.Count)
    return $ordered[[math]::Max(0, $rank - 1)]
}

try {
    $samples = [Collections.Generic.List[object]]::new()
    foreach ($target in $targets) {
        for ($sampleIndex = 1; $sampleIndex -le $SamplesPerPoint; $sampleIndex++) {
            $samples.Add((Invoke-MeasurementSample -Mode 'Controlled' -Target $target -SampleIndex $sampleIndex))
        }
    }
    if (-not $SkipNaturalReference) {
        for ($sampleIndex = 1; $sampleIndex -le $SamplesPerPoint; $sampleIndex++) {
            $samples.Add((Invoke-MeasurementSample -Mode 'Natural' -Target 32 -SampleIndex $sampleIndex))
        }
    }

    $controlled = @($samples | Where-Object { [string]$_.Mode -eq 'Controlled' })
    $regressionSamples = @($controlled | Where-Object { [int]$_.CapturedEventCount -lt 255 })
    $regression = $null
    if (@($regressionSamples.CapturedEventCount | Sort-Object -Unique).Count -ge 2) {
        $meanX = ($regressionSamples | Measure-Object -Property CapturedEventCount -Average).Average
        $meanY = ($regressionSamples | Measure-Object -Property EntryReadDurationMs -Average).Average
        $sxx = ($regressionSamples | ForEach-Object { [math]::Pow([double]$_.CapturedEventCount - $meanX, 2) } | Measure-Object -Sum).Sum
        $sxy = ($regressionSamples | ForEach-Object { ([double]$_.CapturedEventCount - $meanX) * ([double]$_.EntryReadDurationMs - $meanY) } | Measure-Object -Sum).Sum
        $slope = $sxy / $sxx
        $intercept = $meanY - ($slope * $meanX)
        $ssTotal = ($regressionSamples | ForEach-Object { [math]::Pow([double]$_.EntryReadDurationMs - $meanY, 2) } | Measure-Object -Sum).Sum
        $ssResidual = ($regressionSamples | ForEach-Object {
                [math]::Pow([double]$_.EntryReadDurationMs - ($intercept + ($slope * [double]$_.CapturedEventCount)), 2)
            } | Measure-Object -Sum).Sum
        $regression = [ordered]@{
            sampleCount = $regressionSamples.Count
            maximumCapturedEventCount = ($regressionSamples | Measure-Object -Property CapturedEventCount -Maximum).Maximum
            interceptMs = $intercept
            millisecondsPerEvent = $slope
            rSquared = 1 - ($ssResidual / $ssTotal)
        }
    }

    $pointSummaries = @($samples |
            Group-Object Mode, TargetEventCount |
            ForEach-Object {
                $group = @($_.Group)
                $entryDurations = [double[]]@($group | ForEach-Object { [double]$_.EntryReadDurationMs })
                [ordered]@{
                    mode = [string]$group[0].Mode
                    targetEventCount = [int]$group[0].TargetEventCount
                    sampleCount = $group.Count
                    entryReadDurationMsMean = ($entryDurations | Measure-Object -Average).Average
                    entryReadDurationMsMedian = Get-NearestRankPercentile -Values $entryDurations -Percentile 0.5
                    entryReadDurationMsP95NearestRank = Get-NearestRankPercentile -Values $entryDurations -Percentile 0.95
                    entryReadDurationMsMaximum = ($entryDurations | Measure-Object -Maximum).Maximum
                    capturedEventCountMinimum = ($group | Measure-Object -Property CapturedEventCount -Minimum).Minimum
                    capturedEventCountMaximum = ($group | Measure-Object -Property CapturedEventCount -Maximum).Maximum
                    readStates = @($group.ReadState | Sort-Object -Unique)
                }
            } |
            Sort-Object targetEventCount, mode)

    $result = [ordered]@{
        schemaVersion = 1
        recordedAtUtc = [DateTimeOffset]::UtcNow
        sourceRevision = $sourceRevision
        buildConfiguration = 'Release'
        configuredPlcMinimumCycleTimeMs = 1
        observationStrategy = 'PlcCycle'
        monitoringIntervalMs = $null
        drainIntervalMs = 1
        rawEventLoggingEnabled = $false
        samplesPerPoint = $SamplesPerPoint
        controlledTargets = $targets
        naturalReferenceIncluded = -not $SkipNaturalReference
        regression = $regression
        pointSummaries = $pointSummaries
        samples = @($samples)
    }

    $resultPath = Join-Path $outputRoot 'event-payload-read-matrix.json'
    $result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $resultPath -Encoding utf8
    @($samples | ForEach-Object {
            [pscustomobject]@{
                mode = $_.Mode
                targetEventCount = $_.TargetEventCount
                eventCount = $_.EventCount
                capturedEventCount = $_.CapturedEventCount
                readState = $_.ReadState
                snapshotDurationMs = $_.SnapshotDurationMs
                preMetadataDurationMs = $_.PreMetadataDurationMs
                entryReadDurationMs = $_.EntryReadDurationMs
                postMetadataDurationMs = $_.PostMetadataDurationMs
                sourceRevision = $_.SourceRevision
            }
        }) | Export-Csv -LiteralPath (Join-Path $outputRoot 'event-payload-read-samples.csv') -NoTypeInformation -Encoding utf8

    Write-Host "Payload-read matrix written: $resultPath"
    if ($null -ne $regression) {
        Write-Host ('Regression: durationMs = {0:F3} + {1:F3} * events; R2={2:F4}; n={3}' -f $regression.interceptMs, $regression.millisecondsPerEvent, $regression.rSquared, $regression.sampleCount)
    }
}
finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $null, 'Process')
    }
}
