[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReferenceSummaryPath,
    [Parameter(Mandatory)][string]$CandidateSummaryPath,
    [Parameter(Mandatory)][string]$OutputPath,
    [ValidateSet('self-contained', 'framework-dependent')][string]$Deployment = 'self-contained'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Get-Median($Values) {
    $sorted = @($Values | Sort-Object)
    Require ($sorted.Count -gt 0) 'Cannot calculate an empty median.'
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2) { return [decimal]$sorted[$middle] }
    return ([decimal]$sorted[$middle - 1] + [decimal]$sorted[$middle]) / 2
}

function Read-Session([string]$Path) {
    $summary = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
    Require ($summary.success -eq $true) "Session failed: $Path"
    Require ($summary.deployment -eq $Deployment -and $summary.runtimeIdentifier -eq 'win-x64') "Expected $Deployment win-x64 in both sessions."
    Require ($summary.runsPerGroup -eq 3) 'Acceptance requires three runs.'
    Require ($summary.diagnosticBatches -eq 1 -and $summary.diagnosticIdleSeconds -eq 1 -and
        $summary.diagnosticSettleSeconds -eq 3 -and $summary.diagnosticWindowSeconds -eq 30 -and
        $summary.diagnosticHoldSeconds -eq 0) 'Session does not use the approved comparison conditions.'
    $runs = @($summary.results | Where-Object group -eq 'app' | Sort-Object run)
    Require ($runs.Count -eq 3 -and ($runs.run -join ',') -eq '1,2,3') 'Expected distinct app runs 1,2,3.'
    Require (@($runs.report | Select-Object -Unique).Count -eq 3) 'Duplicate report paths.'
    $rows = foreach ($run in $runs) {
        Require ($run.success -eq $true -and $run.exitCode -eq 0 -and $run.measurementComplete -eq $true) 'Incomplete or failed app run.'
        $report = Get-Content -Raw -LiteralPath $run.report | ConvertFrom-Json
        Require ($report.success -eq $true -and $report.phase -eq 'complete' -and $report.processId -eq $run.processId) 'Report does not match its successful run.'
        $config = $report.configuration
        Require ($config.mode -eq 'memory-diagnostics' -and $config.batches -eq 1 -and $config.idleSeconds -eq 1 -and
            $config.holdSeconds -eq 0 -and $config.settleSeconds -eq 3 -and $config.windowSeconds -eq 30 -and
            $config.forcedCollection -eq $false -and $config.workingSetTrim -eq $false) 'Report configuration mismatch.'
        $phases = @($report.phases.phase)
        $windows = @($report.phases | Where-Object phase -eq 'hidden-measurement')
        Require ($windows.Count -eq 1 -and $phases.IndexOf('hidden-measurement') -eq $phases.IndexOf('visible-idle') + 1 -and
            $phases.IndexOf('batch-01') -gt $phases.IndexOf('hidden-measurement')) 'Invalid measurement phase position.'
        $window = $windows[0]
        Require ($window.isVisible -eq $false -and $window.configuredSettleSeconds -eq 3 -and $window.actualSettleMs -ge 2990 -and
            $window.configuredSampleCount -eq 30 -and $window.actualSampleCount -eq 30 -and $window.actualDurationMs -ge 29990) 'Invalid hidden window.'
        $samples = @($window.samples)
        Require ($samples.Count -eq 30) 'Missing raw samples.'
        $previousMs = 0
        $previousUtc = [DateTimeOffset]::MinValue
        for ($index = 0; $index -lt 30; $index++) {
            $sample = $samples[$index]
            $utc = [DateTimeOffset]::Parse($sample.snapshot.CapturedUtc)
            Require ($sample.second -eq $index + 1 -and $sample.atMs -gt $previousMs -and
                $sample.atMs -ge ($index + 1) * 990 -and $utc -gt $previousUtc) 'Invalid sample order/timing.'
            Require ($null -eq $sample.snapshot.PrivateWorkingSetFailureReason) 'PWS capture failed.'
            $previousMs = $sample.atMs
            $previousUtc = $utc
        }
        $stats = $window.statistics
        Require ($stats.MeasurementComplete -eq $true -and $stats.IsComplete -eq $true -and
            $stats.ConfiguredSampleCount -eq 30 -and $stats.ActualSampleCount -eq 30) 'Incomplete statistics.'
        Require (($run.window.statistics | ConvertTo-Json -Depth 8 -Compress) -eq
            ($stats | ConvertTo-Json -Depth 8 -Compress)) 'Session statistics disagree with report.'
        $medians = @{}
        foreach ($mapping in @(@('WorkingSet', 'WorkingSetBytes'), @('PrivateBytes', 'PrivateBytes'), @('PrivateWorkingSet', 'PrivateWorkingSetBytes'))) {
            $values = @($samples | ForEach-Object { $_.snapshot.($mapping[1]) })
            Require (@($values | Where-Object { $null -eq $_ -or $_ -le 0 }).Count -eq 0) 'Missing or nonpositive memory sample.'
            $sorted = @($values | Sort-Object)
            $median = Get-Median $values
            $metric = $stats.($mapping[0])
            Require ($metric.ValidSamples -eq 30 -and $metric.MissingSamples -eq 0 -and
                $metric.MinBytes -eq $sorted[0] -and $metric.MaxBytes -eq $sorted[-1] -and
                $metric.MedianBytes -eq $median) 'Raw samples disagree with statistics.'
            $medians[$mapping[0]] = $median
        }
        Require ($stats.PrivateBytesStartBytes -eq $samples[0].snapshot.PrivateBytes -and
            $stats.PrivateBytesEndBytes -eq $samples[-1].snapshot.PrivateBytes -and
            $stats.PrivateBytesDeltaBytes -eq ($samples[-1].snapshot.PrivateBytes - $samples[0].snapshot.PrivateBytes)) 'Private Bytes delta mismatch.'
        [pscustomobject]@{
            run = $run.run
            report = [IO.Path]::GetFullPath($run.report)
            reportSha256 = (Get-FileHash -LiteralPath $run.report -Algorithm SHA256).Hash
            pwsMedianBytes = $medians.PrivateWorkingSet
            privateMedianBytes = $medians.PrivateBytes
            wsMedianBytes = $medians.WorkingSet
            processRenderMode = $config.processRenderMode
            environment = "$($config.os)|$($config.runtime)|$($config.processArchitecture)|$($config.gcServer)|$($config.gcLatencyMode)"
        }
    }
    Require (@($rows.environment | Select-Object -Unique).Count -eq 1) 'Inconsistent runtime/OS within session.'
    Require (@($rows.processRenderMode | Select-Object -Unique).Count -eq 1) 'Inconsistent render mode within session.'
    return @($rows)
}

$reference = @(Read-Session $ReferenceSummaryPath)
$candidate = @(Read-Session $CandidateSummaryPath)
Require ($reference[0].environment -eq $candidate[0].environment) 'Reference and candidate runtime/OS differ.'
Require (@($candidate | Where-Object { $_.report -in $reference.report -or $_.reportSha256 -in $reference.reportSha256 }).Count -eq 0) 'Reference reports cannot also serve as candidate reports.'
$referencePrivateBytes = Get-Median $reference.privateMedianBytes
$privateLimitBytes = $referencePrivateBytes * 1.05d
$pwsLimitBytes = 60 * 1MB
$results = @($candidate | ForEach-Object {
    [pscustomobject]@{
        run = $_.run
        report = $_.report
        reportSha256 = $_.reportSha256
        pwsMedianBytes = $_.pwsMedianBytes
        privateMedianBytes = $_.privateMedianBytes
        pwsPassed = $_.pwsMedianBytes -lt $pwsLimitBytes
        privateBytesPassed = $_.privateMedianBytes -le $privateLimitBytes
        passed = $_.pwsMedianBytes -lt $pwsLimitBytes -and $_.privateMedianBytes -le $privateLimitBytes
    }
})
$passed = @($results | Where-Object passed -eq $false).Count -eq 0
$result = [ordered]@{
    evaluationComplete = $true
    targetStatus = if ($passed) { 'passed' } else { 'failed' }
    generatedUtc = [DateTimeOffset]::UtcNow
    referenceSummary = [IO.Path]::GetFullPath($ReferenceSummaryPath)
    candidateSummary = [IO.Path]::GetFullPath($CandidateSummaryPath)
    deployment = $Deployment
    pwsLimitBytesExclusive = $pwsLimitBytes
    referencePrivateMedianBytes = $referencePrivateBytes
    privateLimitBytesInclusive = $privateLimitBytes
    reference = $reference
    candidate = $candidate
    results = $results
    note = 'Three complete 30-point app windows; reference Private Bytes is the median of three run medians. No claim of an eight-hour soak or leak freedom.'
}
$output = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [IO.Path]::GetDirectoryName($output)
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) { [IO.Directory]::CreateDirectory($outputDirectory) | Out-Null }
$stream = [IO.File]::Open($output, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
try {
    $bytes = [Text.Encoding]::UTF8.GetBytes(($result | ConvertTo-Json -Depth 10))
    $stream.Write($bytes, 0, $bytes.Length)
} finally { $stream.Dispose() }
$result
if (-not $passed) { throw "Memory target failed; details: $output" }
