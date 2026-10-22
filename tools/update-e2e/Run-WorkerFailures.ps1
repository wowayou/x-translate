# Real staged-worker boundaries with an isolated, explicitly supplied test parent; no product test delays.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CandidatePackage,
    [Parameter(Mandatory)][string]$ParentExecutable,
    [Parameter(Mandatory)][string]$Output,
    [ValidateRange(10,60)][int]$ProcessTimeoutSeconds = 25
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'UpdateData.ps1')
$CandidatePackage = [IO.Path]::GetFullPath($CandidatePackage)
$ParentExecutable = [IO.Path]::GetFullPath($ParentExecutable)
$Output = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $Output) { throw 'Use a new report directory' }
if (-not (Test-Path -LiteralPath "$CandidatePackage.sha256")) { throw 'Candidate checksum required' }
$sha = (Get-Content -Raw -LiteralPath "$CandidatePackage.sha256").Trim().Split(' ')[0]
if ($sha -notmatch '^[0-9a-fA-F]{64}$' -or (Get-FileHash -LiteralPath $CandidatePackage -Algorithm SHA256).Hash -ne $sha) { throw 'Candidate checksum mismatch' }
$normal = $null
if ([Threading.Mutex]::TryOpenExisting('Local\XTranslate.v01', [ref]$normal)) {
    $normal.Dispose(); throw 'Normal application exists; no fixture process started'
}
[IO.Directory]::CreateDirectory($Output) | Out-Null
$checks = [Collections.Generic.List[object]]::new()
$success = $false
$failure = $null
function Require([bool]$value, [string]$message) { if (-not $value) { throw $message } }
try {
    foreach ($kind in @('Killed','Nonzero','MutexHeld')) {
        $install = Join-Path $Output $kind
        [IO.Directory]::CreateDirectory((Join-Path $install 'data/nested')) | Out-Null
        # ParentExecutable must come from tests/XTranslate.UpdateHost.Tests, never a user's executable.
        Copy-Item -Path (Join-Path (Split-Path -Parent $ParentExecutable) '*') -Destination $install
        [IO.File]::WriteAllText((Join-Path $install 'data/nested/fixed.json'), 'fixed worker failure data')
        $before = DataHashes $install
        $exe = Join-Path $install 'XTranslate.exe'
        $exeHash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
        $report = Join-Path $Output "$kind-result.json"
        $ready = Join-Path $Output "$kind-ready.json"
        $code = if ($kind -eq 'Killed') { -1 } elseif ($kind -eq 'Nonzero') { 7 } else { 0 }
        $held = $null; $ownsHeld = $false; $parent = $null; $worker = $null; $workerOwned = $false
        try {
            if ($kind -eq 'MutexHeld') {
                $held = [Threading.Mutex]::new($true, 'Local\XTranslate.v01', [ref]$ownsHeld)
                Require $ownsHeld 'Unexpected normal mutex contention'
            }
            $arguments = @('--prepared-parent', $CandidatePackage, $report, [string]$code, $ready)
            $parent = Start-Process -FilePath $exe -ArgumentList ($arguments | ForEach-Object { '"' + $_ + '"' }) -WorkingDirectory $install -WindowStyle Hidden -PassThru
            $null = $parent.Handle
            $watch = [Diagnostics.Stopwatch]::StartNew()
            do {
                if (Test-Path -LiteralPath $ready) { break }
                Require (-not $parent.HasExited) 'Owned parent exited before the worker handshake'
                Start-Sleep -Milliseconds 25
            } while ($watch.Elapsed.TotalSeconds -lt $ProcessTimeoutSeconds)
            Require (Test-Path -LiteralPath $ready) 'Worker parent handshake timed out'
            $identity = ReadUpdateReceipt $ready
            $worker = Get-Process -Id $identity.updaterProcessId -ErrorAction SilentlyContinue
            if ($null -ne $worker) {
                $null = $worker.Handle
                if (-not $worker.HasExited) {
                    Require ($worker.Path -eq (Join-Path $identity.stage '.update-worker/XTranslate.exe')) 'Worker PID ownership mismatch'
                    $workerOwned = $true
                } else { $worker.Dispose(); $worker = $null } # A fast refusal may exit before Path is queryable.
            }
            if ($kind -eq 'Killed') { $parent.Kill(); $parent.WaitForExit(5000) | Out-Null }
            else { Require ($parent.WaitForExit(5000)) 'Controlled parent did not exit' }
            if ($null -ne $worker) {
                Require ($worker.WaitForExit($ProcessTimeoutSeconds * 1000) -and $worker.ExitCode -eq 1) 'Owned failure worker timed out or returned success'
            }
            $result = ReadUpdateReceipt $report
            $expectedBoundary = if ($kind -eq 'MutexHeld') { 'mutex' } else { 'wait-parent' }
            Require ($null -ne $result -and -not $result.success -and $result.phase -eq 'complete' -and $result.boundary -eq $expectedBoundary) "Wrong failure boundary: $kind"
            Require ((DataHashes $install) -eq $before -and (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -eq $exeHash -and
                @(Get-ChildItem -LiteralPath $install -Filter '*.old' -File).Count -eq 0) "Refused worker modified program/data: $kind"
            Require (Test-Path -LiteralPath (Join-Path $install '.update-result.json')) 'Worker did not retain a failure receipt'
            $checks.Add([ordered]@{ scenario = $kind; boundary = $result.boundary; parentExitCode = $parent.ExitCode;
                workerProcessId = $identity.updaterProcessId; observedWorkerExitCode = if ($null -ne $worker) { $worker.ExitCode } else { $null };
                programDataUnchanged = $true })
            Write-Output "PASS $kind worker refusal"
        }
        finally {
            if ($null -ne $worker) {
                if ($workerOwned -and -not $worker.HasExited) { $worker.Kill(); $worker.WaitForExit(5000) | Out-Null }
                $worker.Dispose()
            }
            if ($null -ne $parent) {
                if (-not $parent.HasExited) { $parent.Kill(); $parent.WaitForExit(5000) | Out-Null }
                $parent.Dispose()
            }
            if ($ownsHeld) { $held.ReleaseMutex() }
            if ($null -ne $held) { $held.Dispose() }
        }
    }
    $success = $true
}
catch { $failure = $_.Exception.ToString(); throw }
finally {
    [ordered]@{ success = $success; checks = $checks; error = $failure; network = 'none'; userData = $false; testParent = $ParentExecutable } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Output 'worker-failures.json') -Encoding UTF8
}
