$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$outputRoot = Join-Path $root ('artifacts/verification/memory-acceptance-tests-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $outputRoot | Out-Null
$script:caseNumber = 0

function New-Session([string]$Name, [long]$Pws, [long]$Private, [string]$Deployment = 'self-contained') {
    $directory = Join-Path $outputRoot $Name
    New-Item -ItemType Directory -Path $directory | Out-Null
    $runs = @(foreach ($run in 1..3) {
        $samples = @(foreach ($second in 1..30) {
            @{ second = $second; atMs = $second * 1000; snapshot = @{
                CapturedUtc = ([DateTimeOffset]'2026-09-29T00:00:00Z').AddMinutes($script:caseNumber * 10 + $run).AddSeconds($second).ToString('O')
                WorkingSetBytes = 200MB; PrivateBytes = $Private; PrivateWorkingSetBytes = $Pws; PrivateWorkingSetFailureReason = $null
            } }
        })
        $stats = @{
            MeasurementComplete = $true; IsComplete = $true; ConfiguredSampleCount = 30; ActualSampleCount = 30
            PrivateBytesStartBytes = $Private; PrivateBytesEndBytes = $Private; PrivateBytesDeltaBytes = 0
        }
        foreach ($metric in @(@('WorkingSet', 200MB), @('PrivateBytes', $Private), @('PrivateWorkingSet', $Pws))) {
            $stats[$metric[0]] = @{ ValidSamples = 30; MissingSamples = 0; MinBytes = $metric[1]; MaxBytes = $metric[1]; MedianBytes = $metric[1] }
        }
        $window = @{
            phase = 'hidden-measurement'; isVisible = $false; configuredSettleSeconds = 3; actualSettleMs = 3000
            configuredSampleCount = 30; actualSampleCount = 30; actualDurationMs = 30000; samples = $samples; statistics = $stats
        }
        $reportPath = Join-Path $directory "run-$run.json"
        @{
            syntheticTestFixture = $true; success = $true; phase = 'complete'; processId = $script:caseNumber * 10 + $run
            configuration = @{
                mode = 'memory-diagnostics'; batches = 1; idleSeconds = 1; holdSeconds = 0; settleSeconds = 3; windowSeconds = 30
                forcedCollection = $false; workingSetTrim = $false; os = 'test-os'; runtime = 'test-runtime'; processArchitecture = 'X64'
                gcServer = $false; gcLatencyMode = 'Interactive'; processRenderMode = 'Default'
            }
            phases = @(@{ phase = 'visible-idle' }, $window, @{ phase = 'batch-01' })
        } | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $reportPath
        @{ group = 'app'; run = $run; success = $true; exitCode = 0; measurementComplete = $true
            processId = $script:caseNumber * 10 + $run; report = $reportPath; window = @{statistics = $stats} }
    })
    $path = Join-Path $directory 'summary.json'
    @{ success = $true; deployment = $Deployment; runtimeIdentifier = 'win-x64'; runsPerGroup = 3
        diagnosticBatches = 1; diagnosticIdleSeconds = 1; diagnosticSettleSeconds = 3; diagnosticWindowSeconds = 30
        diagnosticHoldSeconds = 0; results = $runs } | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $path
    $script:caseNumber++
    return $path
}

function Check([string]$Name, [long]$Pws, [long]$Private, [string]$Expected, [scriptblock]$Mutate = {}, [string]$Deployment = 'self-contained') {
    $reference = New-Session "$script:caseNumber-reference" 100MB 100MB $Deployment
    $candidate = New-Session "$script:caseNumber-candidate" $Pws $Private $Deployment
    & $Mutate $candidate
    $output = Join-Path $outputRoot "$script:caseNumber-result.json"
    $failed = $false
    try { & (Join-Path $root 'memory-acceptance.ps1') -ReferenceSummaryPath $reference -CandidateSummaryPath $candidate -OutputPath $output -Deployment $Deployment | Out-Null }
    catch { $failed = $true }
    $actual = if (Test-Path -LiteralPath $output) { (Get-Content -Raw -LiteralPath $output | ConvertFrom-Json).targetStatus } else { 'invalid' }
    if ($actual -ne $Expected -or ($failed -ne ($Expected -ne 'passed'))) { throw "$Name expected $Expected, got $actual (failed=$failed)" }
    Write-Output "PASS $Name"
}

Check 'Complete windows pass' 59MB 100MB 'passed'
Check 'Exactly 60 MiB fails strict PWS limit' 60MB 100MB 'failed'
Check 'Exactly +5% passes inclusive guardrail' 59MB 105MB 'passed'
Check 'One byte over guardrail fails' 59MB (105MB + 1) 'failed'
Check 'High PWS cannot be hidden by lower Private Bytes' 61MB 80MB 'failed'
Check 'Missing PWS cannot pass' 59MB 100MB 'invalid' {
    param($path)
    $s = Get-Content -Raw $path | ConvertFrom-Json
    $r = Get-Content -Raw $s.results[0].report | ConvertFrom-Json
    $r.phases[1].samples[0].snapshot.PrivateWorkingSetBytes = $null
    $r | ConvertTo-Json -Depth 15 | Set-Content $s.results[0].report
}
Check 'Raw/statistics disagreement cannot pass' 59MB 100MB 'invalid' {
    param($path)
    $s = Get-Content -Raw $path | ConvertFrom-Json
    $r = Get-Content -Raw $s.results[0].report | ConvertFrom-Json
    $r.phases[1].samples[0].snapshot.PrivateWorkingSetBytes = 1MB
    $r | ConvertTo-Json -Depth 15 | Set-Content $s.results[0].report
}
Check 'Different sampling conditions cannot pass' 59MB 100MB 'invalid' {
    param($path)
    $s = Get-Content -Raw $path | ConvertFrom-Json
    $s.diagnosticIdleSeconds = 2
    $s | ConvertTo-Json -Depth 15 | Set-Content $path
}
Check 'Duplicate report cannot count as another run' 59MB 100MB 'invalid' {
    param($path)
    $s = Get-Content -Raw $path | ConvertFrom-Json
    $s.results[1].report = $s.results[0].report
    $s | ConvertTo-Json -Depth 15 | Set-Content $path
}
Check 'Incomplete run cannot pass' 59MB 100MB 'invalid' {
    param($path)
    $s = Get-Content -Raw $path | ConvertFrom-Json
    $s.results[0].measurementComplete = $false
    $s | ConvertTo-Json -Depth 15 | Set-Content $path
}
Check 'Lite windows use the same budget with explicit deployment' 59MB 100MB 'passed' {} 'framework-dependent'
Check 'Lite exactly 60 MiB still fails' 60MB 100MB 'failed' {} 'framework-dependent'
Check 'Different deployment types cannot be compared' 59MB 100MB 'invalid' {
    param($path)
    $s = Get-Content -Raw $path | ConvertFrom-Json
    $s.deployment = 'framework-dependent'
    $s | ConvertTo-Json -Depth 15 | Set-Content $path
}
Write-Output '13/13 memory acceptance checks passed (synthetic fixtures only).'
