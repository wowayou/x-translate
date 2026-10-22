# Synthetic reports/files and a fake owned process only. No WPF/OCR, network or installed user data.
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root ('artifacts\verification\ocr-corpus-tests-' + [Guid]::NewGuid().ToString('N')) }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a new verifier-test directory; prior evidence is not overwritten.' }
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$runner = Join-Path $root 'tools\ocr\Run-OcrCorpus.ps1'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($runner, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Corpus runner parse errors.' }
foreach ($name in @('Require', 'Require-Bool', 'Assert-LocalCorpusPath', 'Read-CorpusJson', 'Read-CorpusPlan',
    'Get-CorpusProgramIdentity', 'Assert-CorpusDeployment', 'Assert-CorpusFile', 'Assert-CorpusReport', 'Assert-CorpusScroll', 'Invoke-CorpusProcess')) {
    $selected = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $true) | Where-Object Name -eq $name)
    if ($selected.Count -ne 1) { throw 'Corpus function is not uniquely selected.' }
    . ([ScriptBlock]::Create($selected[0].Extent.Text))
}
$checks = [Collections.Generic.List[string]]::new()
function Reject([string]$Name, [scriptblock]$Action) {
    $refused = $false
    try { & $Action | Out-Null } catch { $refused = $true }
    Require $refused ('Invalid corpus evidence was accepted: ' + $Name)
    $checks.Add($Name + ' is refused')
}
$plan = Read-CorpusPlan (Join-Path $root 'tools\ocr\corpus-plan.json')
Require (@($plan.cases).Count -eq 10 -and @($plan.cases | Where-Object required).Count -eq 4) 'Plan was not explicitly enumerated from Windows PowerShell JSON array.'
$checks.Add('Production plan reader preserves all ten fixed cases/four required expectations from a JSON array')
$fixtureRoot = Join-Path $OutputDirectory 'generated-evidence'
[IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
foreach ($spec in $plan.cases) {
    [IO.File]::WriteAllBytes((Join-Path $fixtureRoot ($spec.id + '.png')), [byte[]]@(137,80,78,71)) # Identity fixture, not an actual decoded PNG.
    if ($spec.id -in @('multiline-en', 'columns-en', 'mixed-zh')) {
        [IO.File]::WriteAllBytes((Join-Path $fixtureRoot ($spec.id + '.comparison.png')), [byte[]]@(1,2,3,4))
    }
}
function Fixture {
    $rows = @(); $requests = @()
    foreach ($spec in $plan.cases) {
        $comparison = if ($spec.id -in @('multiline-en', 'columns-en', 'mixed-zh')) { $spec.id + '.comparison.png' } else { $null }
        $rows += [pscustomobject]@{ id = $spec.id; language = $spec.language; required = $spec.required; width = $spec.width; height = $spec.height;
            dpi = $spec.dpi; expectedText = $spec.expectedText; actualText = $spec.expectedText; status = 'recognized'; grade = 'exact-whitespace-only';
            whitespaceOnlyMatch = $true; pngSha256 = (Get-FileHash -LiteralPath (Join-Path $fixtureRoot ($spec.id + '.png')) -Algorithm SHA256).Hash;
            pixelSha256 = ('A' * 64); safe = $true; previewInputExact = $true; sourceFileUnchanged = $true; closedPixelsCleared = $true; rawTextPreserved = $true;
            loadCalls = 1; languageEnumerations = 1; recognitions = 1; mockTranslations = 1; recognitionMs = 5; errorCode = $null; comparison = $comparison;
            comparisonSha256 = if ($comparison) { (Get-FileHash -LiteralPath (Join-Path $fixtureRoot $comparison) -Algorithm SHA256).Hash } else { $null } }
        $requests += [pscustomobject]@{ host = 'translate.googleapis.com'; text = $spec.expectedText; target = 'zh-CN' }
    }
    # Clone every nested object, including the plan, so a negative case cannot contaminate another one.
    $fixture = [pscustomobject]@{
        smoke = [pscustomobject]@{ success = $true; mode = 'ocr-corpus-only'; processId = 1001; failure = $null; checks = @('1','2','3','4','5','6','7') }
        report = [pscustomobject]@{ schemaVersion = 1; success = $true; complete = $true; operational = $true; requiredPassed = $true;
            processId = 1001; failure = $null; elapsedMs = 1000; mainSourceUnchanged = $true; modalClosed = $true; outerMockRequests = 0;
            machine = [pscustomobject]@{ edition = 'Lite'; version = '0.4.0'; processRenderMode = 'SoftwareOnly' };
            configuration = [pscustomobject]@{ generatedOnly = $true; screenPixelsRead = $false; clipboard = $false; userData = $false;
                network = 'none: fixed in-process HttpMessageHandler'; retries = 0; recognitionLimit = 10; translationLimit = 10; planSha256 = $plan.sha256 };
            plannedCases = $plan.cases; installedLanguages = @([pscustomobject]@{ tag = 'en-US' }, [pscustomobject]@{ tag = 'zh-Hans-CN' });
            recognitions = 10; mockTranslations = 10; languageEnumerations = 11; cases = $rows; mockRequests = $requests }
    }
    return ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $fixture -Depth 12)
}
function Validate($Fixture) { return Assert-CorpusReport $Fixture.smoke $Fixture.report $plan 1001 'Lite' '0.4.0' $fixtureRoot }
$valid = Validate (Fixture)
Require ($valid.requiredMatches -eq 4 -and $valid.observationalMatches -eq 6 -and $valid.recognitions -eq 10 -and $valid.mockTranslations -eq 10) 'Complete corpus did not pass the actual assertion.'
$checks.Add('Complete fixed synthetic corpus passes actual report/count/file-identity assertions')
$fixture = Fixture
$fixture.report.cases[4].actualText = 'LEFT ONE 111 LEFT TWO 222 RIGHT ONE 333 RIGHT MISREAD 444'
$fixture.report.cases[4].whitespaceOnlyMatch = $false; $fixture.report.cases[4].grade = 'text-mismatch'
$fixture.report.mockRequests[4].text = $fixture.report.cases[4].actualText
$valid = Validate $fixture
Require ($valid.observationalMatches -eq 5 -and -not $valid.grades[4].whitespaceOnlyMatch) 'Observational mismatch became an accuracy pass.'
$checks.Add('An honest observational mismatch remains a mismatch without failing operational acceptance')
$fixture = Fixture
$empty = $fixture.report.cases[5]; $empty.actualText = $null; $empty.status = $empty.grade = 'empty-text'; $empty.whitespaceOnlyMatch = $false
$empty.rawTextPreserved = $false; $empty.mockTranslations = 0; $empty.errorCode = 'EmptyText'; $empty.recognitionMs = $null
$fixture.report.mockRequests = @($fixture.report.mockRequests[0..4]) + @($fixture.report.mockRequests[6..9]); $fixture.report.mockTranslations = 9
$valid = Validate $fixture
Require ($valid.mockTranslations -eq 9 -and $valid.observationalMatches -eq 5 -and $valid.grades[5].grade -eq 'empty-text') 'Safe observational EmptyText became a translation/accuracy pass.'
$checks.Add('An observational EmptyText retains one recognition/zero requests and a non-passing text grade')
$fixture = Fixture
$fixture.report.installedLanguages = @($fixture.report.installedLanguages[0]); $skip = $fixture.report.cases[9]
$skip.status = $skip.grade = 'language-unavailable'; $skip.actualText = $null; $skip.whitespaceOnlyMatch = $false
$skip.recognitions = $skip.mockTranslations = $skip.loadCalls = $skip.languageEnumerations = 0
$fixture.report.mockRequests = @($fixture.report.mockRequests[0..8]); $fixture.report.recognitions = $fixture.report.mockTranslations = 9; $fixture.report.languageEnumerations = 10
$valid = Validate $fixture
Require ($valid.exercised -eq 9 -and $valid.observationalMatches -eq 5 -and $valid.grades[9].grade -eq 'language-unavailable') 'Missing optional language was recognized or graded as a pass.'
$checks.Add('Missing optional Chinese language is explicit/unexercised, not a download, fallback or accuracy pass')

$mutations = [ordered]@{
    'Missing completed case' = { param($f) $f.report.cases = @($f.report.cases[0..8]) }
    'Duplicated/out-of-order cases' = { param($f) $f.report.cases[1] = $f.report.cases[0] }
    'Changed planned expectation' = { param($f) $f.report.plannedCases[0].expectedText = 'changed fixed oracle' }
    'Changed case expectation' = { param($f) $f.report.cases[0].expectedText = 'changed fixed oracle' }
    'Required case demoted' = { param($f) $f.report.cases[0].required = $false }
    'Required mismatch despite success flag' = { param($f) $f.report.cases[0].actualText = 'WRONG'; $f.report.cases[0].grade = 'text-mismatch'; $f.report.cases[0].whitespaceOnlyMatch = $false; $f.report.mockRequests[0].text = 'WRONG' }
    'Case repair/case-insensitive grade' = { param($f) $f.report.cases[0].actualText = $f.report.cases[0].actualText.ToLowerInvariant(); $f.report.mockRequests[0].text = $f.report.cases[0].actualText }
    'Forged ordinal match flag' = { param($f) $f.report.cases[4].actualText = 'WRONG'; $f.report.cases[4].grade = 'text-mismatch'; $f.report.mockRequests[4].text = 'WRONG' }
    'Forged observational grade' = { param($f) $f.report.cases[4].grade = 'text-mismatch' }
    'Unrecognized engine failure hidden as skip' = { param($f) $f.report.cases[5].status = 'engine-failure' }
    'Installed language hidden as unavailable' = { param($f) $f.report.cases[9].status = 'language-unavailable' }
    'Required en-US missing' = { param($f) $f.report.installedLanguages = @($f.report.installedLanguages[1]) }
    'Duplicate installed language' = { param($f) $f.report.installedLanguages += $f.report.installedLanguages[0] }
    'Recognition retry' = { param($f) $f.report.cases[0].recognitions = 2 }
    'Missing load' = { param($f) $f.report.cases[0].loadCalls = 0 }
    'Repeated language enumeration' = { param($f) $f.report.cases[0].languageEnumerations = 2 }
    'Second translation' = { param($f) $f.report.cases[0].mockTranslations = 2 }
    'Total recognition count differs' = { param($f) $f.report.recognitions = 9 }
    'Total translation count differs' = { param($f) $f.report.mockTranslations = 9 }
    'Total language count differs' = { param($f) $f.report.languageEnumerations = 10 }
    'Unexpected mock request' = { param($f) $f.report.mockRequests += $f.report.mockRequests[0] }
    'Translated text silently repaired' = { param($f) $f.report.mockRequests[0].text = 'REPAIRED' }
    'Different translation recipient' = { param($f) $f.report.mockRequests[0].host = 'other.example.test' }
    'Different target language' = { param($f) $f.report.mockRequests[0].target = 'en' }
    'Outer HTTP handler used' = { param($f) $f.report.outerMockRequests = 1 }
    'Unexpected screen pixels read' = { param($f) $f.report.configuration.screenPixelsRead = $true }
    'Unexpected clipboard access' = { param($f) $f.report.configuration.clipboard = $true }
    'Unexpected user data' = { param($f) $f.report.configuration.userData = $true }
    'Wrong transport configuration' = { param($f) $f.report.configuration.network = 'real HTTP' }
    'Generated-only flag missing' = { param($f) $f.report.configuration.PSObject.Properties.Remove('generatedOnly') }
    'Expanded recognition limit' = { param($f) $f.report.configuration.recognitionLimit = 11 }
    'Expanded translation limit' = { param($f) $f.report.configuration.translationLimit = 11 }
    'Configured retry' = { param($f) $f.report.configuration.retries = 1 }
    'Different plan digest' = { param($f) $f.report.configuration.planSha256 = ('B' * 64) }
    'Different process identity' = { param($f) $f.report.processId = 1002 }
    'Wrong edition' = { param($f) $f.report.machine.edition = 'Portable' }
    'Wrong version' = { param($f) $f.report.machine.version = '0.4.1' }
    'Wrong render mode' = { param($f) $f.report.machine.processRenderMode = 'Default' }
    'Incomplete report' = { param($f) $f.report.complete = $false }
    'Operational failure' = { param($f) $f.report.operational = $false }
    'Required results failed' = { param($f) $f.report.requiredPassed = $false }
    'Main source changed' = { param($f) $f.report.mainSourceUnchanged = $false }
    'Modal retained' = { param($f) $f.report.modalClosed = $false }
    'Wrong process receipt lane' = { param($f) $f.smoke.mode = 'smoke-test' }
    'Process receipt failure' = { param($f) $f.smoke.success = $false }
    'String success instead of boolean' = { param($f) $f.report.success = 'true' }
    'Preview/input pixels differ' = { param($f) $f.report.cases[0].previewInputExact = $false }
    'Raw source modified' = { param($f) $f.report.cases[0].rawTextPreserved = $false }
    'Owned pixels uncleared' = { param($f) $f.report.cases[0].closedPixelsCleared = $false }
    'Source file changed' = { param($f) $f.report.cases[0].sourceFileUnchanged = $false }
    'Invalid source digest' = { param($f) $f.report.cases[0].pngSha256 = 'bad-hash' }
    'Invalid pixel digest' = { param($f) $f.report.cases[0].pixelSha256 = 'bad-hash' }
    'Comparison escapes fixture' = { param($f) $f.report.cases[0].comparison = '..\outside.png' }
    'Comparison digest missing' = { param($f) $f.report.cases[0].comparisonSha256 = $null }
}
foreach ($mutation in $mutations.GetEnumerator()) {
    $fixture = Fixture; & $mutation.Value $fixture
    Reject $mutation.Key { Validate $fixture }
}
$fixture = Fixture
$changed = Join-Path $fixtureRoot 'multiline-en.png'
[IO.File]::WriteAllBytes($changed, [byte[]]@(137,80,78,70))
Reject 'Same-length generated file mutation' { Validate $fixture }
[IO.File]::WriteAllBytes($changed, [byte[]]@(137,80,78,71))
$missing = Join-Path $fixtureRoot 'multiline-en.comparison.png'
[IO.File]::Move($missing, $missing + '.held')
Reject 'Missing comparison file' { Validate $fixture }
[IO.File]::Move($missing + '.held', $missing)
Reject 'UNC/network path before open' { Assert-LocalCorpusPath '\\fixed.invalid\share\image.png' }
$large = Join-Path $OutputDirectory 'large.json'
$stream = [IO.File]::Create($large); try { $stream.SetLength(1MB + 1) } finally { $stream.Dispose() }
Reject 'Oversized JSON before parsing' { Read-CorpusJson $large }
$invalidPlan = Join-Path $OutputDirectory 'invalid-plan.json'
[IO.File]::WriteAllText($invalidPlan, '[]')
Reject 'Empty plan' { Read-CorpusPlan $invalidPlan }
$fixture = Fixture; $fixture.report.plannedCases[0].id = '../outside'
[IO.File]::WriteAllText($invalidPlan, (ConvertTo-Json -InputObject $fixture.report.plannedCases -Depth 5))
Reject 'Unsafe generated plan name' { Read-CorpusPlan $invalidPlan }
$program = Join-Path $OutputDirectory 'program'; [IO.Directory]::CreateDirectory($program) | Out-Null
$exe = Join-Path $program 'XTranslate.exe'; $dll = Join-Path $program 'XTranslate.dll'; $runtime = Join-Path $program 'XTranslate.runtimeconfig.json'
[IO.File]::WriteAllBytes($exe, [byte[]]@(1,2)); [IO.File]::WriteAllBytes($dll, [byte[]]@(3,4))
Assert-CorpusDeployment $exe 'Portable'; Reject 'Portable descriptor missing in Lite' { Assert-CorpusDeployment $exe 'Lite' }
[IO.File]::WriteAllText($runtime, '{"runtimeOptions":{"frameworks":[{"name":"Microsoft.WindowsDesktop.App","version":"10.0.12"}]}}')
Assert-CorpusDeployment $exe 'Lite'; Assert-CorpusDeployment $exe 'Dev'
Reject 'Framework-dependent descriptor in Portable' { Assert-CorpusDeployment $exe 'Portable' }
$checks.Add('Explicit Portable/Lite/Dev deployment selection is checked before a child can start')
$data = Join-Path $program 'data'; [IO.Directory]::CreateDirectory($data) | Out-Null
[IO.File]::WriteAllText((Join-Path $data 'settings.json'), 'synthetic-only')
$identity = @(Get-CorpusProgramIdentity $exe)
Require ($identity.Count -eq 3) 'Program identity missed Lite code or traversed data.'
$before = ConvertTo-Json -InputObject $identity -Depth 4 -Compress
[IO.File]::WriteAllBytes($dll, [byte[]]@(4,3))
Require ($before -cne (ConvertTo-Json -InputObject @(Get-CorpusProgramIdentity $exe) -Depth 4 -Compress)) 'Same-length DLL mutation did not change program identity.'
$checks.Add('Bounded flat identity binds Lite DLLs/runtime configuration and detects same-length code changes without traversing data')
for ($index = 0; $index -lt 126; $index++) { [IO.File]::WriteAllText((Join-Path $program ($index.ToString() + '.txt')), '') }
Reject '129th flat program entry' { Get-CorpusProgramIdentity $exe }
$link = Join-Path $OutputDirectory 'junction'
& $env:ComSpec /d /c "mklink /J `"$link`" `"$fixtureRoot`"" | Out-Null
Require ($LASTEXITCODE -eq 0) 'Owned junction fixture could not be created.'
Reject 'Junction root' { Assert-LocalCorpusPath $link }
Reject 'Junction child path' { Assert-LocalCorpusPath (Join-Path $link 'multiline-en.png') }

# Synthetic scroll receipt: actual scrollbar assertions/files, without opening WPF.
function ScrollFixture {
    $rows = @()
    foreach ($longText in @($false, $true)) {
        foreach ($language in @('SimplifiedChinese', 'English')) {
            foreach ($minimum in @($false, $true)) {
                $outer = if ($minimum) { 'Visible' } else { 'Collapsed' }; $text = if ($longText) { 'Visible' } else { 'Collapsed' }
                $images = @(); $prefix = 'corpus-only.ocr-scroll.' + $(if ($longText) { 'long' } else { 'short' }) + '.' +
                    $(if ($minimum) { 'minimum' } else { 'default' }) + '.' + $(if ($language -eq 'English') { 'en.dark' } else { 'zh.light' })
                $scales = if ($longText -and $language -eq 'English') { @(1,1.5,2) } else { @(1) }
                foreach ($scale in $scales) {
                    $name = $prefix + $(if ($scale -eq 1) { '.png' } else { '.' + [int]($scale * 100) + '.png' })
                    $path = Join-Path $fixtureRoot $name
                    if (-not (Test-Path -LiteralPath $path)) { [IO.File]::WriteAllBytes($path, [byte[]]@(1,2,3,4)) }
                    $images += [pscustomobject]@{ name = $name; scale = $scale; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
                }
                $rows += [pscustomobject]@{ shape = [pscustomobject]@{ language = $language; longText = $longText; minimum = $minimum;
                    width = if ($minimum) { 720 } else { 1000 }; height = if ($minimum) { 520 } else { 760 }; sourceHeight = 100; resultHeight = 160;
                    outer = [pscustomobject]@{ verticalBar = $outer; horizontalBar = 'Collapsed'; viewportHeight = 200; extentHeight = if ($minimum) { 400 } else { 200 }; scrollableHeight = if ($minimum) { 200 } else { 0 } };
                    source = [pscustomobject]@{ verticalBar = $text; horizontalBar = 'Collapsed'; viewportHeight = 100; extentHeight = if ($longText) { 500 } else { 100 }; scrollableHeight = if ($longText) { 400 } else { 0 } };
                    result = [pscustomobject]@{ verticalBar = $text; horizontalBar = 'Collapsed'; viewportHeight = 100; extentHeight = if ($longText) { 500 } else { 100 }; scrollableHeight = if ($longText) { 400 } else { 0 } } };
                    editorsOwnScroll = $true; barsExpected = $true; footerVisible = $true; thumbInside = $true; interaction = $true; contentUnchanged = $true; images = $images }
            }
        }
    }
    return [pscustomobject]@{ success = $true; processId = 1001; generatedOnly = $true; screenPixelsRead = $false; clipboard = $false; userData = $false;
        realRecognitions = 0; syntheticCaptures = 2; syntheticRecognitions = 2; languageEnumerations = 1; mockTranslations = 2; failure = $null; rows = $rows }
}
$scroll = Assert-CorpusScroll (ScrollFixture) 1001 $fixtureRoot
Require ($scroll.layouts -eq 8 -and $scroll.screenshots -eq 12) 'Complete synthetic scroll evidence did not pass.'
$checks.Add('Actual scroll verifier checks all eight default/minimum short/long bilingual layouts and twelve bounded image digests')
$scrollMutations = [ordered]@{
    'Missing scroll layout' = { param($s) $s.rows = @($s.rows[0..6]) }
    'Scroll layouts reordered' = { param($s) $s.rows[1].shape.minimum = $false }
    'Scroll actual size differs' = { param($s) $s.rows[0].shape.width = 999 }
    'Unnecessary outer short-content bar' = { param($s) $s.rows[0].shape.outer.verticalBar = 'Visible' }
    'Unnecessary short editor bar' = { param($s) $s.rows[0].shape.source.verticalBar = 'Visible' }
    'Long translation bar missing' = { param($s) $s.rows[4].shape.result.verticalBar = 'Collapsed' }
    'Horizontal scrolling unexpectedly enabled' = { param($s) $s.rows[0].shape.source.horizontalBar = 'Visible' }
    'Duplicate left-column scroll' = { param($s) $s.rows[0].editorsOwnScroll = $false }
    'Scroll thumb outside host' = { param($s) $s.rows[0].thumbInside = $false }
    'Footer clipped' = { param($s) $s.rows[0].footerVisible = $false }
    'Scroll end or boundary handoff failed' = { param($s) $s.rows[0].interaction = $false }
    'Scroll changed source/result/request count' = { param($s) $s.rows[0].contentUnchanged = $false }
    'Missing scroll viewport' = { param($s) $s.rows[0].shape.source.viewportHeight = 0 }
    'Scroll extent statistics disagree' = { param($s) $s.rows[0].shape.source.scrollableHeight = 100 }
    'Scroll performed real recognition' = { param($s) $s.realRecognitions = 1 }
    'Scroll repeated synthetic request' = { param($s) $s.mockTranslations = 3 }
    'Scroll receipt from another process' = { param($s) $s.processId = 1002 }
    'Missing scaled scroll image' = { param($s) $s.rows[6].images = @($s.rows[6].images[0..1]) }
    'Wrong scaled scroll image identity' = { param($s) $s.rows[6].images[1].scale = 2 }
}
foreach ($mutation in $scrollMutations.GetEnumerator()) {
    $scroll = ScrollFixture; & $mutation.Value $scroll
    Reject $mutation.Key { Assert-CorpusScroll $scroll 1001 $fixtureRoot }
}

# The actual launch/wait/exit/timeout/disposal code, with just Start-Process replaced in this process.
$script:fake = $null
function Start-Process {
    param($FilePath, $ArgumentList, [switch]$PassThru, $WindowStyle)
    Require ($FilePath -eq 'fixed-corpus.exe' -and $ArgumentList -eq '"--ocr-corpus" "--smoke-output" "D:\fixed output\report.json"' -and $PassThru) 'Corpus launch/quoting differs.'
    return $script:fake
}
function Fake([int]$ExitCode, [bool]$Timeout) {
    $process = [pscustomobject]@{ Handle = 1; Id = 1001; ExitCode = $ExitCode; HasExited = -not $Timeout; killed = $false; disposed = $false; waits = @() }
    $process | Add-Member ScriptMethod WaitForExit { param($milliseconds) $this.waits += $milliseconds; return $this.HasExited }
    $process | Add-Member ScriptMethod Kill { $this.killed = $true; $this.HasExited = $true }
    $process | Add-Member ScriptMethod Dispose { $this.disposed = $true }
    return $process
}
foreach ($code in @(0,1)) {
    $script:fake = Fake $code $false
    $execution = Invoke-CorpusProcess 'fixed-corpus.exe' 'D:\fixed output\report.json' 30
    Require ($execution.exitCode -eq $code -and -not $execution.timedOut -and $script:fake.disposed -and -not $script:fake.killed -and $script:fake.waits[0] -eq 30000) 'Corpus exit code or handle cleanup was hidden.'
    $checks.Add("Owned process retains actual exit $code and disposes its handle without a retry")
}
$script:fake = Fake 99 $true
$execution = Invoke-CorpusProcess 'fixed-corpus.exe' 'D:\fixed output\report.json' 30
Require ($execution.timedOut -and $script:fake.killed -and $script:fake.disposed -and $script:fake.waits[1] -eq 5000) 'Timed-out owned process was not stopped/disposed within the cleanup wait.'
$checks.Add('Timeout stops only the returned owned child and stays a failed execution with bounded cleanup/no retry')
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'ocr-corpus-tests.json'), (ConvertTo-Json -InputObject ([ordered]@{
    success = $true; count = $checks.Count; checks = $checks; syntheticOnly = $true; source = 'tools/ocr/Run-OcrCorpus.ps1 actual functions' }) -Depth 6), [Text.UTF8Encoding]::new($false))
$checks | ForEach-Object { Write-Output ('PASS ' + $_) }
Write-Output ("{0}/{0} OCR corpus verifier checks passed" -f $checks.Count)
