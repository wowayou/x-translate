[CmdletBinding()]
param(
    [ValidateRange(1, 20)][int]$Runs = 3,
    [ValidateRange(1, 100)][int]$DiagnosticBatches = 5,
    [ValidateRange(1, 600)][int]$DiagnosticIdleSeconds = 5,
    [ValidateRange(0, 600)][int]$DiagnosticSettleSeconds = 3,
    [ValidateRange(1, 600)][int]$DiagnosticWindowSeconds = 30,
    [ValidateRange(0, 600)][int]$DiagnosticHoldSeconds = 0,
    [ValidateRange(30, 600)][int]$TimeoutSeconds = 180,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts\memory-diagnostics'),
    [switch]$SelfContained,
    [switch]$FrameworkDependent,
    [switch]$SoftwareRendering,
    [switch]$HardwareRendering,
    [string]$VMMapPath
)

$ErrorActionPreference = 'Stop'
if ($SelfContained -and $FrameworkDependent) { throw 'Choose either -SelfContained or -FrameworkDependent, not both.' }
if ($SoftwareRendering -and $HardwareRendering) { throw 'Choose either -SoftwareRendering or -HardwareRendering, not both.' }
$projectRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$sdk = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk -PathType Leaf)) { $sdk = (Get-Command dotnet -ErrorAction Stop).Source }
$isSelfContained = -not $FrameworkDependent
$deploymentLabel = if ($isSelfContained) { 'self-contained' } else { 'framework-dependent' }

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$session = Join-Path $outputRoot ('session-{0:yyyyMMdd-HHmmss-fff}-{1}' -f [DateTime]::UtcNow, ([Guid]::NewGuid().ToString('N').Substring(0, 8)))
$appPublish = Join-Path $session 'app'
$baselinePublish = Join-Path $session 'baseline'
New-Item -ItemType Directory -Force -Path $appPublish,$baselinePublish | Out-Null
$cliHome = Join-Path $session 'dotnet-cli-home'
$env:DOTNET_CLI_HOME = $cliHome
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

function Publish-DiagnosticProject {
    param([string]$Project, [string]$Destination)
    $contained = if ($isSelfContained) { 'true' } else { 'false' }
    & $sdk publish $Project -c Release -r win-x64 --self-contained $contained -p:DebugType=None -p:DebugSymbols=false -o $Destination --nologo
    if ($LASTEXITCODE -ne 0) { throw "Diagnostic publish failed: $Project" }
}

function Quote-ArgumentLine {
    param([string[]]$Arguments)
    return (($Arguments | ForEach-Object { '"' + ([string]$_).Replace('"', '\"') + '"' }) -join ' ')
}

function Invoke-DiagnosticRun {
    param(
        [string]$Group,
        [string]$Executable,
        [string[]]$ModeArguments,
        [int]$RunNumber
    )
    $runDir = Join-Path $session (Join-Path $Group ('run-{0:D2}' -f $RunNumber))
    New-Item -ItemType Directory -Force -Path $runDir | Out-Null
    $reportPath = Join-Path $runDir 'memory-diagnostics.json'
    $arguments = @('--memory-diagnostics', '--benchmark-output', $reportPath,
        '--diagnostic-batches', [string]$DiagnosticBatches,
        '--diagnostic-idle-seconds', [string]$DiagnosticIdleSeconds,
        '--diagnostic-settle-seconds', [string]$DiagnosticSettleSeconds,
        '--diagnostic-window-seconds', [string]$DiagnosticWindowSeconds,
        '--diagnostic-hold-seconds', [string]$DiagnosticHoldSeconds) + $ModeArguments
    if ($SoftwareRendering) { $arguments += '--software-rendering' }
    if ($HardwareRendering) { $arguments += '--hardware-rendering' }
    $argumentLine = Quote-ArgumentLine $arguments
    $command = [ordered]@{
        startedUtc = [DateTimeOffset]::UtcNow
        group = $Group
        run = $RunNumber
        executable = $Executable
        arguments = $arguments
        argumentLine = $argumentLine
        deployment = $deploymentLabel
        softwareRenderingRequested = [bool]$SoftwareRendering
        hardwareRenderingRequested = [bool]$HardwareRendering
        productDefaultRendering = 'software'
        timeoutSeconds = $TimeoutSeconds
    }
    $command | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runDir 'command.json') -Encoding UTF8

    $process = $null
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        $process = Start-Process -FilePath $Executable -ArgumentList $argumentLine -WindowStyle Hidden -PassThru
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill() } catch { }
            $process.WaitForExit(5000) | Out-Null
            throw "Diagnostic run timed out; only PID $($process.Id) was terminated."
        }
        if ($process.ExitCode -ne 0) {
            throw "Diagnostic process exited with code $($process.ExitCode): $Executable"
        }
        if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
            throw "Diagnostic process exited without writing report: $reportPath"
        }
        $report = $null
        try { $report = Get-Content -Raw -LiteralPath $reportPath | ConvertFrom-Json } catch { throw "Invalid diagnostic JSON: $reportPath" }
        if (-not [bool]$report.success) { throw "Diagnostic report failed: $reportPath" }
        $measurementPhases = @($report.phases | Where-Object { $_.phase -eq 'hidden-measurement' })
        if ($measurementPhases.Count -ne 1) { throw "Diagnostic report must contain exactly one hidden-measurement phase: $reportPath" }
        $measurement = $measurementPhases[0]
        if ([int]$measurement.configuredSampleCount -ne $DiagnosticWindowSeconds) {
            throw "Diagnostic hidden-measurement configured sample count does not match runner configuration: $reportPath"
        }
        if ($null -eq $measurement.statistics) { throw "Diagnostic hidden-measurement statistics are missing: $reportPath" }
        if ([string]::IsNullOrWhiteSpace([string]$measurement.targetStatus)) { throw "Diagnostic hidden-measurement targetStatus is missing: $reportPath" }
        return [ordered]@{
            group = $Group
            run = $RunNumber
            processId = $process.Id
            exitCode = $process.ExitCode
            elapsedMs = [Math]::Round($watch.Elapsed.TotalMilliseconds, 3)
            report = $reportPath
            command = (Join-Path $runDir 'command.json')
            phase = 'hidden-measurement'
            targetStatus = [string]$measurement.targetStatus
            measurementComplete = [bool]$measurement.statistics.measurementComplete
            window = [ordered]@{
                configuredSettleSeconds = [int]$measurement.configuredSettleSeconds
                actualSettleMs = [double]$measurement.actualSettleMs
                configuredSampleCount = [int]$measurement.configuredSampleCount
                actualSampleCount = [int]$measurement.actualSampleCount
                actualDurationMs = [double]$measurement.actualDurationMs
                isVisible = [bool]$measurement.isVisible
                statistics = $measurement.statistics
            }
            success = $true
        }
    } finally {
        $watch.Stop()
        if ($null -ne $process) { $process.Dispose() }
    }
}

try {
    # Build only into this new session. Never invoke build.ps1 or reuse the 0.2
    # release directory, preserving the release artifact and any user data.
    Publish-DiagnosticProject (Join-Path $projectRoot 'src\XTranslate.App\XTranslate.App.csproj') $appPublish
    Publish-DiagnosticProject (Join-Path $projectRoot 'tools\MemoryBaseline\MemoryBaseline.csproj') $baselinePublish
    $appExecutable = Join-Path $appPublish 'XTranslate.exe'
    $baselineExecutable = Join-Path $baselinePublish 'MemoryBaseline.exe'
    if (-not (Test-Path -LiteralPath $appExecutable -PathType Leaf)) { throw "Published app executable not found: $appExecutable" }
    if (-not (Test-Path -LiteralPath $baselineExecutable -PathType Leaf)) { throw "Published baseline executable not found: $baselineExecutable" }

    $vmMap = [ordered]@{
        status = 'not-collected'
        reason = 'deferred-by-user'
        suppliedPath = $VMMapPath
        note = 'No VMMap process was invoked, no download was attempted, and no EULA was accepted.'
    }
    $results = [Collections.Generic.List[object]]::new()
    $groups = @(
        [pscustomobject]@{ Name = 'app'; Executable = $appExecutable; Arguments = [string[]]@() },
        [pscustomobject]@{ Name = 'baseline-blank'; Executable = $baselineExecutable; Arguments = [string[]]@('--diagnostic-mode', 'blank') },
        [pscustomobject]@{ Name = 'baseline-tray'; Executable = $baselineExecutable; Arguments = [string[]]@('--diagnostic-mode', 'tray') }
    )
    foreach ($group in $groups) {
        for ($run = 1; $run -le $Runs; $run++) {
            # Groups and runs are intentionally sequential; diagnostic processes
            # must never compete for memory or tray state.
            $results.Add((Invoke-DiagnosticRun $group.Name $group.Executable $group.Arguments $run))
        }
    }
    $summary = [ordered]@{
        success = $true
        generatedUtc = [DateTimeOffset]::UtcNow
        sessionDirectory = $session
        deployment = $deploymentLabel
        runtimeIdentifier = 'win-x64'
        runsPerGroup = $Runs
        diagnosticBatches = $DiagnosticBatches
        diagnosticIdleSeconds = $DiagnosticIdleSeconds
        diagnosticSettleSeconds = $DiagnosticSettleSeconds
        diagnosticWindowSeconds = $DiagnosticWindowSeconds
        diagnosticHoldSeconds = $DiagnosticHoldSeconds
        softwareRenderingRequested = [bool]$SoftwareRendering
        hardwareRenderingRequested = [bool]$HardwareRendering
        productDefaultRendering = 'software'
        groups = @('app', 'baseline-blank', 'baseline-tray')
        vmmap = $vmMap
        results = $results
        targetStatus = 'not-evaluated'
        note = 'Built and run in an isolated session; no build.ps1 or final 0.2 release path was used.'
    }
    $summaryPath = Join-Path $session 'memory-diagnostics-summary.json'
    $summary | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
    Write-Output $summaryPath
}
catch {
    $failure = [ordered]@{
        success = $false
        generatedUtc = [DateTimeOffset]::UtcNow
        sessionDirectory = $session
        deployment = $deploymentLabel
        error = $_.Exception.ToString()
        vmmap = [ordered]@{ status = 'not-collected'; reason = 'deferred-by-user' }
    }
    $failure | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $session 'memory-diagnostics-summary.json') -Encoding UTF8
    throw
}
