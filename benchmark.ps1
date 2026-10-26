# Local, explicit benchmark only: bounded sequential children, fresh evidence, no retries.
[CmdletBinding()]
param(
    [string]$ExecutablePath = (Join-Path $PSScriptRoot 'src\XTranslate.App\bin\Release\net10.0-windows\XTranslate.exe'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts\benchmark'),
    [ValidateRange(1, 20)][int]$Runs = 5,
    [ValidateRange(45, 240)][int]$TimeoutSeconds = 180,
    [switch]$SoftwareRendering,
    [switch]$HardwareRendering,
    [ValidateSet('System', 'Light', 'Dark')][string]$Theme
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Require-Benchmark([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-BenchmarkLocalPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    Require-Benchmark ($full -match '^[A-Za-z]:\\' -and
        ([IO.DriveInfo]::new([IO.Path]::GetPathRoot($full))).DriveType -ne [IO.DriveType]::Network) 'Benchmark paths must be on a local drive.'
    for ($current = $full; $null -ne $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if (Test-Path -LiteralPath $current) {
            Require-Benchmark (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Benchmark paths must not contain links/reparse points.'
        }
    }
    return $full
}

function Get-BenchmarkProgramIdentity([string]$Executable) {
    $rows = [Collections.Generic.List[object]]::new(); $examined = 0; $bytes = 0L
    foreach ($path in [IO.Directory]::EnumerateFileSystemEntries([IO.Path]::GetDirectoryName($Executable))) {
        Require-Benchmark (++$examined -le 512) 'Benchmark executable directory exceeds the flat 512-entry limit.'
        $name = [IO.Path]::GetFileName($path)
        if ($name -notmatch '\.(exe|dll)$|\.(runtimeconfig|deps)\.json$') { continue } # Never traverse data.
        $file = Get-Item -LiteralPath $path -Force
        Require-Benchmark (-not $file.PSIsContainer -and ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Benchmark program files must be ordinary files.'
        $bytes += $file.Length
        Require-Benchmark ($bytes -le 512MB) 'Benchmark program identity exceeds 512 MiB.'
        $rows.Add([pscustomobject]@{ name = $name; bytes = $file.Length; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash })
    }
    Require-Benchmark ($rows.Count -gt 0) 'Benchmark program identity is empty.'
    return $rows | Sort-Object name
}

function Read-BenchmarkEvidence([string]$Path, [long]$MaximumBytes, [DateTimeOffset]$StartedUtc) {
    $Path = Assert-BenchmarkLocalPath $Path
    Require-Benchmark (Test-Path -LiteralPath $Path -PathType Leaf) 'Benchmark evidence file is missing.'
    $file = Get-Item -LiteralPath $Path -Force
    Require-Benchmark ($file.Length -gt 0 -and $file.Length -le $MaximumBytes -and
        $file.LastWriteTimeUtc -ge $StartedUtc.UtcDateTime) 'Benchmark evidence is empty, oversized or predates this child.'
    return [IO.File]::ReadAllText($Path, [Text.UTF8Encoding]::new($false, $true))
}

function Assert-BenchmarkNumber($Value, [double]$Minimum, [double]$Maximum, [string]$Name, [switch]$Integral) {
    Require-Benchmark ($Value -is [ValueType] -and $Value -isnot [bool] -and $Value -isnot [char] -and
        $Value -isnot [DateTime] -and $Value -isnot [DateTimeOffset]) ('Benchmark numeric field is missing/invalid: ' + $Name)
    $number = [double]$Value
    Require-Benchmark (-not [double]::IsNaN($number) -and -not [double]::IsInfinity($number) -and
        $number -ge $Minimum -and $number -le $Maximum -and
        (-not $Integral -or $number -eq [Math]::Truncate($number))) ('Benchmark numeric field is out of range: ' + $Name)
}

function Assert-BenchmarkResources($Resources) {
    foreach ($metric in @('WorkingSetBytes', 'PrivateBytes')) {
        Assert-BenchmarkNumber $Resources.$metric 1 ([long]::MaxValue) $metric -Integral
    }
    # GC.GetTotalMemory(false) is an auxiliary estimate, not the private-memory gate.
    # Preserve invalid negative estimates verbatim and explicitly mark them unavailable below.
    Assert-BenchmarkNumber $Resources.ManagedHeapBytes ([long]::MinValue) ([long]::MaxValue) 'ManagedHeapBytes' -Integral
    foreach ($metric in @('Threads', 'Handles', 'GdiObjects', 'UserObjects')) {
        Assert-BenchmarkNumber $Resources.$metric 0 ([long]::MaxValue) $metric -Integral
    }
}

function Get-BenchmarkManagedEstimateStatus($Report) {
    $invalid = [Collections.Generic.List[object]]::new(); $examined = 0
    foreach ($name in @('visible', 'hidden', 'recovery')) {
        $phase = $Report.idle.$name
        $resources = @($phase.Start) + @($phase.Samples | ForEach-Object { $_.Resources }) + @($phase.End)
        for ($index = 0; $index -lt $resources.Count; $index++) {
            $examined++
            if ($resources[$index].ManagedHeapBytes -lt 0) {
                $invalid.Add([pscustomobject]@{ phase = $name; resourceIndex = $index; rawBytes = $resources[$index].ManagedHeapBytes })
            }
        }
    }
    foreach ($name in @('popup', 'translation')) {
        foreach ($endpoint in @('before', 'after')) {
            $examined++
            if ($Report.$name.resources.$endpoint.ManagedHeapBytes -lt 0) {
                $invalid.Add([pscustomobject]@{ phase = $name; endpoint = $endpoint; rawBytes = $Report.$name.resources.$endpoint.ManagedHeapBytes })
            }
        }
    }
    return [pscustomobject]@{ complete = $invalid.Count -eq 0; examinedResourceSnapshots = $examined;
        invalidSamples = $invalid.ToArray();
        policy = 'Negative GC.GetTotalMemory(false) estimates are unavailable, not zero or memory savings; raw evidence is unchanged. No estimate-based acceptance/attribution.' }
}

# Read-only per-metric assessment is not strict acceptance: incomplete idle remains explicit.
# This permits diagnosis of immutable legacy packages without rewriting evidence or rerunning failures.
function Get-BenchmarkReportAssessment($Report, [int]$ProcessId, [DateTimeOffset]$StartedUtc, [DateTimeOffset]$FinishedUtc,
    [string]$RenderMode, [string]$RequestedTheme) {
    Require-Benchmark ($Report.success -is [bool] -and $Report.success -eq $true) 'Benchmark report did not succeed.'
    Assert-BenchmarkNumber $Report.processId $ProcessId $ProcessId 'processId'
    $completedUtc = [DateTimeOffset]$Report.completedUtc
    Require-Benchmark ($completedUtc -ge $StartedUtc -and $completedUtc -le $FinishedUtc) 'Benchmark report timestamp is outside this child execution.'
    Require-Benchmark ($Report.startup.contentRenderedOneShot -is [bool] -and
        $Report.startup.contentRenderedOneShot -eq $true) 'Benchmark startup marker was not one-shot.'
    Assert-BenchmarkNumber $Report.startup.loadedMs 0 240000 'loadedMs'
    Assert-BenchmarkNumber $Report.startup.contentRenderedMs 0 240000 'contentRenderedMs'
    Require-Benchmark ($Report.startup.loadedMs -le $Report.startup.contentRenderedMs) 'Benchmark startup milestones are out of order.'
    $parameters = $Report.parameters
    $workload = @{ idleSeconds = 5; idleSampleIntervalMs = 200; popupIterations = 1000;
        translationIterations = 200; translationSuccesses = 200 }
    foreach ($field in $workload.Keys) {
        Assert-BenchmarkNumber $parameters.$field $workload[$field] $workload[$field] $field
    }
    Assert-BenchmarkNumber $Report.translation.successfulTranslations 200 200 'successfulTranslations'
    Require-Benchmark ($parameters.benchmarkData -ceq 'isolated benchmark-data directory' -and
        $parameters.network -ceq 'none: injected HttpClient handler ignores request contents and returns a fixed local response' -and
        $parameters.processRenderMode -ceq $RenderMode) 'Benchmark isolation, transport or rendering differs.'
    foreach ($flag in @('forcedCollection', 'workingSetTrim')) {
        Require-Benchmark ($parameters.$flag -is [bool] -and $parameters.$flag -eq $false) 'Benchmark forced collection/working-set trimming is not allowed.'
    }
    if ($RequestedTheme) {
        Require-Benchmark ($parameters.theme -ieq $RequestedTheme) 'Benchmark requested theme cannot be verified in the process report.'
    }
    foreach ($field in @('os', 'framework', 'dotnet')) {
        Require-Benchmark ($Report.machine.$field -is [string] -and
            -not [string]::IsNullOrWhiteSpace($Report.machine.$field)) 'Benchmark machine/runtime metadata is missing/invalid.'
    }
    Require-Benchmark ($Report.machine.processArchitecture -cin @('X64', 'X86', 'Arm64', 'Arm')) 'Benchmark process architecture is invalid.'
    Assert-BenchmarkNumber $Report.machine.processorCount 1 65536 'processorCount' -Integral
    $incomplete = [Collections.Generic.List[object]]::new()
    $states = @{ visible = 'visible'; hidden = 'hidden'; recovery = 'visible_after_translation' }
    foreach ($name in @('visible', 'hidden', 'recovery')) {
        $phase = $Report.idle.$name
        Require-Benchmark ($phase.State -ceq $states[$name]) 'Benchmark idle phases are missing or reordered.'
        Assert-BenchmarkNumber $phase.DurationMs 5000 240000 'idle duration'
        $samples = @($phase.Samples)
        Require-Benchmark ($samples.Count -ge 2 -and $samples.Count -le 1200) 'Benchmark idle sample series is missing, a single endpoint or unbounded.'
        Assert-BenchmarkResources $phase.Start
        Assert-BenchmarkResources $phase.End
        $previous = 0d
        foreach ($sample in $samples) {
            Assert-BenchmarkNumber $sample.ElapsedMs 0 ($phase.DurationMs + 1) 'idle sample time'
            Require-Benchmark ($sample.ElapsedMs -gt $previous) 'Benchmark idle samples are out of order.'
            $previous = $sample.ElapsedMs
            Assert-BenchmarkNumber $sample.CpuPercent 0 100 'idle CPU'
            Assert-BenchmarkResources $sample.Resources
        }
        if ($previous -lt 5000) {
            $incomplete.Add([pscustomobject]@{ phase = $name; lastSampleMs = $previous; durationMs = $phase.DurationMs })
        }
        foreach ($metric in @('WorkingSetBytes', 'PrivateBytes', 'ManagedHeapBytes', 'Threads', 'Handles', 'GdiObjects', 'UserObjects')) {
            Assert-BenchmarkNumber $phase.Delta.before.$metric $phase.Start.$metric $phase.Start.$metric ('idle before ' + $metric)
            Assert-BenchmarkNumber $phase.Delta.after.$metric $phase.End.$metric $phase.End.$metric ('idle after ' + $metric)
            $delta = $phase.End.$metric - $phase.Start.$metric
            Assert-BenchmarkNumber $phase.Delta.delta.$metric $delta $delta ('idle delta ' + $metric)
        }
    }
    foreach ($name in @('popup', 'translation')) {
        $phase = $Report.$name; $latency = $phase.latencyMs
        $count = if ($name -eq 'popup') { 1000 } else { 200 }
        Assert-BenchmarkNumber $latency.count $count $count 'latency count'
        $previous = 0d
        foreach ($quantile in @('p50', 'p90', 'p95', 'p99', 'max')) {
            Assert-BenchmarkNumber $latency.$quantile $previous 240000 ('latency ' + $quantile)
            $previous = $latency.$quantile
        }
        Assert-BenchmarkResources $phase.resources.before
        Assert-BenchmarkResources $phase.resources.after
        foreach ($metric in @('WorkingSetBytes', 'PrivateBytes', 'ManagedHeapBytes', 'Threads', 'Handles', 'GdiObjects', 'UserObjects')) {
            $delta = $phase.resources.after.$metric - $phase.resources.before.$metric
            Assert-BenchmarkNumber $phase.resources.delta.$metric $delta $delta ('loop delta ' + $metric)
        }
    }
    return [pscustomobject]@{ latencyMetricsValidated = $true; idleSamplingComplete = $incomplete.Count -eq 0;
        incompleteIdlePhases = $incomplete.ToArray() }
}

function Assert-BenchmarkReport($Report, [int]$ProcessId, [DateTimeOffset]$StartedUtc, [DateTimeOffset]$FinishedUtc,
    [string]$RenderMode, [string]$RequestedTheme) {
    $assessment = Get-BenchmarkReportAssessment $Report $ProcessId $StartedUtc $FinishedUtc $RenderMode $RequestedTheme
    Require-Benchmark $assessment.idleSamplingComplete 'Benchmark idle samples did not reach the full five-second window.'
}

function Invoke-BenchmarkProcess([string]$Executable, [string[]]$Arguments, [string]$MarkerPath, [int]$Timeout) {
    Require-Benchmark (-not (Test-Path -LiteralPath $MarkerPath)) 'Benchmark marker must not already exist.'
    $argumentLine = ($Arguments | ForEach-Object { '"' + ([string]$_).Replace('"', '\"') + '"' }) -join ' '
    # Identity hashing and JSON verification are outside the startup measurement.
    $startedUtc = [DateTimeOffset]::UtcNow
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = $null; $cleanupAttempted = $false
    try {
        $process = Start-Process -FilePath $Executable -ArgumentList $argumentLine -WindowStyle Hidden -PassThru
        $null = $process.Handle # Preserve exit information even for a very fast child.
        $processId = $process.Id; $markerSeen = $false; $launchMs = $null; $completedInTime = $false
        while ($watch.Elapsed.TotalSeconds -lt $Timeout) {
            if (Test-Path -LiteralPath $MarkerPath -PathType Leaf) {
                $markerSeen = $true; $launchMs = $watch.Elapsed.TotalMilliseconds
            }
            if ($process.HasExited) { $completedInTime = $watch.Elapsed.TotalSeconds -lt $Timeout; break }
            if ($markerSeen) { break }
            Start-Sleep -Milliseconds 50
        }
        if (-not $completedInTime) {
            $remainingMs = [int][Math]::Max(0, [Math]::Floor($Timeout * 1000 - $watch.Elapsed.TotalMilliseconds))
            if ($remainingMs -gt 0) {
                $completedInTime = $process.WaitForExit($remainingMs) -and $watch.Elapsed.TotalSeconds -lt $Timeout
            }
        }
        # A deadline stays a failure even if cleanup observes exit 0 and a successful report.
        $timedOut = -not $completedInTime; $cleanupFailure = $null
        if (-not $process.HasExited) {
            $cleanupAttempted = $true
            try {
                $process.Kill()
                if (-not $process.WaitForExit(5000)) { $cleanupFailure = 'ChildDidNotExitAfterKill' }
            } catch { $cleanupFailure = $_.Exception.GetType().Name }
        }
        $watch.Stop()
        return [pscustomobject]@{
            processId = $processId; exitCode = if ($process.HasExited) { $process.ExitCode } else { $null }
            completed = $process.HasExited; timedOut = $timedOut; cleanupFailure = $cleanupFailure
            markerSeen = $markerSeen; launchToContentRenderedMs = $launchMs
            startedUtc = $startedUtc.ToString('O'); finishedUtc = [DateTimeOffset]::UtcNow.ToString('O')
            elapsedSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 3)
        }
    } finally {
        $watch.Stop()
        if ($null -ne $process) {
            try {
                # Exceptional paths still get one cleanup attempt; never repeat a failed kill/wait.
                if (-not $cleanupAttempted -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
            } finally { $process.Dispose() }
        }
    }
}

function Get-BenchmarkArtifactAssessment($Execution, [string]$ResultPath, [string]$MarkerPath, [string]$RenderMode, [string]$RequestedTheme) {
    foreach ($flag in @('completed', 'timedOut', 'markerSeen')) {
        Require-Benchmark ($Execution.$flag -is [bool]) 'Benchmark child completion/deadline/marker flags are not boolean.'
    }
    Require-Benchmark ($Execution.completed -eq $true -and $Execution.timedOut -eq $false -and
        $null -eq $Execution.cleanupFailure -and $Execution.markerSeen -eq $true) 'Benchmark child failed, timed out or did not write its startup marker.'
    Assert-BenchmarkNumber $Execution.exitCode 0 0 'child exitCode'
    Assert-BenchmarkNumber $Execution.processId 1 ([int]::MaxValue) 'child processId' -Integral
    Assert-BenchmarkNumber $Execution.launchToContentRenderedMs 0 240000 'launchToContentRenderedMs'
    $startedUtc = [DateTimeOffset]$Execution.startedUtc
    $finishedUtc = [DateTimeOffset]$Execution.finishedUtc
    $marker = Read-BenchmarkEvidence $MarkerPath 1024 $startedUtc
    Require-Benchmark ($marker -cmatch '^content-rendered\t([^\t\r\n]+)\tpid=([1-9][0-9]*)$') 'Benchmark startup marker is malformed.'
    $markerUtc = [DateTimeOffset]$Matches[1]; $markerPid = [int]$Matches[2]
    Require-Benchmark ($markerPid -eq $Execution.processId -and $markerUtc -ge $startedUtc -and
        $markerUtc -le $finishedUtc) 'Benchmark startup marker belongs to another child/time window.'
    $report = ConvertFrom-Json -InputObject (Read-BenchmarkEvidence $ResultPath 1MB $startedUtc)
    $assessment = Get-BenchmarkReportAssessment $report $Execution.processId $startedUtc $finishedUtc $RenderMode $RequestedTheme
    Require-Benchmark ($markerUtc -le [DateTimeOffset]$report.completedUtc) 'Benchmark marker was written after report completion.'
    return [pscustomobject]@{ report = $report; reportSha256 = (Get-FileHash -LiteralPath $ResultPath -Algorithm SHA256).Hash
        markerSha256 = (Get-FileHash -LiteralPath $MarkerPath -Algorithm SHA256).Hash
        managedHeapEstimate = Get-BenchmarkManagedEstimateStatus $report; assessment = $assessment }
}

function Assert-BenchmarkArtifacts($Execution, [string]$ResultPath, [string]$MarkerPath, [string]$RenderMode, [string]$RequestedTheme) {
    $verified = Get-BenchmarkArtifactAssessment $Execution $ResultPath $MarkerPath $RenderMode $RequestedTheme
    Require-Benchmark $verified.assessment.idleSamplingComplete 'Benchmark idle samples did not reach the full five-second window.'
    return $verified
}

if ($SoftwareRendering -and $HardwareRendering) { throw 'Choose either -SoftwareRendering or -HardwareRendering, not both.' }
$exe = Assert-BenchmarkLocalPath $ExecutablePath
$root = Assert-BenchmarkLocalPath $OutputDirectory
$summaryPath = Assert-BenchmarkLocalPath (Join-Path $root 'benchmark-launch-summary.json')
Require-Benchmark (-not (Test-Path -LiteralPath $summaryPath) -or (Test-Path -LiteralPath $summaryPath -PathType Leaf)) 'Benchmark latest summary must be an ordinary file when present.'
Require-Benchmark (Test-Path -LiteralPath $exe -PathType Leaf) 'Benchmark executable not found.'
$repoDotnet = Join-Path $PSScriptRoot '.tools\dotnet'
if (Test-Path -LiteralPath (Join-Path $repoDotnet 'dotnet.exe')) { $env:DOTNET_ROOT = $repoDotnet }
[IO.Directory]::CreateDirectory($root) | Out-Null
$session = Join-Path $root ('session-{0:yyyyMMdd-HHmmss-fff}-{1}' -f [DateTime]::UtcNow, [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $session | Out-Null
$before = @(Get-BenchmarkProgramIdentity $exe)
$identity = ConvertTo-Json -InputObject $before -Depth 4 -Compress
$after = $null; $identityUnchanged = $false
$renderMode = if ($HardwareRendering) { 'Default' } else { 'SoftwareOnly' }
$runResults = [Collections.Generic.List[object]]::new()
for ($index = 1; $index -le $Runs; $index++) {
    $runDir = Join-Path $session ('run-{0:D2}' -f $index)
    New-Item -ItemType Directory -Path $runDir | Out-Null
    $resultPath = Join-Path $runDir 'benchmark-result.json'; $markerPath = Join-Path $runDir 'content-rendered.marker'
    $arguments = @('--benchmark', '--benchmark-output', $resultPath, '--benchmark-marker', $markerPath)
    if ($SoftwareRendering) { $arguments += '--software-rendering' }
    if ($HardwareRendering) { $arguments += '--hardware-rendering' }
    if ($Theme) { $arguments += @('--theme', $Theme) }
    $execution = $null; $verified = $null; $failure = $null
    try {
        $execution = Invoke-BenchmarkProcess $exe $arguments $markerPath $TimeoutSeconds
        $after = @(Get-BenchmarkProgramIdentity $exe)
        $identityUnchanged = $identity -ceq (ConvertTo-Json -InputObject $after -Depth 4 -Compress)
        Require-Benchmark $identityUnchanged 'Benchmark program files changed during execution.'
        $verified = Assert-BenchmarkArtifacts $execution $resultPath $markerPath $renderMode $Theme
    } catch { $failure = $_.Exception.Message }
    $runResults.Add([ordered]@{
        run = $index; success = $null -eq $failure; failure = $failure
        processId = if ($execution) { $execution.processId } else { $null }
        exitCode = if ($execution) { $execution.exitCode } else { $null }
        completed = $null -ne $execution -and $execution.completed
        timedOut = if ($execution) { $execution.timedOut } else { $null }
        cleanupFailure = if ($execution) { $execution.cleanupFailure } else { $null }
        startedUtc = if ($execution) { $execution.startedUtc } else { $null }
        finishedUtc = if ($execution) { $execution.finishedUtc } else { $null }
        elapsedSeconds = if ($execution) { $execution.elapsedSeconds } else { $null }
        launchToContentRenderedMs = if ($execution) { $execution.launchToContentRenderedMs } else { $null }
        markerPollingIntervalMs = 50; warmLaunch = $true; resultSuccess = $null -ne $verified
        resultPath = $resultPath; markerPath = $markerPath
        reportSha256 = if ($verified) { $verified.reportSha256 } else { $null }
        markerSha256 = if ($verified) { $verified.markerSha256 } else { $null }
        managedHeapEstimate = if ($verified) { $verified.managedHeapEstimate } else { $null }
    })
    if ($null -ne $failure) { break } # No retries or successful-sample substitution.
}
$successCount = @($runResults | Where-Object success -eq $true).Count
$summary = [ordered]@{
    success = $successCount -eq $Runs; generatedUtc = [DateTimeOffset]::UtcNow.ToString('O'); executable = $exe
    programFilesBefore = $before; programFilesAfter = $after; programFilesUnchanged = $identityUnchanged
    softwareRenderingRequested = [bool]$SoftwareRendering; hardwareRenderingRequested = [bool]$HardwareRendering
    productDefaultRendering = 'software'; requestedTheme = $Theme
    runs = $Runs; attemptedRuns = $runResults.Count; successfulRuns = $successCount
    completedRuns = @($runResults | Where-Object completed -eq $true).Count
    managedHeapEstimatesComplete = $successCount -eq $Runs -and @($runResults | Where-Object { $null -eq $_.managedHeapEstimate -or -not $_.managedHeapEstimate.complete }).Count -eq 0
    sessionDirectory = $session; timeoutSeconds = $TimeoutSeconds; noRetry = $true
    launchTiming = 'Process creation to observed ContentRendered marker; 50 ms marker polling and process creation overhead are included, not a WPF/compositor timestamp.'
    cachePolicy = 'OS/app caches were not cleared; warm launches, not cold boots. Predeclared warmup runs must be kept separate by the caller.'
    identityScope = 'Bounded top-level EXE/DLL/runtimeconfig/deps identity, no data traversal; not a recursive or atomic tamper-proof manifest.'
    results = $runResults.ToArray()
    acceptanceScope = 'success validates bounded child completion, marker/PID/time/identity, fixed local workload and required process/latency metrics; auxiliary managed estimates have separate explicit availability.'
}
$summaryJson = ConvertTo-Json -InputObject $summary -Depth 10
$sessionSummary = Join-Path $session 'benchmark-launch-summary.json'
$stream = [IO.File]::Open($sessionSummary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
try { $bytes = [Text.Encoding]::UTF8.GetBytes($summaryJson); $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
# This is only the latest-session convenience pointer; immutable evidence is in the new session.
$null = Assert-BenchmarkLocalPath $summaryPath
[IO.File]::WriteAllText($summaryPath, $summaryJson, [Text.UTF8Encoding]::new($false))
Write-Output $sessionSummary
if (-not $summary.success) { exit 1 }
exit 0
