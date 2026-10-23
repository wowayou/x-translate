# Synthetic report/files and fake owned children. Never launches WPF, OCR, SDK or network.
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root ('artifacts\verification\benchmark-tests-' + [Guid]::NewGuid().ToString('N')) }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a new benchmark-test directory; prior evidence is retained.' }
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'benchmark.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Benchmark runner has parse errors.' }
foreach ($name in @('Require-Benchmark', 'Assert-BenchmarkLocalPath', 'Get-BenchmarkProgramIdentity', 'Read-BenchmarkEvidence',
    'Assert-BenchmarkNumber', 'Assert-BenchmarkResources', 'Get-BenchmarkManagedEstimateStatus', 'Assert-BenchmarkReport', 'Invoke-BenchmarkProcess', 'Assert-BenchmarkArtifacts')) {
    $selected = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $true) | Where-Object Name -eq $name)
    if ($selected.Count -ne 1) { throw 'Benchmark function is not uniquely selected.' }
    . ([ScriptBlock]::Create($selected[0].Extent.Text))
}
$checks = [Collections.Generic.List[string]]::new()
function Reject([string]$Name, [scriptblock]$Action) {
    $refused = $false
    try { & $Action | Out-Null } catch { $refused = $true }
    Require-Benchmark $refused ('Invalid benchmark evidence was accepted: ' + $Name)
    $checks.Add($Name + ' is refused')
}
$started = [DateTimeOffset]::UtcNow.AddMinutes(-1); $finished = [DateTimeOffset]::UtcNow.AddMinutes(1)
function Resources {
    return [pscustomobject]@{ WorkingSetBytes = 100000000; PrivateBytes = 50000000; ManagedHeapBytes = 1000000;
        Threads = 10; Handles = 20; GdiObjects = 2; UserObjects = 3 }
}
function Change {
    return [pscustomobject]@{ before = (Resources); after = (Resources); delta = [pscustomobject]@{
        workingSetBytes = 0; privateBytes = 0; managedHeapBytes = 0; threads = 0; handles = 0; gdiObjects = 0; userObjects = 0 } }
}
function Fixture {
    $idle = [ordered]@{}
    foreach ($name in @('visible', 'hidden', 'recovery')) {
        $state = if ($name -eq 'recovery') { 'visible_after_translation' } else { $name }
        $idle[$name] = [pscustomobject]@{ State = $state; DurationMs = 5010; Start = (Resources); End = (Resources); Delta = (Change);
            Samples = @(foreach ($index in 1..25) { [pscustomobject]@{ ElapsedMs = $index * 200; Resources = (Resources); CpuPercent = 0.3 } }) }
    }
    return [pscustomobject]@{ success = $true; completedUtc = [DateTimeOffset]::UtcNow.ToString('O'); processId = 1001;
        startup = [pscustomobject]@{ loadedMs = 500; contentRenderedMs = 600; contentRenderedOneShot = $true };
        machine = [pscustomobject]@{ os = 'synthetic OS'; framework = '.NET 10.0.12'; dotnet = '10.0.12'; processArchitecture = 'X64'; processorCount = 20 };
        parameters = [pscustomobject]@{ idleSeconds = 5; idleSampleIntervalMs = 200; popupIterations = 1000; translationIterations = 200;
            translationSuccesses = 200; benchmarkData = 'isolated benchmark-data directory';
            network = 'none: injected HttpClient handler ignores request contents and returns a fixed local response';
            forcedCollection = $false; workingSetTrim = $false; processRenderMode = 'SoftwareOnly'; theme = 'Light' };
        idle = [pscustomobject]$idle;
        popup = [pscustomobject]@{ latencyMs = [pscustomobject]@{ count = 1000; p50 = 1; p90 = 2; p95 = 3; p99 = 4; max = 5 }; resources = (Change) };
        translation = [pscustomobject]@{ latencyMs = [pscustomobject]@{ count = 200; p50 = 1; p90 = 2; p95 = 3; p99 = 4; max = 5 };
            resources = (Change); successfulTranslations = 200 } }
}
function Validate($Report) { Assert-BenchmarkReport $Report 1001 $started $finished 'SoftwareOnly' 'Light' }
Validate (Fixture)
$checks.Add('Complete fixed synthetic benchmark passes actual workload, resource, timing and PID assertions')
Assert-BenchmarkReport (Fixture) 1001 $started $finished 'SoftwareOnly' 'light'
$checks.Add('Case-insensitive theme argument agrees with the product enum without relaxing mismatched-theme rejection')
$legacy = Fixture; $legacy.parameters.PSObject.Properties.Remove('theme')
Assert-BenchmarkReport $legacy 1001 $started $finished 'SoftwareOnly' ''
$checks.Add('Legacy report without theme is usable only when no explicit theme was requested')
Reject 'Legacy unverified explicit theme' { Validate $legacy }
$hardware = Fixture; $hardware.parameters.processRenderMode = 'Default'
Assert-BenchmarkReport $hardware 1001 $started $finished 'Default' 'Light'
$checks.Add('Explicit hardware-rendering report must match its requested lane')
$estimate = Fixture
$estimate.idle.recovery.Samples[0].Resources.ManagedHeapBytes = -1000
Validate $estimate
$availability = Get-BenchmarkManagedEstimateStatus $estimate
Require-Benchmark (-not $availability.complete -and $availability.invalidSamples.Count -eq 1 -and
    $availability.invalidSamples[0].rawBytes -eq -1000 -and $estimate.idle.recovery.Samples[0].Resources.ManagedHeapBytes -eq -1000) 'Invalid auxiliary estimate was concealed, clamped or used as a memory success.'
$checks.Add('Negative auxiliary managed estimate is explicitly unavailable and preserved verbatim, not treated as zero or memory savings')
Require-Benchmark ((Get-BenchmarkManagedEstimateStatus (Fixture)).complete) 'Valid managed estimates were marked unavailable.'
$checks.Add('Nonnegative auxiliary estimates retain independent availability without changing required metric acceptance')
$mutations = [ordered]@{
    'False report success' = { param($r) $r.success = $false }
    'String success instead of boolean' = { param($r) $r.success = 'true' }
    'Report from another PID' = { param($r) $r.processId = 1002 }
    'String report PID' = { param($r) $r.processId = '1001' }
    'Stale completed timestamp' = { param($r) $r.completedUtc = $started.AddSeconds(-1).ToString('O') }
    'Future completed timestamp' = { param($r) $r.completedUtc = $finished.AddSeconds(1).ToString('O') }
    'Repeated startup milestone' = { param($r) $r.startup.contentRenderedOneShot = $false }
    'Startup milestones reversed' = { param($r) $r.startup.loadedMs = 700 }
    'Missing startup time' = { param($r) $r.startup.PSObject.Properties.Remove('loadedMs') }
    'NaN startup time' = { param($r) $r.startup.loadedMs = [double]::NaN }
    'Changed popup workload' = { param($r) $r.parameters.popupIterations = 999 }
    'String popup workload' = { param($r) $r.parameters.popupIterations = '1000' }
    'Changed translation workload' = { param($r) $r.parameters.translationIterations = 199 }
    'Incomplete successful translations' = { param($r) $r.translation.successfulTranslations = 199 }
    'String successful translation count' = { param($r) $r.translation.successfulTranslations = '200' }
    'Unisolated benchmark storage' = { param($r) $r.parameters.benchmarkData = 'data' }
    'Real HTTP transport' = { param($r) $r.parameters.network = 'real HTTP' }
    'Forced collection' = { param($r) $r.parameters.forcedCollection = $true }
    'Working-set trim' = { param($r) $r.parameters.workingSetTrim = $true }
    'String false privacy flag' = { param($r) $r.parameters.forcedCollection = 'false' }
    'Wrong render mode' = { param($r) $r.parameters.processRenderMode = 'Default' }
    'Wrong explicit theme' = { param($r) $r.parameters.theme = 'Dark' }
    'Missing runtime metadata' = { param($r) $r.machine.dotnet = '' }
    'Non-string runtime metadata' = { param($r) $r.machine.dotnet = 10 }
    'Invalid processor count' = { param($r) $r.machine.processorCount = 0 }
    'Fractional processor count' = { param($r) $r.machine.processorCount = 20.5 }
    'Incomplete idle duration' = { param($r) $r.idle.hidden.DurationMs = 4999 }
    'Missing hidden idle phase' = { param($r) $r.idle.PSObject.Properties.Remove('hidden') }
    'Wrong idle state' = { param($r) $r.idle.hidden.State = 'visible' }
    'Incomplete idle sample window' = { param($r) $r.idle.hidden.Samples = @($r.idle.hidden.Samples[0]) }
    'Single late endpoint instead of idle sample series' = { param($r) $r.idle.hidden.Samples = @($r.idle.hidden.Samples[-1]) }
    'Empty idle samples' = { param($r) $r.idle.hidden.Samples = @() }
    'Out-of-order idle samples' = { param($r) $r.idle.hidden.Samples[1].ElapsedMs = 199 }
    'Sample after phase end' = { param($r) $r.idle.hidden.Samples[-1].ElapsedMs = 6000 }
    'Negative CPU' = { param($r) $r.idle.hidden.Samples[0].CpuPercent = -1 }
    'Infinite CPU' = { param($r) $r.idle.hidden.Samples[0].CpuPercent = [double]::PositiveInfinity }
    'Missing private memory' = { param($r) $r.idle.hidden.Samples[0].Resources.PSObject.Properties.Remove('PrivateBytes') }
    'Missing auxiliary managed estimate' = { param($r) $r.idle.hidden.Samples[0].Resources.PSObject.Properties.Remove('ManagedHeapBytes') }
    'NaN auxiliary managed estimate' = { param($r) $r.idle.hidden.Samples[0].Resources.ManagedHeapBytes = [double]::NaN }
    'Negative private memory' = { param($r) $r.idle.hidden.Samples[0].Resources.PrivateBytes = -1 }
    'String numeric memory' = { param($r) $r.idle.hidden.Samples[0].Resources.PrivateBytes = '50000000' }
    'Fractional memory bytes' = { param($r) $r.idle.hidden.Samples[0].Resources.PrivateBytes = 50000000.5 }
    'Fractional resource count' = { param($r) $r.idle.hidden.Samples[0].Resources.Handles = 20.5 }
    'Negative GUI resources' = { param($r) $r.idle.hidden.Samples[0].Resources.GdiObjects = -1 }
    'Wrong idle before endpoint' = { param($r) $r.idle.hidden.Delta.before.PrivateBytes++ }
    'String idle before endpoint' = { param($r) $r.idle.hidden.Delta.before.PrivateBytes = '50000000' }
    'Wrong idle delta' = { param($r) $r.idle.hidden.Delta.delta.privateBytes++ }
    'String idle delta' = { param($r) $r.idle.hidden.Delta.delta.privateBytes = '0' }
    'Incomplete popup latency count' = { param($r) $r.popup.latencyMs.count = 999 }
    'String latency count' = { param($r) $r.popup.latencyMs.count = '1000' }
    'Reversed latency quantiles' = { param($r) $r.popup.latencyMs.p95 = 1 }
    'Missing translation latency' = { param($r) $r.translation.latencyMs.PSObject.Properties.Remove('p99') }
    'Wrong loop resource delta' = { param($r) $r.popup.resources.delta.handles = 1 }
    'String loop resource delta' = { param($r) $r.popup.resources.delta.handles = '0' }
}
foreach ($mutation in $mutations.GetEnumerator()) {
    $fixture = Fixture; & $mutation.Value $fixture
    Reject $mutation.Key { Validate $fixture }
}
$evidence = Join-Path $OutputDirectory 'fixed evidence with spaces'
[IO.Directory]::CreateDirectory($evidence) | Out-Null
$resultPath = Join-Path $evidence 'benchmark-result.json'; $markerPath = Join-Path $evidence 'content-rendered.marker'
function Write-FixtureEvidence {
    [IO.File]::WriteAllText($resultPath, (ConvertTo-Json -InputObject (Fixture) -Depth 12), [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($markerPath, ('content-rendered' + "`t" + [DateTimeOffset]::UtcNow.ToString('O') + "`tpid=1001"))
}
function Execution {
    return [pscustomobject]@{ completed = $true; timedOut = $false; exitCode = 0; cleanupFailure = $null;
        markerSeen = $true; launchToContentRenderedMs = 600; processId = 1001;
        startedUtc = $started.ToString('O'); finishedUtc = $finished.ToString('O') }
}
Write-FixtureEvidence
# Report must complete after the marker, as in the real App lifecycle.
[IO.File]::WriteAllText($resultPath, (ConvertTo-Json -InputObject (Fixture) -Depth 12), [Text.UTF8Encoding]::new($false))
$receipt = Assert-BenchmarkArtifacts (Execution) $resultPath $markerPath 'SoftwareOnly' 'Light'
Require-Benchmark ($receipt.reportSha256 -cmatch '^[0-9A-F]{64}$' -and $receipt.markerSha256 -cmatch '^[0-9A-F]{64}$') 'Artifact digests are missing.'
$checks.Add('Fresh marker/report artifacts bind both SHA-256 values to the synthetic child PID/time window')
foreach ($field in @('timedOut', 'exitCode', 'completed', 'markerSeen', 'cleanupFailure')) {
    $execution = Execution
    switch ($field) {
        'timedOut' { $execution.timedOut = $true }
        'exitCode' { $execution.exitCode = 1 }
        'completed' { $execution.completed = $false }
        'markerSeen' { $execution.markerSeen = $false }
        'cleanupFailure' { $execution.cleanupFailure = 'synthetic failure' }
    }
    Reject ('Successful artifacts cannot conceal child ' + $field) { Assert-BenchmarkArtifacts $execution $resultPath $markerPath 'SoftwareOnly' 'Light' }
}
foreach ($field in @('completed', 'timedOut', 'markerSeen', 'exitCode', 'processId', 'launchToContentRenderedMs')) {
    $execution = Execution
    $execution.$field = [string]$execution.$field
    Reject ('String child field ' + $field) { Assert-BenchmarkArtifacts $execution $resultPath $markerPath 'SoftwareOnly' 'Light' }
}
$execution = Execution; $execution.launchToContentRenderedMs = $null
Reject 'Missing external launch measurement' { Assert-BenchmarkArtifacts $execution $resultPath $markerPath 'SoftwareOnly' 'Light' }
[IO.File]::WriteAllText($markerPath, ('content-rendered' + "`t" + [DateTimeOffset]::UtcNow.ToString('O') + "`tpid=1002"))
Reject 'Marker from another PID' { Assert-BenchmarkArtifacts (Execution) $resultPath $markerPath 'SoftwareOnly' 'Light' }
[IO.File]::WriteAllText($markerPath, ('content-rendered' + "`t" + $started.AddSeconds(-1).ToString('O') + "`tpid=1001"))
Reject 'Marker outside execution time' { Assert-BenchmarkArtifacts (Execution) $resultPath $markerPath 'SoftwareOnly' 'Light' }
[IO.File]::WriteAllText($markerPath, 'not a ContentRendered marker')
Reject 'Malformed marker' { Assert-BenchmarkArtifacts (Execution) $resultPath $markerPath 'SoftwareOnly' 'Light' }
Write-FixtureEvidence
[IO.File]::SetLastWriteTimeUtc($resultPath, $started.UtcDateTime.AddSeconds(-1))
Reject 'Report file predates invocation' { Read-BenchmarkEvidence $resultPath 1MB $started }
[IO.File]::WriteAllText($resultPath, '{broken JSON')
Reject 'Malformed JSON' { Assert-BenchmarkArtifacts (Execution) $resultPath $markerPath 'SoftwareOnly' 'Light' }
$oversized = Join-Path $evidence 'oversized.json'
$stream = [IO.File]::Create($oversized); try { $stream.SetLength(1MB + 1) } finally { $stream.Dispose() }
Reject 'Oversized report before parsing' { Read-BenchmarkEvidence $oversized 1MB $started }
Reject 'Missing evidence file' { Read-BenchmarkEvidence (Join-Path $evidence 'missing.json') 1MB $started }
$empty = Join-Path $evidence 'empty.json'; [IO.File]::WriteAllText($empty, '')
Reject 'Empty evidence file' { Read-BenchmarkEvidence $empty 1MB $started }
Reject 'UNC/network output' { Assert-BenchmarkLocalPath '\\fixed.invalid\share\benchmark.json' }
$program = Join-Path $OutputDirectory 'program'; [IO.Directory]::CreateDirectory($program) | Out-Null
$exe = Join-Path $program 'XTranslate.exe'; $dll = Join-Path $program 'XTranslate.dll'
[IO.File]::WriteAllBytes($exe, [byte[]]@(1,2)); [IO.File]::WriteAllBytes($dll, [byte[]]@(3,4))
[IO.File]::WriteAllText((Join-Path $program 'XTranslate.runtimeconfig.json'), '{}')
[IO.Directory]::CreateDirectory((Join-Path $program 'data')) | Out-Null
[IO.File]::WriteAllText((Join-Path $program 'data\settings.json'), 'synthetic-only')
$before = @(Get-BenchmarkProgramIdentity $exe)
Require-Benchmark ($before.Count -eq 3) 'Flat identity omitted Lite code/runtime or traversed data.'
[IO.File]::WriteAllBytes($dll, [byte[]]@(4,3))
Require-Benchmark ((ConvertTo-Json -InputObject $before -Depth 4 -Compress) -cne
    (ConvertTo-Json -InputObject @(Get-BenchmarkProgramIdentity $exe) -Depth 4 -Compress)) 'Same-length DLL mutation retained identity.'
$checks.Add('Flat identity includes Lite DLL/runtime and detects same-length changes without hashing data')
$link = Join-Path $OutputDirectory 'junction'
& $env:ComSpec /d /c "mklink /J `"$link`" `"$evidence`"" | Out-Null
Require-Benchmark ($LASTEXITCODE -eq 0) 'Could not create owned junction fixture.'
Reject 'Junction output root' { Assert-BenchmarkLocalPath $link }
Reject 'Junction child evidence' { Assert-BenchmarkLocalPath (Join-Path $link 'benchmark-result.json') }
for ($index = 1; $index -le 509; $index++) { [IO.File]::WriteAllText((Join-Path $program ($index.ToString() + '.txt')), '') }
Reject '513th flat entry' { Get-BenchmarkProgramIdentity $exe }

# Test real launch/wait/timeout/disposal code with only process creation replaced.
$script:fake = $null; $script:launches = 0
$global:benchmarkTestLaunchDelayMilliseconds = 0
$global:benchmarkTestEntryMode = $false; $global:benchmarkTestEntryBadReport = $false; $global:benchmarkTestEntryChangeCode = $false
$global:benchmarkTestEntryExe = $null; $global:benchmarkTestEntryFake = $null; $global:benchmarkTestLaunches = 0
function Start-Process {
    param($FilePath, $ArgumentList, $WindowStyle, [switch]$PassThru)
    if ($global:benchmarkTestEntryMode) {
        Require-Benchmark ($FilePath -eq $global:benchmarkTestEntryExe -and $PassThru -and
            $ArgumentList -cmatch '^"--benchmark" "--benchmark-output" "([^"]+)" "--benchmark-marker" "([^"]+)" "--theme" "Light"$') 'Benchmark entry-point launch changed.'
        $result = $Matches[1]; $marker = $Matches[2]
        [IO.File]::WriteAllText($marker, ('content-rendered' + "`t" + [DateTimeOffset]::UtcNow.ToString('O') + "`tpid=1001"))
        $report = Fixture
        if ($global:benchmarkTestEntryBadReport) { $report.success = 'true' }
        [IO.File]::WriteAllText($result, (ConvertTo-Json -InputObject $report -Depth 12), [Text.UTF8Encoding]::new($false))
        if ($global:benchmarkTestEntryChangeCode) { [IO.File]::WriteAllBytes([IO.Path]::ChangeExtension($global:benchmarkTestEntryExe, '.dll'), [byte[]]@(4,3)) }
    } else {
        Require-Benchmark ($FilePath -eq 'fixed-benchmark.exe' -and
            $ArgumentList -ceq '"--benchmark" "--benchmark-output" "D:\fixed output\result.json"' -and $PassThru) 'Benchmark quoting or launch changed.'
        [IO.File]::WriteAllText($script:processMarker, 'synthetic marker')
    }
    $global:benchmarkTestLaunches++
    if ($global:benchmarkTestLaunchDelayMilliseconds) { Start-Sleep -Milliseconds $global:benchmarkTestLaunchDelayMilliseconds }
    return $(if ($global:benchmarkTestEntryMode) { $global:benchmarkTestEntryFake } else { $script:fake })
}
function Fake([int]$ExitCode, [bool]$Timeout, [bool]$ExitDuringCleanup = $false) {
    $process = [pscustomobject]@{ Handle = 1; Id = 1001; ExitCode = $ExitCode; HasExited = -not $Timeout;
        killed = $false; killCount = 0; disposed = $false; waits = @(); exitDuringCleanup = $ExitDuringCleanup;
        waitDelayMilliseconds = 0; killThrows = $false; exitAfterKill = $true }
    $process | Add-Member ScriptMethod WaitForExit { param($milliseconds)
        $this.waits += $milliseconds
        if ($this.waitDelayMilliseconds) { Start-Sleep -Milliseconds $this.waitDelayMilliseconds; $this.HasExited = $true }
        if ($this.exitDuringCleanup -and $this.waits.Count -eq 1) { $this.HasExited = $true; return $false }
        return $this.HasExited
    }
    $process | Add-Member ScriptMethod Kill {
        $this.killed = $true; $this.killCount++
        if ($this.killThrows) { throw 'Synthetic kill failure' }
        $this.HasExited = $this.exitAfterKill
    }
    $process | Add-Member ScriptMethod Dispose { $this.disposed = $true }
    return $process
}
foreach ($code in @(0,1)) {
    $script:processMarker = Join-Path $evidence ('fake-' + $code + '.marker')
    $script:fake = Fake $code $false
    $execution = Invoke-BenchmarkProcess 'fixed-benchmark.exe' @('--benchmark','--benchmark-output','D:\fixed output\result.json') $script:processMarker 45
    Require-Benchmark ($execution.exitCode -eq $code -and -not $execution.timedOut -and $execution.markerSeen -and
        $script:fake.disposed -and -not $script:fake.killed) 'Fast child exit or cleanup was hidden.'
    $checks.Add("Fast owned child retains exit $code/PID/marker and disposes its handle")
}
$script:processMarker = Join-Path $evidence 'fake-timeout.marker'; $script:fake = Fake 0 $true
$execution = Invoke-BenchmarkProcess 'fixed-benchmark.exe' @('--benchmark','--benchmark-output','D:\fixed output\result.json') $script:processMarker 45
Require-Benchmark ($execution.timedOut -and $script:fake.killed -and $script:fake.disposed -and
    $script:fake.waits[0] -le 45000 -and $script:fake.waits[1] -eq 5000) 'Timed-out child did not retain failure/bounded cleanup.'
$checks.Add('Timeout remains failure even when kill observes exit 0; cleanup is limited to returned child and five-second wait')
$script:processMarker = Join-Path $evidence 'fake-deadline-exit.marker'; $script:fake = Fake 0 $true $true
$execution = Invoke-BenchmarkProcess 'fixed-benchmark.exe' @('--benchmark','--benchmark-output','D:\fixed output\result.json') $script:processMarker 45
Require-Benchmark ($execution.timedOut -and $execution.exitCode -eq 0 -and -not $script:fake.killed -and $script:fake.disposed) 'Exit immediately after a failed deadline wait became success.'
$checks.Add('Exit 0 after deadline is still rejected without unnecessary kill or retry')
Require-Benchmark ($global:benchmarkTestLaunches -eq 4) 'Unexpected process retry.'
Reject 'Existing marker before child allocation' {
    Invoke-BenchmarkProcess 'fixed-benchmark.exe' @('--benchmark','--benchmark-output','D:\fixed output\result.json') $script:processMarker 45
}
Require-Benchmark ($global:benchmarkTestLaunches -eq 4) 'Existing marker allocated a child.'
$script:processMarker = Join-Path $evidence 'fake-late-launch.marker'
$script:fake = Fake 0 $false; $global:benchmarkTestLaunchDelayMilliseconds = 1100
try { $execution = Invoke-BenchmarkProcess 'fixed-benchmark.exe' @('--benchmark','--benchmark-output','D:\fixed output\result.json') $script:processMarker 1 }
finally { $global:benchmarkTestLaunchDelayMilliseconds = 0 }
Require-Benchmark ($execution.timedOut -and $execution.exitCode -eq 0 -and -not $script:fake.killed -and $script:fake.disposed) 'Slow process creation bypassed the wall-clock deadline.'
$checks.Add('Process creation itself consumes the deadline; a late exited child is not successful')
$script:processMarker = Join-Path $evidence 'fake-late-wait.marker'
$script:fake = Fake 0 $true; $script:fake.waitDelayMilliseconds = 1100
$execution = Invoke-BenchmarkProcess 'fixed-benchmark.exe' @('--benchmark','--benchmark-output','D:\fixed output\result.json') $script:processMarker 1
Require-Benchmark ($execution.timedOut -and $execution.exitCode -eq 0 -and -not $script:fake.killed -and $script:fake.disposed) 'Late true WaitForExit bypassed the wall-clock deadline.'
$checks.Add('WaitForExit returning true after the wall-clock deadline still fails without a retry')
foreach ($failure in @('kill', 'wait')) {
    $script:processMarker = Join-Path $evidence ('fake-cleanup-' + $failure + '.marker')
    $script:fake = Fake 0 $true
    if ($failure -eq 'kill') { $script:fake.killThrows = $true } else { $script:fake.exitAfterKill = $false }
    $execution = Invoke-BenchmarkProcess 'fixed-benchmark.exe' @('--benchmark','--benchmark-output','D:\fixed output\result.json') $script:processMarker 45
    Require-Benchmark ($execution.timedOut -and -not $execution.completed -and $null -ne $execution.cleanupFailure -and
        $script:fake.killCount -eq 1 -and $script:fake.disposed -and $script:fake.waits.Count -eq $(if ($failure -eq 'kill') { 1 } else { 2 })) 'Failed cleanup was hidden or retried by finally.'
    $checks.Add('Failed cleanup ' + $failure + ' stays explicit with one kill attempt and at most one five-second cleanup wait')
}

# Exercise the real script entry point as a child scope, with only Start-Process replaced.
$global:benchmarkTestEntryMode = $true
$entryProgram = Join-Path $OutputDirectory 'entry program'; [IO.Directory]::CreateDirectory($entryProgram) | Out-Null
$global:benchmarkTestEntryExe = Join-Path $entryProgram 'XTranslate.exe'
[IO.File]::WriteAllBytes($global:benchmarkTestEntryExe, [byte[]]@(1,2))
[IO.File]::WriteAllBytes([IO.Path]::ChangeExtension($global:benchmarkTestEntryExe, '.dll'), [byte[]]@(3,4))
$entryOutput = Join-Path $OutputDirectory 'entry reports'
$script:fake = Fake 0 $false; $global:benchmarkTestEntryFake = $script:fake; $beforeLaunches = $global:benchmarkTestLaunches
& (Join-Path $root 'benchmark.ps1') -ExecutablePath $global:benchmarkTestEntryExe -OutputDirectory $entryOutput -Runs 3 -Theme Light | Out-Null
Require-Benchmark ($LASTEXITCODE -eq 0 -and $global:benchmarkTestLaunches - $beforeLaunches -eq 3) 'Entry point did not run exactly three sequential synthetic children.'
$latest = Join-Path $entryOutput 'benchmark-launch-summary.json'
$summary = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($latest))
$immutable = Join-Path $summary.sessionDirectory 'benchmark-launch-summary.json'
Require-Benchmark ($summary.success -eq $true -and $summary.attemptedRuns -eq 3 -and $summary.successfulRuns -eq 3 -and
    $summary.completedRuns -eq 3 -and $summary.programFilesUnchanged -eq $true -and $summary.managedHeapEstimatesComplete -eq $true -and
    [IO.File]::ReadAllText($latest) -ceq [IO.File]::ReadAllText($immutable)) 'Entry summary count, metrics, identity or immutable evidence differs.'
$immutableHash = (Get-FileHash -LiteralPath $immutable -Algorithm SHA256).Hash
$checks.Add('Actual entry point records three sequential synthetic children with complete counts, identities, digests and an immutable session summary')
$global:benchmarkTestEntryBadReport = $true; $beforeLaunches = $global:benchmarkTestLaunches
& (Join-Path $root 'benchmark.ps1') -ExecutablePath $global:benchmarkTestEntryExe -OutputDirectory $entryOutput -Runs 3 -Theme Light | Out-Null
Require-Benchmark ($LASTEXITCODE -eq 1 -and $global:benchmarkTestLaunches - $beforeLaunches -eq 1) 'Entry point retried/replaced a rejected report or returned success.'
$summary = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($latest))
Require-Benchmark ($summary.success -eq $false -and $summary.attemptedRuns -eq 1 -and $summary.successfulRuns -eq 0 -and
    $summary.completedRuns -eq 1 -and -not $summary.managedHeapEstimatesComplete -and $null -ne $summary.results[0].failure -and
    (Get-FileHash -LiteralPath $immutable -Algorithm SHA256).Hash -ceq $immutableHash) 'Rejected child lost evidence/counts or overwrote the previous immutable summary.'
$checks.Add('Actual entry point stops at its first rejected report, preserves completed-vs-successful counts and prior evidence, and exits 1')
$global:benchmarkTestEntryBadReport = $false; $global:benchmarkTestEntryChangeCode = $true; $beforeLaunches = $global:benchmarkTestLaunches
& (Join-Path $root 'benchmark.ps1') -ExecutablePath $global:benchmarkTestEntryExe -OutputDirectory (Join-Path $OutputDirectory 'changed code reports') -Runs 3 -Theme Light | Out-Null
$summary = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText((Join-Path $OutputDirectory 'changed code reports\benchmark-launch-summary.json')))
Require-Benchmark ($LASTEXITCODE -eq 1 -and $global:benchmarkTestLaunches - $beforeLaunches -eq 1 -and
    $summary.success -eq $false -and -not $summary.programFilesUnchanged -and $summary.attemptedRuns -eq 1) 'Changed Lite DLL identity was accepted or retried by the entry point.'
$checks.Add('Actual entry point rejects same-length Lite DLL mutation after one child, preserves changed identity and never retries')
$global:benchmarkTestEntryChangeCode = $false
$linkOutput = Join-Path $OutputDirectory 'linked latest summary'; [IO.Directory]::CreateDirectory($linkOutput) | Out-Null
$latestLink = Join-Path $linkOutput 'benchmark-launch-summary.json'
& $env:ComSpec /d /c "mklink /J `"$latestLink`" `"$evidence`"" | Out-Null
Require-Benchmark ($LASTEXITCODE -eq 0) 'Could not create owned latest-summary junction.'
$beforeLaunches = $global:benchmarkTestLaunches
Reject 'Linked latest summary at actual preflight' {
    & (Join-Path $root 'benchmark.ps1') -ExecutablePath $global:benchmarkTestEntryExe -OutputDirectory $linkOutput -Runs 3 -Theme Light | Out-Null
}
Require-Benchmark ($global:benchmarkTestLaunches -eq $beforeLaunches -and @(Get-ChildItem -LiteralPath $linkOutput -Force).Count -eq 1) 'Unsafe latest summary allocated a child/session before rejection.'
$checks.Add('Linked latest-summary preflight fails before allocating any child or session')
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'benchmark-tests.json'), (ConvertTo-Json -InputObject ([ordered]@{
    success = $true; count = $checks.Count; checks = $checks; syntheticOnly = $true; source = 'benchmark.ps1 actual functions' }) -Depth 6), [Text.UTF8Encoding]::new($false))
$checks | ForEach-Object { Write-Output ('PASS ' + $_) }
Write-Output ("{0}/{0} benchmark verifier checks passed" -f $checks.Count)
