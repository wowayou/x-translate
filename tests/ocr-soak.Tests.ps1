# Synthetic fixtures only; loads actual runner functions, never launches WPF/OCR/network.
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root ('artifacts/verification/ocr-soak-tests-' + [Guid]::NewGuid().ToString('N')) }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a new report directory' }
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'memory-soak.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) { throw 'Soak script parse errors' }
foreach ($name in @('Require', 'Quote-ArgumentLine', 'AssertOcrWorkload', 'Get-SoakTiming', 'Get-ExecutableDeployment', 'Get-SoakProgramFiles', 'Invoke-SoakBuild')) {
    $function = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $true) | Where-Object Name -eq $name)
    if ($function.Count -ne 1) { throw 'Soak assertion is not uniquely selected' }
    . ([ScriptBlock]::Create($function[0].Extent.Text))
}
function Fixture {
    return [pscustomobject]@{
        configuration = [pscustomobject]@{ ocr = $true }
        phases = @(foreach ($index in 1..2) {
            [pscustomobject]@{ phase = ('batch-{0:D2}' -f $index); showHideIterations = 100; translationIterations = 20;
                ocr = [pscustomobject]@{ windows = 3; languageEnumerations = 3; recognitions = 6; mockTranslations = 6;
                    exactTextAndTranslation = $true; callerPixelsUnchanged = $true; closedPixelsCleared = $true;
                    screenPixelsRead = $false; filesOpened = $false; clipboard = $false; retries = 0;
                    width = 960; height = 160; pixelSha256 = ('A' * 64) } }
        })
    }
}
$checks = [Collections.Generic.List[string]]::new()
function Refuses([string]$Name, [scriptblock]$Action) {
    $refused = $false
    try { & $Action } catch { $refused = $true }
    if (-not $refused) { throw "Accepted invalid soak evidence: $Name" }
    $checks.Add($Name + ' is refused')
}
AssertOcrWorkload (Fixture) 2
$checks.Add('Two complete fixed OCR batches pass the production assertion')
$cases = [ordered]@{
    'OCR flag missing' = { param($report) $report.configuration.PSObject.Properties.Remove('ocr') }
    'Missing batch' = { param($report) $report.phases = @($report.phases[0]) }
    'Recognition count changed' = { param($report) $report.phases[0].ocr.recognitions = 5 }
    'Unexpected request count' = { param($report) $report.phases[0].ocr.mockTranslations = 7 }
    'Language enumeration repeated' = { param($report) $report.phases[0].ocr.languageEnumerations = 4 }
    'Base workload changed' = { param($report) $report.phases[0].showHideIterations = 99 }
    'Source pixels changed' = { param($report) $report.phases[0].ocr.callerPixelsUnchanged = $false }
    'Closed pixels not cleared' = { param($report) $report.phases[0].ocr.closedPixelsCleared = $false }
    'Unexpected screen read' = { param($report) $report.phases[0].ocr.screenPixelsRead = $true }
    'Unexpected file read' = { param($report) $report.phases[0].ocr.filesOpened = $true }
    'Unexpected clipboard access' = { param($report) $report.phases[0].ocr.clipboard = $true }
    'Unexpected retry' = { param($report) $report.phases[0].ocr.retries = 1 }
    'Duplicate batch' = { param($report) $report.phases[1].phase = 'batch-01' }
    'Out-of-order batch' = { param($report) $report.phases = @($report.phases[1], $report.phases[0]) }
    'Fixture hash changed' = { param($report) $report.phases[1].ocr.pixelSha256 = ('B' * 64) }
    'Invalid fixture hash' = { param($report) $report.phases[0].ocr.pixelSha256 = 'not-a-hash' }
    'Fixture dimensions changed' = { param($report) $report.phases[0].ocr.width = 961 }
}
foreach ($case in $cases.GetEnumerator()) {
    $fixture = Fixture
    & $case.Value $fixture
    Refuses $case.Key { AssertOcrWorkload $fixture 2 }
}
$timing = Get-SoakTiming 1200 600 30 1 1
Require ($timing.batches -eq 2 -and $timing.holdSeconds -eq 1200 -and $timing.diagnosticSeconds -eq 38 -and
    $timing.timeoutSeconds -eq 1298) 'Timeout omitted scheduled non-hold diagnostic phases.'
$checks.Add('Twenty-minute run budgets fixed diagnostic phases separately from its one-minute grace')
$timing = Get-SoakTiming 28800 600 30 1 30
Require ($timing.batches -eq 48 -and $timing.holdSeconds -eq 28800 -and $timing.timeoutSeconds -eq 30684) 'Eight-hour schedule changed.'
$checks.Add('Eight-hour run retains 48 complete ten-minute holds and a bounded deadline')
Refuses 'More than 100 batches' { Get-SoakTiming 601 5 30 1 1 }

# Empty fake binaries identify layouts only; no child process is ever started.
$program = Join-Path $OutputDirectory 'fake-program'
[IO.Directory]::CreateDirectory($program) | Out-Null
$exe = Join-Path $program 'XTranslate.exe'
$dll = Join-Path $program 'XTranslate.dll'
$runtime = [IO.Path]::ChangeExtension($exe, '.runtimeconfig.json')
[IO.File]::WriteAllBytes($exe, [byte[]]@(1, 2))
[IO.File]::WriteAllBytes($dll, [byte[]]@(3, 4))
Require ((Get-ExecutableDeployment $exe) -eq 'self-contained') 'Single-file layout was misidentified.'
$checks.Add('Single-file portable deployment is recognized')
foreach ($property in @('framework', 'frameworks')) {
    @{ runtimeOptions = @{ $property = @{ name = 'Microsoft.WindowsDesktop.App'; version = '10.0.12' } } } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $runtime -Encoding UTF8
    Require ((Get-ExecutableDeployment $exe) -eq 'framework-dependent') 'Lite deployment was misidentified.'
    $checks.Add("Lite $property deployment is recognized")
}
@{ runtimeOptions = @{ includedFrameworks = @(@{ name = 'Microsoft.NETCore.App'; version = '10.0.12' }) } } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $runtime -Encoding UTF8
Require ((Get-ExecutableDeployment $exe) -eq 'self-contained') 'Self-contained directory was misidentified.'
$checks.Add('Self-contained directory deployment is recognized')
'{}' | Set-Content -LiteralPath $runtime
Refuses 'Missing runtimeOptions' { Get-ExecutableDeployment $exe }
'{"runtimeOptions":{"tfm":"net10.0"}}' | Set-Content -LiteralPath $runtime
Refuses 'Unknown deployment descriptor' { Get-ExecutableDeployment $exe }
'not-json' | Set-Content -LiteralPath $runtime
Refuses 'Malformed runtime descriptor' { Get-ExecutableDeployment $exe }

$data = Join-Path $program 'data'
[IO.Directory]::CreateDirectory($data) | Out-Null
[IO.File]::WriteAllText((Join-Path $data 'settings.json'), 'synthetic settings')
[IO.File]::WriteAllText((Join-Path $program 'diagnostic.json'), 'synthetic report')
$identity = @(Get-SoakProgramFiles $exe)
Require ($identity.Count -eq 3 -and @($identity | Where-Object name -eq 'XTranslate.dll').Count -eq 1) 'Program identity traversed data or missed Lite code.'
$checks.Add('Flat identity includes Lite DLL/runtime descriptor but excludes data and diagnostic reports')
$before = ConvertTo-Json -InputObject $identity -Depth 4 -Compress
[IO.File]::WriteAllBytes($dll, [byte[]]@(4, 3))
$after = ConvertTo-Json -InputObject @(Get-SoakProgramFiles $exe) -Depth 4 -Compress
Require ($before -ne $after) 'A same-length Lite DLL change retained the same program identity.'
$checks.Add('Same-length Lite DLL mutation changes program identity despite unchanged apphost')
# Exercise the real build wait/exit/cleanup logic with a fake process, not a real SDK invocation.
$sdk = 'synthetic-dotnet.exe'
$session = $OutputDirectory
$script:fakeBuild = $null
function Start-Process {
    param($FilePath, $ArgumentList, $WindowStyle, [switch]$PassThru, $RedirectStandardOutput, $RedirectStandardError)
    Require ($FilePath -eq $sdk -and $ArgumentList -eq '"restore" "fixed project.csproj"' -and $PassThru) 'Build command quoting changed.'
    return $script:fakeBuild
}
function FakeBuild([int]$ExitCode, [bool]$Timeout) {
    $process = [pscustomobject]@{ Handle = 1; HasExited = -not $Timeout; ExitCode = $ExitCode; killed = $false; disposed = $false; waits = @() }
    $process | Add-Member ScriptMethod WaitForExit { param($milliseconds) $this.waits += $milliseconds; return $this.HasExited }
    $process | Add-Member ScriptMethod Kill { $this.killed = $true; $this.HasExited = $true }
    $process | Add-Member ScriptMethod Dispose { $this.disposed = $true }
    return $process
}
$script:fakeBuild = FakeBuild 0 $false
Invoke-SoakBuild @('restore', 'fixed project.csproj') 'synthetic'
Require ($script:fakeBuild.disposed -and -not $script:fakeBuild.killed -and $script:fakeBuild.waits[0] -eq 120000) 'Successful build process cleanup failed.'
$checks.Add('Build uses a 120-second wait, quoted arguments and disposes successful child handle')
$script:fakeBuild = FakeBuild 1 $false
Refuses 'Nonzero build exit' { Invoke-SoakBuild @('restore', 'fixed project.csproj') 'synthetic' }
Require ($script:fakeBuild.disposed) 'Failed build process handle was retained.'
$script:fakeBuild = FakeBuild 0 $true
Refuses 'Build timeout' { Invoke-SoakBuild @('restore', 'fixed project.csproj') 'synthetic' }
Require ($script:fakeBuild.killed -and $script:fakeBuild.disposed -and $script:fakeBuild.waits[1] -eq 5000) 'Timed-out child was not bounded and disposed.'
[ordered]@{ success = $true; count = $checks.Count; checks = $checks; syntheticOnly = $true; source = 'memory-soak.ps1 actual functions' } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'ocr-soak-tests.json') -Encoding UTF8
$checks | ForEach-Object { Write-Output ('PASS ' + $_) }
Write-Output ("{0}/{0} OCR soak verifier checks passed" -f $checks.Count)
