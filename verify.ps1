param([switch]$LiveGoogle, [switch]$NativeChecks, [string]$Executable, [string]$ReportDirectory, [switch]$SoftwareRendering, [switch]$HardwareRendering)
$ErrorActionPreference = 'Stop'
if ($SoftwareRendering -and $HardwareRendering) { throw 'Choose either -SoftwareRendering or -HardwareRendering, not both.' }
$projectRoot = $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools/cli'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$sdk = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet -ErrorAction Stop).Source }
# Framework-dependent test builds must run on the same runtime as the SDK, not an older system install.
$env:DOTNET_ROOT = Split-Path -Parent $sdk
Push-Location $projectRoot
try {
    & $sdk run --project tests/XTranslate.Core.Tests/XTranslate.Core.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
    if (-not $Executable) {
        & $sdk build src/XTranslate.App/XTranslate.App.csproj -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
        $Executable = Join-Path $projectRoot 'src/XTranslate.App/bin/Release/net10.0-windows/XTranslate.exe'
    }
    if (-not $ReportDirectory) {
        $projectXml = [xml](Get-Content -Raw -LiteralPath 'src/XTranslate.App/XTranslate.App.csproj')
        $version = [string]($projectXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
        $ReportDirectory = Join-Path $projectRoot ('artifacts/verification/' + $version)
    }
    $reportRoot = [System.IO.Path]::GetFullPath($ReportDirectory)
    $runName = 'run-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $runDirectory = Join-Path $reportRoot $runName
    New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
    $report = Join-Path $runDirectory 'ui-smoke.json'
    $startedAtUtc = [DateTime]::UtcNow
    $arguments = @('--smoke-test', '--smoke-output', ('"' + $report + '"'))
    if ($LiveGoogle) { $arguments += '--live-google' }
    if ($NativeChecks) { $arguments += '--native-checks' }
    if ($SoftwareRendering) { $arguments += '--software-rendering' }
    if ($HardwareRendering) { $arguments += '--hardware-rendering' }
    $process = Start-Process -FilePath $Executable -ArgumentList $arguments -WindowStyle Hidden -PassThru
    try {
        if (-not $process.WaitForExit(45000)) {
            try { $process.Kill() } catch { }
            $process.WaitForExit(5000)
            throw 'UI verification exceeded 45 seconds; this invocation was terminated with a non-zero failure.'
        }
        if ($process.ExitCode -ne 0) {
            if (Test-Path -LiteralPath $report) { Get-Content -Raw -LiteralPath $report }
            throw ('UI verification failed with exit code ' + $process.ExitCode)
        }
        if (-not (Test-Path -LiteralPath $report)) {
            throw "UI verification exited successfully but did not write the new report '$report'."
        }
        $reportFile = Get-Item -LiteralPath $report
        if ($reportFile.LastWriteTimeUtc -lt $startedAtUtc) {
            throw "UI verification report '$report' predates this invocation."
        }
        $result = Get-Content -Raw -LiteralPath $report | ConvertFrom-Json
        if (-not $result.success) { throw 'UI verification reported failure.' }
        $result.checks | ForEach-Object { Write-Host ('PASS ' + $_) }
        Write-Host ('Report: ' + $report)
    } finally {
        $process.Dispose()
    }
} finally { Pop-Location }
