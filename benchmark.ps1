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
if ($SoftwareRendering -and $HardwareRendering) { throw 'Choose either -SoftwareRendering or -HardwareRendering, not both.' }
$exe = [IO.Path]::GetFullPath($ExecutablePath)
$repoDotnet = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '.tools\dotnet'
if (Test-Path -LiteralPath (Join-Path $repoDotnet 'dotnet.exe')) { $env:DOTNET_ROOT = $repoDotnet } # Framework-dependent builds
$root = [IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Executable not found: $exe"
}
New-Item -ItemType Directory -Force -Path $root | Out-Null
$session = Join-Path $root ("session-{0:yyyyMMdd-HHmmss-fff}-{1}" -f [DateTime]::UtcNow, ([Guid]::NewGuid().ToString('N').Substring(0, 8)))
New-Item -ItemType Directory -Force -Path $session | Out-Null

$runResults = [Collections.Generic.List[object]]::new()
$fatalFailure = $false
for ($index = 1; $index -le $Runs; $index++) {
    $runDir = Join-Path $session ("run-{0:D2}" -f $index)
    New-Item -ItemType Directory -Force -Path $runDir | Out-Null
    $resultPath = Join-Path $runDir 'benchmark-result.json'
    $markerPath = Join-Path $runDir 'content-rendered.marker'
    $arguments = @('--benchmark', '--benchmark-output', $resultPath, '--benchmark-marker', $markerPath)
    if ($SoftwareRendering) { $arguments += '--software-rendering' }
    if ($HardwareRendering) { $arguments += '--hardware-rendering' }
    if ($Theme) { $arguments += @('--theme', $Theme) }
    # Start-Process joins ArgumentList into one command line. Quote every value so
    # output paths containing spaces remain a single argument.
    $argumentLine = ($arguments | ForEach-Object { '"' + ([string]$_).Replace('"', '\"') + '"' }) -join ' '

    # The timer starts immediately before process creation. Marker polling adds a
    # bounded, explicitly reported observation error; this is warm launch, not cold boot.
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $exe -ArgumentList $argumentLine -WindowStyle Hidden -PassThru
    $markerSeen = $false
    while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if (Test-Path -LiteralPath $markerPath -PathType Leaf) {
            $markerSeen = $true
            break
        }
        if ($process.HasExited) { break }
        Start-Sleep -Milliseconds 50
    }
    $launchToMarkerMs = if ($markerSeen) { $watch.Elapsed.TotalMilliseconds } else { $null }

    if (-not $process.HasExited) {
        $remainingMs = [Math]::Max(1, [int](($TimeoutSeconds - $watch.Elapsed.TotalSeconds) * 1000))
        $process.WaitForExit($remainingMs) | Out-Null
    }
    if (-not $process.HasExited) {
        # Only terminate the process this script started; do not interfere with
        # another normal XTranslate instance or a previous benchmark session.
        try { $process.Kill() } catch { }
        $process.WaitForExit(5000) | Out-Null
    }
    $watch.Stop()
    $result = $null
    if (Test-Path -LiteralPath $resultPath -PathType Leaf) {
        try { $result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json } catch { }
    }
    $runSuccess = $markerSeen -and $process.HasExited -and $process.ExitCode -eq 0 -and $null -ne $result -and [bool]$result.success
    $runResults.Add([ordered]@{
        run = $index
        processId = $process.Id
        exitCode = if ($process.HasExited) { $process.ExitCode } else { $null }
        completed = $process.HasExited
        launchToContentRenderedMs = $launchToMarkerMs
        markerPollingIntervalMs = 50
        warmLaunch = $true
        resultSuccess = if ($null -ne $result) { [bool]$result.success } else { $false }
        resultPath = $resultPath
    })
    $process.Dispose()
    if (-not $runSuccess) {
        $fatalFailure = $true
        break
    }
}

$summary = [ordered]@{
    success = -not $fatalFailure -and $runResults.Count -eq $Runs
    generatedUtc = [DateTimeOffset]::UtcNow
    executable = $exe
    softwareRenderingRequested = [bool]$SoftwareRendering
    hardwareRenderingRequested = [bool]$HardwareRendering
    productDefaultRendering = 'software'
    requestedTheme = $Theme # Older executables may not implement --theme; check the process report too.
    runs = $Runs
    completedRuns = $runResults.Count
    sessionDirectory = $session
    timeoutSeconds = $TimeoutSeconds
    launchTiming = 'Process creation to the benchmark process ContentRendered marker; marker polling interval is included and is not a WPF frame timestamp.'
    cachePolicy = 'OS/app caches were not cleared; classify these as warm launches, not cold boots.'
    results = $runResults
}
$summaryPath = Join-Path $root 'benchmark-launch-summary.json'
$summaryJson = $summary | ConvertTo-Json -Depth 8
$summaryJson | Set-Content -LiteralPath (Join-Path $session 'benchmark-launch-summary.json') -Encoding UTF8
$summaryJson | Set-Content -LiteralPath $summaryPath -Encoding UTF8
$summaryPath
if (-not [bool]$summary.success) { exit 1 }
