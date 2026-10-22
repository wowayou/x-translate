# Fixed owned fixtures only. Does not launch/rebuild XTranslate or read an installed data directory.
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$verifier = Join-Path $root 'tools/update-e2e/Run-UpdateE2E.ps1'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root ('artifacts/verification/update-data-tests-' + [Guid]::NewGuid().ToString('N')) }
$work = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $work) { throw 'The test output directory must be new; no prior evidence is overwritten.' }
[IO.Directory]::CreateDirectory($work) | Out-Null
$checks = [Collections.Generic.List[string]]::new()
# Read the real verifier's function definitions without executing its top-level package/install workflow.
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($verifier, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw 'Update verifier has PowerShell parse errors.' }
$functions = $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $true)
foreach ($name in @('Require', 'ReadDataFixtureFiles', 'DataHashes', 'Run')) {
    $function = @($functions | Where-Object Name -eq $name)
    if ($function.Count -ne 1) { throw "Verifier function '$name' was not uniquely selected." }
    . ([ScriptBlock]::Create($function[0].Extent.Text))
}
function Check([bool]$value, [string]$message) { if (-not $value) { throw $message } }
function Reject([scriptblock]$action, [string]$expected) {
    $reason = $null
    try { & $action | Out-Null } catch { $reason = $_.Exception.Message }
    Check ($null -ne $reason -and $reason.Contains($expected)) 'Expected bounded fixture rejection did not occur.'
}
$fixture = Join-Path $work 'fixture'
[IO.Directory]::CreateDirectory((Join-Path $fixture 'learning')) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $fixture 'nested')) | Out-Null
[IO.File]::WriteAllText((Join-Path $fixture 'learning/book.json'), '{"fixed":"learning fixture"}')
[IO.File]::WriteAllBytes((Join-Path $fixture 'nested/unknown.bin'), [byte[]]@(0,1,2,255))
Check (@(ReadDataFixtureFiles $fixture).Count -eq 2) 'Bounded scanner lost fixed nested files.'
$checks.Add('Bounded scanner retains a fixed nested learning and binary fixture')
Reject { ReadDataFixtureFiles (Join-Path $work 'missing') } 'does not exist'
$checks.Add('Missing fixture is refused')
$large = Join-Path $work 'large'
[IO.Directory]::CreateDirectory($large) | Out-Null
$file = [IO.File]::Create((Join-Path $large 'large.bin'))
try { $file.SetLength(16MB + 1) } finally { $file.Dispose() }
Reject { ReadDataFixtureFiles $large } '128-entry / 16 MiB'
$checks.Add('Oversized fixture is refused before package workflow')
$many = Join-Path $work 'many'
[IO.Directory]::CreateDirectory($many) | Out-Null
for ($i = 0; $i -lt 129; $i++) { [IO.File]::WriteAllText((Join-Path $many "$i.txt"), '') }
Reject { ReadDataFixtureFiles $many } '128-entry / 16 MiB'
$checks.Add('The 129th fixture entry is refused during lazy enumeration')
$linkRoot = Join-Path $work 'links'
[IO.Directory]::CreateDirectory($linkRoot) | Out-Null
$link = Join-Path $linkRoot 'junction'
& $env:ComSpec /d /c "mklink /J `"$link`" `"$fixture`"" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not create the owned directory-junction fixture.' }
Reject { ReadDataFixtureFiles $linkRoot } 'link/reparse'
Reject { ReadDataFixtureFiles $link } 'link/reparse'
$checks.Add('Root and child directory junctions are refused before traversal')
$install = Join-Path $work 'owned install'
[IO.Directory]::CreateDirectory((Join-Path $install 'data/learning')) | Out-Null
[IO.File]::WriteAllText((Join-Path $install 'data/learning/book.json'), '{"fixed":"old"}')
[IO.File]::WriteAllText((Join-Path $install 'data/settings.json'), '{"fixed":"settings"}')
$before = DataHashes $install
[IO.File]::WriteAllText((Join-Path $install 'data/learning/book.json'), '{"fixed":"modified"}')
Check ($before -ne (DataHashes $install)) 'A nested notebook change was invisible to recursive data hashes.'
[IO.File]::WriteAllText((Join-Path $install 'data/learning/book.json'), '{"fixed":"old"}')
$checks.Add('Recursive data identity detects a nested learning notebook edit')
# Scope replacement only for child launch: exercise the production Run hash boundary without launching a
# real product. The fake child models a bug that corrupts nested data then otherwise exits successfully.
$script:mutate = $true
$script:launches = 0
function Start-Process {
    param($FilePath, $ArgumentList, [switch]$PassThru, $WindowStyle)
    $script:launches++
    if ($script:mutate) { [IO.File]::WriteAllText((Join-Path (Split-Path -Parent $FilePath) 'data/learning/book.json'), '{"fixed":"corrupted"}') }
    $child = [PSCustomObject]@{ Handle = [IntPtr]1; HasExited = $true; ExitCode = 0; Id = 1001 }
    $child | Add-Member ScriptMethod WaitForExit { param($milliseconds) return $true }
    $child | Add-Member ScriptMethod Dispose { }
    return $child
}
$ProcessTimeoutSeconds = 15
$attempts = [Collections.Generic.List[object]]::new()
Reject { Run (Join-Path $install 'XTranslate.exe') @('--cleanup-only') } 'changed a data file'
Check ($attempts.Count -eq 1 -and -not $attempts[0].dataUnchanged -and $script:launches -eq 1) 'Data mutation was not reported by Run.'
$checks.Add('Production update Run rejects a successful child that modifies the nested notebook')
[IO.File]::WriteAllText((Join-Path $install 'data/learning/book.json'), '{"fixed":"old"}')
$script:mutate = $false
Check ((Run (Join-Path $install 'XTranslate.exe') @('--cleanup-only')) -eq 0 -and $attempts[1].dataUnchanged) 'Unchanged child was not accepted.'
$checks.Add('Production update Run accepts an unchanged successful child')
$hidden = Join-Path $install 'data/hidden.bin'
[IO.File]::WriteAllBytes($hidden, [byte[]]@(1,2,3))
[IO.File]::SetAttributes($hidden, [IO.FileAttributes]::Hidden)
$before = DataHashes $install
$stream = [IO.File]::Open($hidden, [IO.FileMode]::Open, [IO.FileAccess]::Write)
try { $stream.WriteByte(4) } finally { $stream.Dispose() }
Check ($before -ne (DataHashes $install)) 'A hidden data-file edit was invisible to recursive hashes.'
$checks.Add('Recursive data identity includes hidden files')
# Removing rather than editing is a distinct boundary: missing nested file changes the retained identity.
$before = DataHashes $install
[IO.File]::Delete((Join-Path $install 'data/learning/book.json'))
Check ($before -ne (DataHashes $install)) 'Removing a nested notebook did not change data identity.'
$checks.Add('Recursive data identity detects nested notebook removal')
$json = [ordered]@{ success = $true; checks = $checks; count = $checks.Count; fixtureOnly = $true; verifier = 'tools/update-e2e/Run-UpdateE2E.ps1';
    note = 'Production PowerShell functions loaded from the real verifier AST. Child execution replaced only in this process; no XTranslate processes or network requests.' } | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText((Join-Path $work 'update-data-tests.json'), $json, [Text.UTF8Encoding]::new($false))
$checks | ForEach-Object { Write-Output ('PASS ' + $_) }
Write-Output ("{0}/{0} update data verifier checks passed" -f $checks.Count)
