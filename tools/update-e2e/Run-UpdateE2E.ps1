# End-to-end in-place update with real published binaries, in a temporary folder.
# Installs portable X.Y.Z, builds X.Y.(Z+1), applies it headlessly, then checks version, data, cleanup,
# tampered digest, partial-replacement rollback and downgrade refusal for both editions.
# No network, no registry, no running user instance is touched. Supplied ZIP bytes are preserved.
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\..\artifacts\verification\update-e2e'),
    [string]$PortableFrom, [string]$PortableTo, [string]$LiteFrom, [string]$LiteTo,
    [string]$DataFixtureDirectory,
    [ValidateRange(15, 180)][int]$ProcessTimeoutSeconds = 60,
    [string]$StackTool,
    [switch]$KeepWorkOnFailure
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$sdk = Join-Path $root '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_ROOT = Split-Path -Parent $sdk # Lite (framework-dependent) builds run on the SDK's runtime.
$project = Join-Path $root 'src/XTranslate.App/XTranslate.App.csproj'
$version = [string](([xml](Get-Content -Raw -LiteralPath $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
$parts = $version.Split('.'); $next = "$($parts[0]).$($parts[1]).$([int]$parts[2] + 1)"
$packageMode = [bool]($PortableFrom -or $PortableTo -or $LiteFrom -or $LiteTo)
if ($packageMode) {
    if (-not ($PortableFrom -and $PortableTo -and $LiteFrom -and $LiteTo)) { throw 'Supply all four package paths, or none to build test versions.' }
    foreach ($path in @($PortableFrom, $PortableTo, $LiteFrom, $LiteTo)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Package not found: $path" }
        if (-not (Test-Path -LiteralPath "$path.sha256" -PathType Leaf)) { throw "Package checksum not found: $path.sha256" }
        $expected = (Get-Content -Raw -LiteralPath "$path.sha256").Trim().Split(' ')[0]
        if ($expected -notmatch '^[0-9a-fA-F]{64}$' -or (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash -ne $expected) {
            throw "Package checksum mismatch: $path"
        }
    }
    if ((Split-Path -Leaf $PortableFrom) -notmatch '^XTranslate-(\d+\.\d+\.\d+)-win-x64-portable\.zip$') { throw 'Invalid source package name.' }
    $version = $Matches[1]
    if ((Split-Path -Leaf $PortableTo) -notmatch '^XTranslate-(\d+\.\d+\.\d+)-win-x64-portable\.zip$') { throw 'Invalid candidate package name.' }
    $next = $Matches[1]
    if ((Split-Path -Leaf $LiteFrom) -ne "XTranslate-$version-win-x64-lite.zip" -or
        (Split-Path -Leaf $LiteTo) -ne "XTranslate-$next-win-x64-lite.zip") { throw 'Portable and Lite package versions do not agree.' }
}
# Spaces and CJK characters in the path catch quoting and encoding bugs.
$work = Join-Path ([IO.Path]::GetTempPath()) ('xtranslate update e2e ' + [string][char]0x6D4B + [string][char]0x8BD5 + ' ' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$install = Join-Path $work 'XTranslate'
$checks = [Collections.Generic.List[string]]::new()
$attempts = [Collections.Generic.List[object]]::new()
. (Join-Path $PSScriptRoot 'UpdateData.ps1')
$fixtureFiles = @()
if ($DataFixtureDirectory) {
    $DataFixtureDirectory = [IO.Path]::GetFullPath($DataFixtureDirectory)
    if ($DataFixtureDirectory.Length -gt [IO.Path]::GetPathRoot($DataFixtureDirectory).Length) {
        $DataFixtureDirectory = $DataFixtureDirectory.TrimEnd([IO.Path]::DirectorySeparatorChar)
    }
    $fixtureFiles = @(ReadDataFixtureFiles $DataFixtureDirectory)
}
if ($StackTool -and -not (Test-Path -LiteralPath $StackTool -PathType Leaf)) { throw "Stack tool not found: $StackTool" }
function Require([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
function Publish([string]$v, [string]$dir, [string]$flavor = 'Portable') {
    if ($packageMode) {
        $archive = if ($flavor -eq 'Portable') { if ($v -eq $version) { $PortableFrom } else { $PortableTo } }
                   else { if ($v -eq $version) { $LiteFrom } else { $LiteTo } }
        Expand-Archive -LiteralPath $archive -DestinationPath $dir
        Require ((Get-Item (Join-Path $dir 'XTranslate.exe')).VersionInfo.ProductVersion -like "$v*") 'Package version differs from its name'
        return
    }
    $flavorArgs = if ($flavor -eq 'Portable') { @('--self-contained', 'true', '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=false', '-p:IncludeNativeLibrariesForSelfExtract=false') }
                  else { @('--self-contained', 'false') }
    & $sdk publish $project -c Release -r win-x64 @flavorArgs "-p:UpdateFlavor=$flavor" "-p:Version=$v" -p:DebugType=None -p:DebugSymbols=false -o $dir --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Publish $v $flavor failed" }
}
function Run([string]$exe, [string[]]$arguments) {
    $directory = Split-Path -Parent $exe
    $dataBefore = DataHashes $directory
    $p = Start-Process -FilePath $exe -ArgumentList ($arguments | ForEach-Object { '"' + $_ + '"' }) -PassThru -WindowStyle Hidden
    $attempt = [ordered]@{ executable = $exe; arguments = $arguments; processId = $p.Id; startedUtc = [DateTimeOffset]::UtcNow; timedOut = $false }
    $attempts.Add($attempt)
    try {
        $null = $p.Handle # Retain the owned process handle so fast child exit codes remain available on Windows PowerShell.
        if (-not $p.WaitForExit($ProcessTimeoutSeconds * 1000)) {
            $attempt.timedOut = $true
            $attempt['cpuMs'] = $p.TotalProcessorTime.TotalMilliseconds
            $attempt['windowTitle'] = $p.MainWindowTitle
            $attempt['installEntries'] = @(Get-ChildItem -LiteralPath (Split-Path -Parent $exe) | Select-Object Name, Length)
            if ($StackTool) {
                $stack = Start-Process -FilePath $StackTool -ArgumentList @('report', '--process-id', [string]$p.Id) -PassThru -WindowStyle Hidden `
                    -RedirectStandardOutput (Join-Path $work "timeout-$($p.Id)-stack.log") -RedirectStandardError (Join-Path $work "timeout-$($p.Id)-stack-error.log")
                try {
                    if (-not $stack.WaitForExit(15000)) { $stack.Kill(); $stack.WaitForExit(5000) | Out-Null }
                    $attempt['stackExitCode'] = $stack.ExitCode
                } finally { $stack.Dispose() }
            }
            $p.Kill()
            $p.WaitForExit(5000) | Out-Null
            throw "Update $($arguments[0]) timed out; only its own PID $($p.Id) was stopped."
        }
        $attempt['exitCode'] = $p.ExitCode
        $dataAfter = DataHashes $directory
        $attempt['dataUnchanged'] = $dataBefore -eq $dataAfter
        $attempt['dataFiles'] = @((ConvertFrom-Json -InputObject $dataBefore) | ForEach-Object {
            [ordered]@{ name = [string]$_.name; sha256 = [string]$_.sha256 }
        })
        Require ($dataBefore -eq $dataAfter) "Update/cleanup operation $($arguments[0]) changed a data file"
        return $p.ExitCode
    } finally {
        $attempt['completedUtc'] = [DateTimeOffset]::UtcNow
        if (-not $p.HasExited) { $p.Kill(); $p.WaitForExit(5000) | Out-Null }
        $p.Dispose()
    }
}
function ProgramHashes([string]$directory) {
    return ((Get-ChildItem -LiteralPath $directory -File | Sort-Object Name | ForEach-Object {
        [ordered]@{ name = $_.Name; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    }) | ConvertTo-Json -Compress)
}
function VerifyRollback([string]$directory, [string]$zip, [string]$label) {
    $before = ProgramHashes $directory
    $dataPath = Join-Path $directory 'data/settings.json'
    $dataBefore = (Get-FileHash -LiteralPath $dataPath -Algorithm SHA256).Hash
    $lockPath = (Get-ChildItem -LiteralPath $directory -File | Where-Object Name -ne 'XTranslate.exe' | Sort-Object Name | Select-Object -Last 1).FullName
    Require (-not [string]::IsNullOrEmpty($lockPath)) 'No program file available for rollback lock'
    $watcher = [IO.FileSystemWatcher]::new($directory, '*.old')
    $eventId = 'XTranslate-Rollback-' + [Guid]::NewGuid().ToString('N')
    $subscription = Register-ObjectEvent -InputObject $watcher -EventName Renamed -SourceIdentifier $eventId
    $locked = $null
    $report = Join-Path $work "$label-rollback.json"
    try {
        $watcher.EnableRaisingEvents = $true
        $locked = [IO.File]::Open($lockPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        Require ((Run (Join-Path $directory 'XTranslate.exe') @('--apply-update', $zip, $report)) -eq 1) "$label locked-file update was accepted"
        Require ((Get-Content -Raw -LiteralPath $report | ConvertFrom-Json).code -eq 'Apply') "$label rollback returned the wrong error"
        # A refusal before replacing any file is not evidence of partial-replacement rollback.
        $deadline = [DateTime]::UtcNow.AddSeconds(2)
        do {
            $renames = @(Get-Event -SourceIdentifier $eventId -ErrorAction SilentlyContinue)
            if ($renames.Count -gt 0) { break }
            Start-Sleep -Milliseconds 50
        } while ([DateTime]::UtcNow -lt $deadline)
        Require ($renames.Count -gt 0) "$label rollback did not observe an earlier file being renamed to .old"
        Require ((ProgramHashes $directory) -eq $before) "$label rollback changed a program file or left .old files"
        Require ((Get-FileHash -LiteralPath $dataPath -Algorithm SHA256).Hash -eq $dataBefore) "$label rollback modified data"
        Require (@(Get-ChildItem -LiteralPath $directory -Directory -Filter '.update-*').Count -eq 0) "$label rollback left staging files"
        [ordered]@{ lockedFile = Split-Path -Leaf $lockPath; renamedBeforeFailure = @($renames | ForEach-Object { $_.SourceEventArgs.Name });
            originalProgramHashes = ($before | ConvertFrom-Json); dataSha256 = $dataBefore; allOriginalBytesRestored = $true } |
            ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $work "$label-rollback-evidence.json") -Encoding UTF8
        $checks.Add("$label`: after partial replacement, a locked file triggers rollback; every original program/data hash is restored")
    } finally {
        if ($null -ne $locked) { $locked.Dispose() }
        Unregister-Event -SourceIdentifier $eventId -ErrorAction SilentlyContinue
        Get-Event -SourceIdentifier $eventId -ErrorAction SilentlyContinue | Remove-Event
        $watcher.Dispose()
    }
}
try {
    New-Item -ItemType Directory -Path $work | Out-Null
    Publish $version $install
    $newDir = Join-Path $work 'new'; Publish $next $newDir
    $old = Join-Path $work "XTranslate-$version-win-x64-portable.zip"
    if ($packageMode) { Copy-Item -LiteralPath $PortableFrom -Destination $old }
    else { Compress-Archive -Path (Join-Path $install '*') -DestinationPath $old }
    New-Item -ItemType Directory -Path (Join-Path $install 'data') | Out-Null
    Set-Content -LiteralPath (Join-Path $install 'data/settings.json') -Value '{"marker":"user-data"}' -NoNewline
    AddDataFixture $install $DataFixtureDirectory $fixtureFiles
    $zip = Join-Path $work "XTranslate-$next-win-x64-portable.zip"
    if ($packageMode) { Copy-Item -LiteralPath $PortableTo -Destination $zip }
    else { Compress-Archive -Path (Join-Path $newDir '*') -DestinationPath $zip }
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash.ToLowerInvariant()
    $exe = Join-Path $install 'XTranslate.exe'

    Set-Content -LiteralPath "$zip.sha256" -Value ('0' * 64) -NoNewline
    Require ((Run $exe @('--apply-update', $zip, "$work\tampered.json")) -eq 1) 'Tampered digest was accepted'
    Require ((Get-Content -Raw "$work\tampered.json" | ConvertFrom-Json).code -eq 'Integrity') 'Tampered digest gave the wrong error'
    Require ((Get-Item $exe).VersionInfo.ProductVersion -like "$version*") 'Rejected update changed the program'
    $checks.Add('A package whose SHA-256 does not match is refused and nothing changes')

    Set-Content -LiteralPath "$zip.sha256" -Value "$hash  $(Split-Path -Leaf $zip)" -NoNewline
    VerifyRollback $install $zip 'Portable'
    Require ((Run $exe @('--apply-update', $zip, "$work\apply.json")) -eq 0) "Update failed: $(Get-Content -Raw "$work\apply.json")"
    Require ((Get-Item $exe).VersionInfo.ProductVersion -like "$next*") 'Program version did not change'
    Require ((Get-Content -Raw (Join-Path $install 'data/settings.json')) -eq '{"marker":"user-data"}') 'data/ was modified'
    Require (@(Get-ChildItem $install -Filter '*.old').Count -gt 0) 'In-place replacement did not use the rename path'
    $checks.Add("The running $version EXE replaced itself with $next in place; data/ was untouched")

    Require ((Run $exe @('--cleanup-only')) -eq 0) 'Cleanup run failed'
    Require (@(Get-ChildItem $install -Filter '*.old').Count -eq 0 -and @(Get-ChildItem $install -Directory -Filter '.update-*').Count -eq 0) 'Leftovers were not cleaned'
    $checks.Add('The next start removed every *.old file and staging folder')

    Require ((Run $exe @('--apply-update', $old, "$work\downgrade.json")) -eq 1) 'Downgrade was accepted'
    Require ((Get-Content -Raw "$work\downgrade.json" | ConvertFrom-Json).code -eq 'NotNewer') 'Downgrade gave the wrong error'
    $checks.Add('A genuinely older package is refused')

    # Lite edition: a normal upgrade, and a package that needs a runtime this PC lacks.
    $lite = Join-Path $work 'Lite'; Publish $version $lite 'Lite'
    $liteNew = Join-Path $work 'lite-new'; Publish $next $liteNew 'Lite'
    $liteOld = Join-Path $work "XTranslate-$version-win-x64-lite.zip"
    if ($packageMode) { Copy-Item -LiteralPath $LiteFrom -Destination $liteOld }
    else { Compress-Archive -Path (Join-Path $lite '*') -DestinationPath $liteOld }
    $liteExe = Join-Path $lite 'XTranslate.exe'
    New-Item -ItemType Directory -Path (Join-Path $lite 'data') | Out-Null
    Set-Content -LiteralPath (Join-Path $lite 'data/settings.json') -Value '{"marker":"lite-user-data"}' -NoNewline
    AddDataFixture $lite $DataFixtureDirectory $fixtureFiles
    $liteDataHash = (Get-FileHash -LiteralPath (Join-Path $lite 'data/settings.json') -Algorithm SHA256).Hash
    $future = Join-Path $work 'lite-future'; Copy-Item -LiteralPath $liteNew -Destination $future -Recurse
    $config = Join-Path $future 'XTranslate.runtimeconfig.json'
    $runtimeConfig = Get-Content -Raw -LiteralPath $config | ConvertFrom-Json
    $frameworks = @($runtimeConfig.runtimeOptions.frameworks) + @($runtimeConfig.runtimeOptions.framework) | Where-Object { $null -ne $_ }
    Require (@($frameworks).Count -gt 0) 'Lite test package did not declare a shared runtime'
    foreach ($framework in $frameworks) { $framework.version = '99.0.0' }
    $runtimeConfig | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $config -Encoding UTF8
    $futureZip = Join-Path $work "future-lite.zip"; Compress-Archive -Path (Join-Path $future '*') -DestinationPath $futureZip
    Require ((Run $liteExe @('--apply-update', $futureZip, "$work\runtime.json")) -eq 1) 'A package needing a missing runtime was accepted'
    Require ((Get-Content -Raw "$work\runtime.json" | ConvertFrom-Json).code -eq 'RuntimeMissing') 'Missing runtime gave the wrong error'
    Require ((Get-Item $liteExe).VersionInfo.ProductVersion -like "$version*") 'Refused Lite update changed the program'
    $checks.Add('Lite: a package that needs a .NET runtime this PC lacks is refused before any change')
    $liteZip = Join-Path $work "XTranslate-$next-win-x64-lite.zip"
    if ($packageMode) { Copy-Item -LiteralPath $LiteTo -Destination $liteZip }
    else { Compress-Archive -Path (Join-Path $liteNew '*') -DestinationPath $liteZip }
    $liteHash = (Get-FileHash -LiteralPath $liteZip -Algorithm SHA256).Hash
    Set-Content -LiteralPath "$liteZip.sha256" -Value ('0' * 64) -NoNewline
    Require ((Run $liteExe @('--apply-update', $liteZip, "$work\lite-tampered.json")) -eq 1) 'Lite tampered digest was accepted'
    Require ((Get-Content -Raw "$work\lite-tampered.json" | ConvertFrom-Json).code -eq 'Integrity') 'Lite tampered digest gave the wrong error'
    Set-Content -LiteralPath "$liteZip.sha256" -Value $liteHash -NoNewline
    VerifyRollback $lite $liteZip 'Lite'
    Require ((Run $liteExe @('--apply-update', $liteZip, "$work\lite.json")) -eq 0) "Lite update failed: $(Get-Content -Raw "$work\lite.json")"
    Require ((Get-Item $liteExe).VersionInfo.ProductVersion -like "$next*") 'Lite version did not change'
    Require ((Run $liteExe @('--cleanup-only')) -eq 0 -and @(Get-ChildItem $lite -Filter '*.old').Count -eq 0) 'Lite cleanup failed'
    Require ((Get-FileHash -LiteralPath (Join-Path $lite 'data/settings.json') -Algorithm SHA256).Hash -eq $liteDataHash) 'Lite update or cleanup changed user data'
    $checks.Add("Lite: $version replaced itself with $next (digest/runtime checks passed), preserved data and cleaned up")
    Require ((Run $liteExe @('--apply-update', $liteOld, "$work\lite-downgrade.json")) -eq 1) 'Lite downgrade was accepted'
    Require ((Get-Content -Raw "$work\lite-downgrade.json" | ConvertFrom-Json).code -eq 'NotNewer') 'Lite downgrade gave the wrong error'
    $checks.Add('Lite: a genuinely older package is refused')
    $success = $true
} catch { $failure = $_.Exception.Message; $success = $false }
finally {
    New-Item -ItemType Directory -Path $Output -Force | Out-Null
    $packageEvidence = if ($packageMode) { @($PortableFrom, $PortableTo, $LiteFrom, $LiteTo) | ForEach-Object {
        [ordered]@{ path = [IO.Path]::GetFullPath($_); sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }
    } } else { @() }
    Get-ChildItem -LiteralPath $work -File -ErrorAction SilentlyContinue | Where-Object Extension -in @('.json', '.log') | Copy-Item -Destination $Output
    [ordered]@{ success = $success; from = $version; to = $next; suppliedPackages = $packageMode; packages = @($packageEvidence); attempts = $attempts;
        processTimeoutSeconds = $ProcessTimeoutSeconds; dataFixtureFileCount = $fixtureFiles.Count; dataFixtureScope = 'Caller-selected fixed fixtures copied to owned temporary installs; recursive data hashes checked after every update/cleanup invocation'; checks = $checks; error = $failure;
        retainedWorkDirectory = if (-not $success -and $KeepWorkOnFailure) { $work } else { $null } } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Output 'update-e2e.json') -Encoding UTF8
    if ($success -or -not $KeepWorkOnFailure) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
}
$checks | ForEach-Object { Write-Host "PASS $_" }
if (-not $success) { Write-Host "FAIL $failure"; exit 1 }
exit 0
