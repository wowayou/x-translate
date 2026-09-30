[CmdletBinding()]
param(
    [ValidateRange(0.001, 16.0)][double]$DurationHours = 8,
    [ValidateRange(5, 600)][int]$CheckpointSeconds = 600,
    [ValidateRange(1, 600)][int]$DiagnosticWindowSeconds = 30,
    [ValidateRange(1, 600)][int]$DiagnosticIdleSeconds = 1,
    [ValidateRange(1, 60)][int]$TimeoutGraceMinutes = 30,
    [ValidateRange(1, 1024)][decimal]$PwsLimitMiB = 60,
    [ValidateRange(1, 4096)][decimal]$PrivateBytesLimitMiB = 159.4072265625,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts\verification\memory-soak'),
    [string]$SessionDirectory,
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Quote-ArgumentLine([string[]]$Arguments) {
    return (($Arguments | ForEach-Object { '"' + ([string]$_).Replace('"', '\"') + '"' }) -join ' ')
}

function Get-Median($Values) {
    $sorted = @($Values | Sort-Object)
    Require ($sorted.Count -gt 0) 'Cannot calculate an empty median.'
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2) { return [decimal]$sorted[$middle] }
    return ([decimal]$sorted[$middle - 1] + [decimal]$sorted[$middle]) / 2
}

function Write-JsonAtomic([string]$Path, $Value) {
    $temporary = "$Path.tmp"
    $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}

function Read-Progress([string]$ReportPath) {
    if (-not (Test-Path -LiteralPath $ReportPath -PathType Leaf)) { return $null }
    try {
        $report = Get-Content -Raw -LiteralPath $ReportPath | ConvertFrom-Json
        return [ordered]@{
            reportUpdatedUtc = $report.updatedUtc
            reportPhase = $report.phase
            completedHoldPhases = @($report.phases | Where-Object { $_.phase -like 'batch-*-hidden-hold' }).Count
        }
    } catch {
        return [ordered]@{ reportReadPending = $true }
    }
}

$projectRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null
$session = if ([string]::IsNullOrWhiteSpace($SessionDirectory)) {
    Join-Path $outputRoot ('session-{0:yyyyMMdd-HHmmss-fff}-{1}' -f [DateTime]::UtcNow, ([Guid]::NewGuid().ToString('N').Substring(0, 8)))
} else {
    [IO.Path]::GetFullPath($SessionDirectory)
}
[IO.Directory]::CreateDirectory($session) | Out-Null
foreach ($reserved in @('soak-config.json', 'soak-status.json', 'soak-summary.json', 'memory-diagnostics.json', 'app')) {
    Require (-not (Test-Path -LiteralPath (Join-Path $session $reserved))) "Session path is not empty: $session"
}

$requestedSeconds = [int][Math]::Ceiling($DurationHours * 3600d)
$batches = [int][Math]::Ceiling($requestedSeconds / [double]$CheckpointSeconds)
Require ($batches -ge 1 -and $batches -le 100) 'Duration and checkpoint interval require more than 100 batches; increase CheckpointSeconds or reduce DurationHours.'
$scheduledHoldSeconds = $batches * $CheckpointSeconds
$timeoutSeconds = $scheduledHoldSeconds + ($TimeoutGraceMinutes * 60)
$pwsLimitBytes = [decimal]$PwsLimitMiB * 1MB
$privateBytesLimitBytes = [decimal]$PrivateBytesLimitMiB * 1MB
$appPublish = Join-Path $session 'app'
$reportPath = Join-Path $session 'memory-diagnostics.json'
$configPath = Join-Path $session 'soak-config.json'
$statusPath = Join-Path $session 'soak-status.json'
$summaryPath = Join-Path $session 'soak-summary.json'
$sdk = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk -PathType Leaf)) { $sdk = (Get-Command dotnet -ErrorAction Stop).Source }
$packageCache = Join-Path $projectRoot '.tools\cli\.nuget\packages'
if (Test-Path -LiteralPath $packageCache -PathType Container) { $env:NUGET_PACKAGES = $packageCache }
$env:DOTNET_CLI_HOME = Join-Path $session 'dotnet-cli-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

$deployment = if ($FrameworkDependent) { 'framework-dependent' } else { 'self-contained' }
$contained = if ($FrameworkDependent) { 'false' } else { 'true' }
$config = [ordered]@{
    createdUtc = [DateTimeOffset]::UtcNow
    sessionDirectory = $session
    durationHoursRequested = $DurationHours
    durationSecondsRequested = $requestedSeconds
    checkpointSeconds = $CheckpointSeconds
    batches = $batches
    scheduledHoldSeconds = $scheduledHoldSeconds
    diagnosticWindowSeconds = $DiagnosticWindowSeconds
    diagnosticIdleSeconds = $DiagnosticIdleSeconds
    timeoutSeconds = $timeoutSeconds
    deployment = $deployment
    runtimeIdentifier = 'win-x64'
    productDefaultRendering = 'software'
    pwsLimitMiBExclusive = $PwsLimitMiB
    privateBytesLimitMiBInclusive = $PrivateBytesLimitMiB
    workload = 'Each batch performs 100 off-screen Show/Hide transitions and 20 fixed in-process translations, then samples hidden state once per second.'
    network = 'none: fixed in-process HttpMessageHandler'
    clipboard = $false
    userData = $false
    forcedCollection = $false
    workingSetTrim = $false
    acceptance = 'Operational completion plus every hidden-hold phase median below the PWS limit and at or below the Private Bytes limit; trend deltas remain observational.'
}
Write-JsonAtomic $configPath $config

$process = $null
$watch = [Diagnostics.Stopwatch]::StartNew()
$startedUtc = [DateTimeOffset]::UtcNow
try {
    & $sdk publish (Join-Path $projectRoot 'src\XTranslate.App\XTranslate.App.csproj') -c Release -r win-x64 --self-contained $contained -p:DebugType=None -p:DebugSymbols=false -o $appPublish --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Soak application publish failed.' }
    $executable = Join-Path $appPublish 'XTranslate.exe'
    Require (Test-Path -LiteralPath $executable -PathType Leaf) 'Published soak executable was not found.'
    $arguments = @(
        '--memory-diagnostics', '--benchmark-output', $reportPath,
        '--diagnostic-batches', [string]$batches,
        '--diagnostic-idle-seconds', [string]$DiagnosticIdleSeconds,
        '--diagnostic-settle-seconds', '3',
        '--diagnostic-window-seconds', [string]$DiagnosticWindowSeconds,
        '--diagnostic-hold-seconds', [string]$CheckpointSeconds,
        '--diagnostic-compact-holds'
    )
    $config['executable'] = $executable
    $config['arguments'] = $arguments
    Write-JsonAtomic $configPath $config
    $process = Start-Process -FilePath $executable -ArgumentList (Quote-ArgumentLine $arguments) -WindowStyle Hidden -PassThru
    $pollSeconds = [Math]::Min(30, $CheckpointSeconds)
    while (-not $process.WaitForExit($pollSeconds * 1000)) {
        if ($watch.Elapsed.TotalSeconds -gt $timeoutSeconds) {
            try { $process.Kill() } catch { }
            $process.WaitForExit(5000) | Out-Null
            throw "Soak timed out after $([Math]::Round($watch.Elapsed.TotalHours, 3)) hours; only PID $($process.Id) was terminated."
        }
        $status = [ordered]@{
            state = 'running'
            processId = $process.Id
            startedUtc = $startedUtc
            updatedUtc = [DateTimeOffset]::UtcNow
            elapsedSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 3)
            expectedMinimumSeconds = $requestedSeconds
            progress = Read-Progress $reportPath
        }
        Write-JsonAtomic $statusPath $status
    }
    Require ($process.ExitCode -eq 0) "Soak process exited with code $($process.ExitCode)."
    Require (Test-Path -LiteralPath $reportPath -PathType Leaf) 'Soak process did not write its report.'
    $report = Get-Content -Raw -LiteralPath $reportPath | ConvertFrom-Json
    Require ($report.success -eq $true -and $report.phase -eq 'complete') 'Soak diagnostic report did not complete successfully.'
    Require ($report.configuration.network -eq 'none: fixed in-process HttpMessageHandler' -and
        $report.configuration.clipboard -eq $false -and $report.configuration.userData -eq $false -and
        $report.configuration.forcedCollection -eq $false -and $report.configuration.workingSetTrim -eq $false) 'Soak safety configuration mismatch.'
    Require ($report.configuration.processRenderMode -eq 'SoftwareOnly') 'Soak did not use the product-default software renderer.'
    Require ($report.configuration.batches -eq $batches -and $report.configuration.holdSeconds -eq $CheckpointSeconds -and
        $report.configuration.compactHolds -eq $true) 'Soak report configuration mismatch.'
    $holds = @($report.phases | Where-Object { $_.phase -like 'batch-*-hidden-hold' })
    Require ($holds.Count -eq $batches) "Expected $batches completed hold phases, found $($holds.Count)."
    $phaseNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($hold in $holds) { Require ($phaseNames.Add([string]$hold.phase)) "Duplicate hold phase: $($hold.phase)" }
    $sidecarNames = @($holds.rawSamplesFile | Select-Object -Unique)
    Require ($sidecarNames.Count -eq 1 -and -not [string]::IsNullOrWhiteSpace([string]$sidecarNames[0])) 'Soak raw sample sidecar is missing or inconsistent.'
    $samplesPath = [IO.Path]::GetFullPath((Join-Path (Split-Path $reportPath) ([string]$sidecarNames[0])))
    Require ([IO.Path]::GetDirectoryName($samplesPath) -eq [IO.Path]::GetDirectoryName($reportPath)) 'Soak sidecar escaped the session directory.'
    Require (Test-Path -LiteralPath $samplesPath -PathType Leaf) 'Soak raw sample sidecar was not found.'
    $sampleGroups = @{}
    foreach ($line in Get-Content -LiteralPath $samplesPath) {
        $row = $line | ConvertFrom-Json
        Require ($phaseNames.Contains([string]$row.phase)) "Unexpected phase in soak sidecar: $($row.phase)"
        if (-not $sampleGroups.ContainsKey([string]$row.phase)) {
            $sampleGroups[[string]$row.phase] = [ordered]@{
                count = 0
                previousSecond = -1
                pws = [Collections.Generic.List[long]]::new()
                private = [Collections.Generic.List[long]]::new()
                ws = [Collections.Generic.List[long]]::new()
                first = $null
                last = $null
            }
        }
        $group = $sampleGroups[[string]$row.phase]
        Require ([int]$row.second -eq ([int]$group.previousSecond + 1)) "Out-of-order sidecar sample: $($row.phase) second $($row.second)"
        Require ($null -ne $row.snapshot.PrivateWorkingSetBytes -and $row.snapshot.PrivateWorkingSetBytes -gt 0) "Missing PWS sample: $($row.phase)"
        Require ($row.snapshot.PrivateBytes -gt 0 -and $row.snapshot.WorkingSetBytes -gt 0) "Invalid process memory sample: $($row.phase)"
        if ($null -eq $group.first) { $group.first = $row.snapshot }
        $group.last = $row.snapshot
        $group.previousSecond = [int]$row.second
        $group.count = [int]$group.count + 1
        $group.pws.Add([long]$row.snapshot.PrivateWorkingSetBytes)
        $group.private.Add([long]$row.snapshot.PrivateBytes)
        $group.ws.Add([long]$row.snapshot.WorkingSetBytes)
    }
    $holdResults = @()
    foreach ($hold in $holds) {
        Require ($hold.isVisible -eq $false) "Hold phase was visible: $($hold.phase)"
        Require ($hold.durationMs -ge (($CheckpointSeconds * 1000) - 100)) "Hold phase was too short: $($hold.phase)"
        Require ($sampleGroups.ContainsKey([string]$hold.phase)) "Hold phase has no sidecar samples: $($hold.phase)"
        $group = $sampleGroups[[string]$hold.phase]
        Require ($group.count -eq ($CheckpointSeconds + 1) -and $hold.rawSampleCount -eq $group.count) "Hold phase sample count mismatch: $($hold.phase)"
        Require ($group.previousSecond -eq $CheckpointSeconds) "Hold phase did not reach its final second: $($hold.phase)"
        $pwsMedian = Get-Median $group.pws
        $privateMedian = Get-Median $group.private
        $wsMedian = Get-Median $group.ws
        Require ($hold.statistics.PrivateWorkingSet.MedianBytes -eq $pwsMedian -and
            $hold.statistics.PrivateBytes.MedianBytes -eq $privateMedian -and
            $hold.statistics.WorkingSet.MedianBytes -eq $wsMedian) "Hold report statistics disagree with raw sidecar: $($hold.phase)"
        $pwsPassed = $pwsMedian -lt $pwsLimitBytes
        $privatePassed = $privateMedian -le $privateBytesLimitBytes
        $holdResults += [pscustomobject]@{
            phase = $hold.phase
            samples = $group.count
            durationMs = $hold.durationMs
            pwsMedianBytes = $pwsMedian
            privateMedianBytes = $privateMedian
            workingSetMedianBytes = $wsMedian
            pwsPassed = $pwsPassed
            privateBytesPassed = $privatePassed
            passed = $pwsPassed -and $privatePassed
        }
    }
    $totalSamples = ($holdResults | Measure-Object -Property samples -Sum).Sum
    Require ($totalSamples -gt 1) 'Soak did not contain enough snapshots for trend reporting.'
    $first = $sampleGroups[[string]$holds[0].phase].first
    $last = $sampleGroups[[string]$holds[-1].phase].last
    $third = [Math]::Max(1, [int][Math]::Floor($holdResults.Count / 3))
    $firstThird = @($holdResults | Select-Object -First $third)
    $lastThird = @($holdResults | Select-Object -Last $third)
    $allBudgetsPassed = @($holdResults | Where-Object passed -eq $false).Count -eq 0
    $summary = [ordered]@{
        success = $allBudgetsPassed
        soakStatus = if ($allBudgetsPassed) { 'passed' } else { 'failed' }
        completedUtc = [DateTimeOffset]::UtcNow
        processId = $process.Id
        processExitCode = $process.ExitCode
        elapsedSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 3)
        requestedSeconds = $requestedSeconds
        scheduledHoldSeconds = $scheduledHoldSeconds
        completedHoldPhases = $holds.Count
        holdSamplesValidated = $totalSamples
        pwsLimitBytesExclusive = $pwsLimitBytes
        privateBytesLimitBytesInclusive = $privateBytesLimitBytes
        allHoldMediansPassed = $allBudgetsPassed
        firstThirdMedian = [ordered]@{
            pwsBytes = Get-Median $firstThird.pwsMedianBytes
            privateBytes = Get-Median $firstThird.privateMedianBytes
        }
        lastThirdMedian = [ordered]@{
            pwsBytes = Get-Median $lastThird.pwsMedianBytes
            privateBytes = Get-Median $lastThird.privateMedianBytes
        }
        endpointDelta = [ordered]@{
            privateWorkingSetBytes = $last.PrivateWorkingSetBytes - $first.PrivateWorkingSetBytes
            privateBytes = $last.PrivateBytes - $first.PrivateBytes
            workingSetBytes = $last.WorkingSetBytes - $first.WorkingSetBytes
            managedAllocatedEstimateBytes = $last.ManagedAllocatedEstimateBytes - $first.ManagedAllocatedEstimateBytes
            handles = $last.Handles - $first.Handles
            gdiObjects = if ($null -ne $first.GdiObjects -and $null -ne $last.GdiObjects) { $last.GdiObjects - $first.GdiObjects } else { $null }
            userObjects = if ($null -ne $first.UserObjects -and $null -ne $last.UserObjects) { $last.UserObjects - $first.UserObjects } else { $null }
        }
        holds = $holdResults
        report = $reportPath
        reportSha256 = (Get-FileHash -LiteralPath $reportPath -Algorithm SHA256).Hash
        rawSamples = $samplesPath
        rawSamplesSha256 = (Get-FileHash -LiteralPath $samplesPath -Algorithm SHA256).Hash
        config = $configPath
        leakConclusion = 'not-proven: an eight-hour bounded run and trend deltas cannot prove the absence of every leak.'
    }
    $stream = [IO.File]::Open($summaryPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes(($summary | ConvertTo-Json -Depth 12))
        $stream.Write($bytes, 0, $bytes.Length)
    } finally { $stream.Dispose() }
    Write-JsonAtomic $statusPath ([ordered]@{
        state = if ($allBudgetsPassed) { 'complete' } else { 'failed' }
        processId = $process.Id
        updatedUtc = [DateTimeOffset]::UtcNow
        elapsedSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 3)
        summary = $summaryPath
    })
    Write-Output $summaryPath
    if (-not $allBudgetsPassed) { throw "Soak completed but one or more memory-budget checkpoints failed: $summaryPath" }
}
catch {
    if ($null -ne $process -and -not $process.HasExited) {
        try { $process.Kill() } catch { }
        try { $process.WaitForExit(5000) | Out-Null } catch { }
    }
    Write-JsonAtomic $statusPath ([ordered]@{
        state = 'failed'
        processId = if ($null -ne $process) { $process.Id } else { $null }
        updatedUtc = [DateTimeOffset]::UtcNow
        elapsedSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 3)
        error = $_.Exception.ToString()
        partialReport = if (Test-Path -LiteralPath $reportPath) { $reportPath } else { $null }
    })
    throw
}
finally {
    $watch.Stop()
    if ($null -ne $process) { $process.Dispose() }
}
