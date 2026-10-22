# Explicit generated-image acceptance only. One owned WPF process; no builds, retries or real HTTP.
# The separate scroll lane adds exactly two fixed synthetic recognitions/translations to the ten-image corpus.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExecutablePath,
    [Parameter(Mandatory)][ValidateSet('Dev', 'Lite', 'Portable')][string]$Flavor,
    [string]$OutputDirectory,
    [ValidateRange(30, 240)][int]$TimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Require-Bool($Value, [bool]$Expected, [string]$Message) {
    Require ($Value -is [bool] -and $Value -eq $Expected) $Message
}
function Assert-LocalCorpusPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    Require ($full -match '^[A-Za-z]:\\' -and ([IO.DriveInfo]::new([IO.Path]::GetPathRoot($full))).DriveType -ne [IO.DriveType]::Network) 'Corpus paths must be local drive paths.'
    for ($current = $full; $null -ne $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if (Test-Path -LiteralPath $current) {
            Require (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Corpus paths must not contain links/reparse points.'
        }
    }
    return $full
}
function Read-CorpusJson([string]$Path) {
    $Path = Assert-LocalCorpusPath $Path
    Require (Test-Path -LiteralPath $Path -PathType Leaf) 'Corpus JSON evidence is missing.'
    Require (([IO.FileInfo]::new($Path)).Length -le 1MB) 'Corpus JSON evidence exceeds 1 MiB.'
    return ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($Path, [Text.UTF8Encoding]::new($false, $true)))
}
function Read-CorpusPlan([string]$Path) {
    $parsed = Read-CorpusJson $Path
    $cases = @($parsed)
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    Require ($cases.Count -eq 10) 'Corpus plan must contain ten fixed cases.'
    foreach ($case in $cases) {
        Require ($case.id -cmatch '^[a-z0-9-]{1,40}$' -and $ids.Add($case.id) -and $case.required -is [bool] -and
            $case.language -in @('en-US', 'zh-Hans-CN') -and $case.width -ge 1 -and $case.width -le 8192 -and
            $case.height -ge 1 -and $case.height -le 8192 -and [long]$case.width * $case.height -le 8000000 -and
            $case.dpi -ge 96 -and $case.dpi -le 300 -and $case.expectedText -is [string] -and
            -not [string]::IsNullOrWhiteSpace($case.expectedText) -and $case.expectedText.Length -le 3000) 'Invalid bounded corpus plan.'
    }
    Require (@($cases | Where-Object required).Count -eq 4) 'Corpus plan must retain four required expectations.'
    return [pscustomobject]@{ cases = $cases; sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
}
function Get-CorpusProgramIdentity([string]$Executable) {
    $rows = [Collections.Generic.List[object]]::new(); $examined = 0; $bytes = 0L
    foreach ($path in [IO.Directory]::EnumerateFileSystemEntries([IO.Path]::GetDirectoryName($Executable))) {
        Require (++$examined -le 128) 'Corpus executable directory exceeds the flat 128-entry limit.'
        $name = [IO.Path]::GetFileName($path)
        if ($name -notmatch '\.(exe|dll)$|\.(runtimeconfig|deps)\.json$') { continue } # Never traverse data.
        $file = Get-Item -LiteralPath $path -Force
        Require (-not $file.PSIsContainer -and ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Corpus program files must be ordinary files.'
        $bytes += $file.Length
        Require ($bytes -le 512MB) 'Corpus program identity exceeds 512 MiB.'
        $rows.Add([pscustomobject]@{ name = $name; bytes = $file.Length; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash })
    }
    Require ($rows.Count -gt 0) 'Corpus program identity is empty.'
    return $rows | Sort-Object name
}
function Assert-CorpusDeployment([string]$Executable, [string]$Edition) {
    $runtime = [IO.Path]::ChangeExtension($Executable, '.runtimeconfig.json')
    if (-not (Test-Path -LiteralPath $runtime)) {
        Require ($Edition -eq 'Portable') 'Missing framework-dependent runtime descriptor.'
        return
    }
    $descriptor = Read-CorpusJson $runtime
    $names = @($descriptor.runtimeOptions.PSObject.Properties.Name)
    Require ($Edition -ne 'Portable' -and ($names -contains 'frameworks' -or $names -contains 'framework')) 'Corpus deployment does not match the requested edition.'
}
function Assert-CorpusFile([string]$Root, [string]$Name, $Hash) {
    Require ($Name -ceq [IO.Path]::GetFileName($Name) -and $Hash -is [string] -and $Hash -cmatch '^[0-9A-F]{64}$') 'Invalid corpus evidence file identity.'
    $path = Assert-LocalCorpusPath (Join-Path $Root $Name)
    Require (Test-Path -LiteralPath $path -PathType Leaf) 'Corpus generated evidence file is missing.'
    $length = ([IO.FileInfo]::new($path)).Length
    Require ($length -gt 0 -and $length -le 16MB -and (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ceq $Hash) 'Corpus generated evidence file hash/size differs.'
}
function Assert-CorpusReport($Smoke, $Report, $Plan, [int]$ProcessId, [string]$Edition, [string]$Version, [string]$FixtureRoot) {
    Require-Bool $Smoke.success $true 'Corpus-only process did not report success.'
    Require ($Smoke.mode -ceq 'ocr-corpus-only' -and $Smoke.processId -eq $ProcessId -and $null -eq $Smoke.failure -and
        @($Smoke.checks).Count -eq 7) 'Corpus-only process receipt differs.'
    foreach ($flag in @('success', 'complete', 'operational', 'requiredPassed', 'mainSourceUnchanged', 'modalClosed')) {
        Require-Bool $Report.$flag $true ('Corpus report flag failed: ' + $flag)
    }
    Require ($Report.schemaVersion -eq 1 -and $Report.processId -eq $ProcessId -and $null -eq $Report.failure -and
        $Report.elapsedMs -gt 0 -and $Report.machine.edition -ceq $Edition -and $Report.machine.version -ceq $Version -and
        $Report.machine.processRenderMode -ceq 'SoftwareOnly') 'Corpus report identity/configuration differs.'
    $configuration = $Report.configuration
    foreach ($flag in @('generatedOnly')) { Require-Bool $configuration.$flag $true 'Corpus requires generated-only input.' }
    foreach ($flag in @('screenPixelsRead', 'clipboard', 'userData')) { Require-Bool $configuration.$flag $false ('Corpus safety flag differs: ' + $flag) }
    Require ($configuration.network -ceq 'none: fixed in-process HttpMessageHandler' -and $configuration.retries -eq 0 -and
        $configuration.recognitionLimit -eq 10 -and $configuration.translationLimit -eq 10 -and
        $configuration.planSha256 -ceq $Plan.sha256 -and $Report.outerMockRequests -eq 0) 'Corpus plan, transport or request limit differs.'
    $specs = @($Plan.cases); $planned = @($Report.plannedCases); $rows = @($Report.cases)
    Require ($planned.Count -eq 10 -and $rows.Count -eq 10) 'Corpus cases are missing or duplicated.'
    $installed = @($Report.installedLanguages); $languages = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    Require ($installed.Count -ge 1 -and $installed.Count -le 128) 'Corpus installed-language evidence differs.'
    foreach ($language in $installed) {
        Require ($language.tag -cmatch '^[A-Za-z0-9-]{1,64}$' -and $languages.Add($language.tag)) 'Invalid/duplicated installed language.'
    }
    Require ($languages.Contains('en-US')) 'Required en-US is unavailable; no skip or language fallback is allowed.'
    $reads = 0; $translations = 0; $requiredMatches = 0; $observedMatches = 0; $exercised = 0
    $requests = @($Report.mockRequests); $grades = [Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt 10; $index++) {
        $spec = $specs[$index]; $row = $rows[$index]
        foreach ($entry in @($planned[$index], $row)) {
            foreach ($field in @('id', 'language', 'width', 'height', 'dpi', 'expectedText')) {
                Require ($entry.$field -ceq $spec.$field) ('Corpus plan/case changed or reordered: ' + $spec.id)
            }
            Require-Bool $entry.required $spec.required 'Corpus required/observational grading changed.'
        }
        Assert-CorpusFile $FixtureRoot ($spec.id + '.png') $row.pngSha256
        Require ($row.pixelSha256 -cmatch '^[0-9A-F]{64}$') 'Corpus pixel identity is missing.'
        $match = $false
        if (-not $languages.Contains($spec.language)) {
            Require (-not $spec.required -and $row.status -ceq 'language-unavailable' -and $row.grade -ceq 'language-unavailable' -and
                $null -eq $row.actualText -and $row.recognitions -eq 0 -and $row.mockTranslations -eq 0 -and
                $row.languageEnumerations -eq 0 -and $row.loadCalls -eq 0) 'Unavailable language was exercised or reported as a pass.'
        } else {
            $exercised++
            foreach ($flag in @('safe', 'previewInputExact', 'sourceFileUnchanged', 'closedPixelsCleared')) {
                Require-Bool $row.$flag $true ('Corpus execution/safety differs: ' + $flag)
            }
            Require ($row.loadCalls -eq 1 -and $row.languageEnumerations -eq 1 -and $row.recognitions -eq 1) 'Corpus performed missing/repeated decoding or recognition.'
            $reads++
            if ($row.status -ceq 'recognized') {
                Require ($row.actualText -is [string] -and -not [string]::IsNullOrWhiteSpace($row.actualText) -and $row.actualText.Length -le 3000 -and
                    $row.mockTranslations -eq 1 -and $null -eq $row.errorCode -and $null -ne $row.recognitionMs -and $row.recognitionMs -ge 0) 'Corpus recognized outcome differs.'
                Require-Bool $row.rawTextPreserved $true 'Corpus raw recognized text was modified.'
                $match = ($row.actualText -creplace '\s', '') -ceq ($spec.expectedText -creplace '\s', '')
                $grade = if ($match) { 'exact-whitespace-only' } else { 'text-mismatch' }
                Require ($row.grade -ceq $grade) 'Corpus text mismatch was incorrectly graded.'
                Require ($translations -lt $requests.Count -and $requests[$translations].host -ceq 'translate.googleapis.com' -and
                    $requests[$translations].target -ceq 'zh-CN' -and $requests[$translations].text -ceq $row.actualText) 'Corpus explicit mock translation did not preserve exact raw text/recipient.'
                $translations++
                if ($spec.id -in @('multiline-en', 'columns-en', 'mixed-zh')) {
                    Require ($row.comparison -ceq ($spec.id + '.comparison.png')) 'Corpus comparison evidence name differs.'
                    Assert-CorpusFile $FixtureRoot $row.comparison $row.comparisonSha256
                } else { Require ($null -eq $row.comparison -and $null -eq $row.comparisonSha256) 'Unexpected corpus comparison evidence.' }
            } else {
                Require (-not $spec.required -and $row.status -ceq 'empty-text' -and $row.grade -ceq 'empty-text' -and $row.errorCode -ceq 'EmptyText' -and
                    $null -eq $row.actualText -and $row.mockTranslations -eq 0 -and $null -eq $row.recognitionMs -and
                    $null -eq $row.comparison -and $null -eq $row.comparisonSha256) 'Corpus engine failure was skipped or called an accuracy pass.'
                Require-Bool $row.rawTextPreserved $false 'Empty OCR reported preserved successful text.'
            }
        }
        Require-Bool $row.whitespaceOnlyMatch $match 'Corpus reported text grade disagrees with independent whitespace-only comparison.'
        if ($match) { if ($spec.required) { $requiredMatches++ } else { $observedMatches++ } }
        $grades.Add([pscustomobject]@{ id = $spec.id; required = $spec.required; status = $row.status; grade = $row.grade; whitespaceOnlyMatch = $match })
    }
    Require ($requiredMatches -eq 4 -and $Report.recognitions -eq $reads -and $Report.mockTranslations -eq $translations -and
        $requests.Count -eq $translations -and $Report.languageEnumerations -eq ($exercised + 1)) 'Corpus required expectations or total work/request counts differ.'
    return [pscustomobject]@{ planned = 10; exercised = $exercised; requiredMatches = $requiredMatches; observationalMatches = $observedMatches;
        observationalPlanned = 6; recognitions = $reads; mockTranslations = $translations; grades = $grades.ToArray() }
}
function Assert-CorpusScroll($Report, [int]$ProcessId, [string]$Root) {
    Require-Bool $Report.success $true 'Corpus scroll report did not pass.'
    Require-Bool $Report.generatedOnly $true 'Scroll input was not generated.'
    foreach ($flag in @('screenPixelsRead', 'clipboard', 'userData')) { Require-Bool $Report.$flag $false 'Scroll safety flag differs.' }
    Require ($Report.processId -eq $ProcessId -and $null -eq $Report.failure -and $Report.realRecognitions -eq 0 -and
        $Report.syntheticCaptures -eq 2 -and $Report.syntheticRecognitions -eq 2 -and $Report.languageEnumerations -eq 1 -and
        $Report.mockTranslations -eq 2) 'Scroll process/workload identity differs.'
    $rows = @($Report.rows); Require ($rows.Count -eq 8) 'Scroll layout cases are missing/duplicated.'
    $index = 0; $images = 0
    foreach ($longText in @($false, $true)) {
        foreach ($language in @('SimplifiedChinese', 'English')) {
            foreach ($minimum in @($false, $true)) {
                $row = $rows[$index++]; $shape = $row.shape
                Require ($shape.language -ceq $language) 'Scroll layout cases changed order/language.'
                Require-Bool $shape.longText $longText 'Scroll text length case differs.'
                Require-Bool $shape.minimum $minimum 'Scroll size case differs.'
                Require ($shape.width -eq $(if ($minimum) { 720 } else { 1000 }) -and
                    $shape.height -eq $(if ($minimum) { 520 } else { 760 })) 'Scroll actual window size differs from the planned layout.'
                foreach ($flag in @('editorsOwnScroll', 'barsExpected', 'footerVisible', 'thumbInside', 'interaction', 'contentUnchanged')) {
                    Require-Bool $row.$flag $true ('Scroll contract failed: ' + $flag)
                }
                $expectedOuter = if ($minimum) { 'Visible' } else { 'Collapsed' }
                $expectedText = if ($longText) { 'Visible' } else { 'Collapsed' }
                Require ($shape.outer.verticalBar -ceq $expectedOuter -and $shape.source.verticalBar -ceq $expectedText -and
                    $shape.result.verticalBar -ceq $expectedText -and $shape.sourceHeight -ge 100 -and $shape.resultHeight -ge 60) 'Scroll bars/text viewport dimensions differ.'
                foreach ($scrollHost in @($shape.outer, $shape.source, $shape.result)) {
                    Require ($scrollHost.horizontalBar -ceq 'Collapsed' -and $scrollHost.viewportHeight -gt 0 -and $scrollHost.scrollableHeight -ge 0 -and
                        [Math]::Abs([Math]::Max(0, $scrollHost.extentHeight - $scrollHost.viewportHeight) - $scrollHost.scrollableHeight) -lt 1) 'Scroll metrics are incomplete/inconsistent.'
                }
                Require (($minimum -or $shape.outer.scrollableHeight -eq 0) -and ($longText -or
                    ($shape.source.scrollableHeight -eq 0 -and $shape.result.scrollableHeight -eq 0))) 'Short/default layout has unnecessary overflow.'
                $snapshots = @($row.images); $expectedImages = if ($longText -and $language -eq 'English') { 3 } else { 1 }
                Require ($snapshots.Count -eq $expectedImages) 'Scroll layout screenshot count differs.'
                $prefix = 'corpus-only.ocr-scroll.' + $(if ($longText) { 'long' } else { 'short' }) + '.' +
                    $(if ($minimum) { 'minimum' } else { 'default' }) + '.' + $(if ($language -eq 'English') { 'en.dark' } else { 'zh.light' })
                for ($imageIndex = 0; $imageIndex -lt $snapshots.Count; $imageIndex++) {
                    $scale = @(1, 1.5, 2)[$imageIndex]
                    $suffix = if ($imageIndex -eq 0) { '.png' } else { '.' + [int]($scale * 100) + '.png' }
                    Require ($snapshots[$imageIndex].name -ceq ($prefix + $suffix) -and $snapshots[$imageIndex].scale -eq $scale) 'Scroll screenshot name/scale differs.'
                    Assert-CorpusFile $Root $snapshots[$imageIndex].name $snapshots[$imageIndex].sha256; $images++
                }
            }
        }
    }
    return [pscustomobject]@{ layouts = 8; screenshots = $images; independentEditorScroll = $true; boundaryHandoff = $true; fixedFooter = $true }
}
function Invoke-CorpusProcess([string]$Executable, [string]$Output, [int]$Seconds) {
    $arguments = @('--ocr-corpus', '--smoke-output', $Output)
    $quoted = (($arguments | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }) -join ' ')
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $Executable -ArgumentList $quoted -PassThru -WindowStyle Hidden
    try {
        $null = $process.Handle
        $timedOut = -not $process.WaitForExit($Seconds * 1000)
        if ($timedOut) { $process.Kill(); Require ($process.WaitForExit(5000)) 'Owned corpus process did not stop after its deadline.' }
        return [pscustomobject]@{ processId = $process.Id; exitCode = $process.ExitCode; timedOut = $timedOut;
            elapsedSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 3); timeoutSeconds = $Seconds; arguments = $arguments }
    } finally {
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
        $process.Dispose()
    }
}

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root ('artifacts\verification\ocr-corpus-' + [Guid]::NewGuid().ToString('N')) }
$session = Assert-LocalCorpusPath $OutputDirectory
Require (-not (Test-Path -LiteralPath $session)) 'Use a new corpus evidence directory; prior results are never overwritten.'
$executable = Assert-LocalCorpusPath $ExecutablePath
Require (Test-Path -LiteralPath $executable -PathType Leaf) 'Corpus executable is missing.'
Assert-CorpusDeployment $executable $Flavor
$plan = Read-CorpusPlan (Join-Path $PSScriptRoot 'corpus-plan.json')
$program = @(Get-CorpusProgramIdentity $executable)
$version = ([Diagnostics.FileVersionInfo]::GetVersionInfo($executable)).ProductVersion.Split('+')[0]
$env:DOTNET_ROOT = Join-Path $root '.tools\dotnet'
[IO.Directory]::CreateDirectory($session) | Out-Null
$output = Join-Path $session 'corpus-only.json'
$config = [ordered]@{ executable = $executable; flavor = $Flavor; version = $version; programFiles = $program; planSha256 = $plan.sha256;
    timeoutSeconds = $TimeoutSeconds; recognitionLimit = 10; mockTranslationLimit = 10; scrollSyntheticRecognitions = 2;
    scrollMockTranslations = 2; processes = 1; retries = 0;
    generatedOnly = $true; screenPixelsRead = $false; clipboard = $false; userData = $false; network = 'none: fixed in-process HttpMessageHandler' }
[IO.File]::WriteAllText((Join-Path $session 'command.json'), (ConvertTo-Json -InputObject $config -Depth 6), [Text.UTF8Encoding]::new($false))
$execution = $null; $validation = $null; $scrollValidation = $null; $success = $false; $failure = $null
try {
    $execution = Invoke-CorpusProcess $executable $output $TimeoutSeconds
    Require (-not $execution.timedOut -and $execution.exitCode -eq 0) 'Corpus process failed or exceeded its deadline; evidence retained, no retry.'
    Require ((ConvertTo-Json -InputObject @(Get-CorpusProgramIdentity $executable) -Depth 4 -Compress) -ceq
        (ConvertTo-Json -InputObject $program -Depth 4 -Compress)) 'Corpus program files changed during acceptance.'
    $smoke = Read-CorpusJson $output
    $reportPath = Join-Path $session 'ocr-corpus.json'
    $report = Read-CorpusJson $reportPath
    $validation = Assert-CorpusReport $smoke $report $plan $execution.processId $Flavor $version (Join-Path $session 'ocr-corpus-fixture')
    $scrollValidation = Assert-CorpusScroll (Read-CorpusJson (Join-Path $session 'ocr-scroll.json')) $execution.processId $session
    $success = $true
} catch { $failure = $_.Exception.Message }
$summary = [ordered]@{ success = $success; execution = $execution; validation = $validation; scrollValidation = $scrollValidation; failure = $failure;
    processReportSha256 = if (Test-Path -LiteralPath $output) { (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash } else { $null };
    corpusReportSha256 = if (Test-Path -LiteralPath (Join-Path $session 'ocr-corpus.json')) { (Get-FileHash -LiteralPath (Join-Path $session 'ocr-corpus.json') -Algorithm SHA256).Hash } else { $null };
    scrollReportSha256 = if (Test-Path -LiteralPath (Join-Path $session 'ocr-scroll.json')) { (Get-FileHash -LiteralPath (Join-Path $session 'ocr-scroll.json') -Algorithm SHA256).Hash } else { $null };
    accuracyClaim = 'Only four required generated-image expectations are acceptance gates; six observational grades never become accuracy guarantees.' }
[IO.File]::WriteAllText((Join-Path $session 'corpus-summary.json'), (ConvertTo-Json -InputObject $summary -Depth 10), [Text.UTF8Encoding]::new($false))
if (-not $success) { throw "OCR corpus acceptance failed: $failure" }
Write-Output "PASS $Flavor corpus: $($validation.requiredMatches)/4 required; $($validation.observationalMatches)/6 observational matches; $($validation.recognitions) recognitions, $($validation.mockTranslations) mock requests."
Write-Output "Report: $session"
