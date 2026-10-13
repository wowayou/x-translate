# Install or update a local XTranslate folder from source in one step.
#
#   ./install.ps1                       # default target: artifacts/XTranslate
#   ./install.ps1 -Target D:\Apps\XTranslate -Flavor Lite
#
# 1. Publishes into a fresh staging folder next to the target.
# 2. Runs Core tests and the WPF smoke test against the staged EXE (skip with -SkipTests).
# 3. If this target's XTranslate is running, asks it to exit through its normal tray-exit path.
#    It never force-kills, and never closes an instance running from another folder.
# 4. Swaps folders: the old program becomes <Target>.previous (one rollback copy), the target's
#    data/ (settings, encrypted keys, logs) is moved into the new program folder unchanged.
# 5. First install only: copies settings.json from the source tree's Release build, if present.
# 6. Restarts the app if it was running (or with -Launch).
# No registry, installer, shortcut or system location is touched.
param(
    [string]$Target = (Join-Path $PSScriptRoot 'artifacts\XTranslate'),
    [ValidateSet('Portable', 'Lite')][string]$Flavor = 'Portable',
    [switch]$SkipTests,
    [switch]$Launch,
    [ValidateRange(3, 60)][int]$ExitTimeoutSeconds = 10
)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools/cli'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$sdk = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet -ErrorAction Stop).Source }

$targetPath = [IO.Path]::GetFullPath($Target).TrimEnd('\')
$parent = Split-Path -Parent $targetPath
if (-not $parent -or $targetPath -eq [IO.Path]::GetPathRoot($targetPath)) { throw "Refusing to install into a drive root: $targetPath" }
if ($targetPath -eq $projectRoot.TrimEnd('\') -or $projectRoot.StartsWith($targetPath + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Target must not be the source tree or one of its parents: $targetPath"
}
$targetExe = Join-Path $targetPath 'XTranslate.exe'
if ((Test-Path -LiteralPath $targetPath) -and (Get-ChildItem -LiteralPath $targetPath -Force | Select-Object -First 1) -and
    -not (Test-Path -LiteralPath $targetExe)) {
    throw "Target exists, is not empty and is not an XTranslate folder; nothing was changed: $targetPath"
}
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$staging = "$targetPath.staging-" + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$backup = "$targetPath.previous"

function Step([string]$text) { Write-Host ('==> ' + $text) }

Step "Publishing $Flavor build"
$flavorArguments = if ($Flavor -eq 'Portable') {
    @('--self-contained', 'true', '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=false', '-p:IncludeNativeLibrariesForSelfExtract=false')
} else { @('--self-contained', 'false') }
& $sdk publish (Join-Path $projectRoot 'src/XTranslate.App/XTranslate.App.csproj') -c Release -r win-x64 @flavorArguments "-p:UpdateFlavor=$Flavor" `
    -p:DebugType=None -p:DebugSymbols=false -o $staging --nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed; the installed copy was not changed. Staging: $staging" }
$readmeName = (-join [char[]](0x4F7F, 0x7528, 0x8BF4, 0x660E)) + '.md'
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $staging $readmeName)
$stagedExe = Join-Path $staging 'XTranslate.exe'

if (-not $SkipTests) {
    Step 'Verifying the staged build (Core tests + WPF smoke)'
    try { & (Join-Path $projectRoot 'verify.ps1') -Executable $stagedExe -ReportDirectory (Join-Path $projectRoot 'artifacts/verification/install') }
    catch { throw "Verification failed ($($_.Exception.Message)); the installed copy was not changed. Staging: $staging" }
}
# Smoke runs write fixture data beside the EXE; the shipped folder must not contain it.
foreach ($name in 'smoke-data', 'smoke-result.json', 'benchmark-data', 'memory-diagnostics-data') {
    $item = Join-Path $staging $name
    if (Test-Path -LiteralPath $item) { Remove-Item -LiteralPath $item -Recurse -Force }
}
$forbidden = Get-ChildItem -LiteralPath $staging -Recurse -Force | Where-Object { $_.Name -match '^(data|smoke-data|benchmark-data|verification)(-|$)' }
if ($forbidden) { throw "Staging contains user/test data; nothing was changed: $($forbidden.FullName -join ', ')" }

$running = @(Get-Process XTranslate -ErrorAction SilentlyContinue)
$ours = @($running | Where-Object { $_.Path -eq $targetExe })
$others = @($running | Where-Object { $_.Path -ne $targetExe })
$wasRunning = $ours.Count -gt 0
if ($wasRunning) {
    Step 'Asking the running XTranslate to exit'
    & $stagedExe --exit
    $deadline = [DateTime]::UtcNow.AddSeconds($ExitTimeoutSeconds)
    while (($ours | Where-Object { -not $_.HasExited }) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 200; $ours | ForEach-Object { $_.Refresh() } }
    if ($ours | Where-Object { -not $_.HasExited }) {
        throw "XTranslate did not exit (Settings may be open, or the running version predates --exit). Exit it from the tray and run again; nothing was changed. Staging kept: $staging"
    }
}

Step "Installing to $targetPath"
$firstInstall = -not (Test-Path -LiteralPath $targetExe)
if (Test-Path -LiteralPath $backup) {
    # Only program files are ever replaced. A backup still holding data/ means an earlier run was interrupted.
    if (Test-Path -LiteralPath (Join-Path $backup 'data')) { throw "$backup still contains data/. Move it into $targetPath manually; nothing was changed." }
    Remove-Item -LiteralPath $backup -Recurse -Force
}
if (-not $firstInstall) { Move-Item -LiteralPath $targetPath -Destination $backup }
elseif (Test-Path -LiteralPath $targetPath) { Remove-Item -LiteralPath $targetPath -Force } # empty folder
try {
    Move-Item -LiteralPath $staging -Destination $targetPath
    $oldData = Join-Path $backup 'data'
    if (-not $firstInstall -and (Test-Path -LiteralPath $oldData)) { Move-Item -LiteralPath $oldData -Destination (Join-Path $targetPath 'data') }
}
catch {
    if (-not (Test-Path -LiteralPath $targetPath) -and (Test-Path -LiteralPath $backup)) { Move-Item -LiteralPath $backup -Destination $targetPath }
    throw
}

$devSettings = Join-Path $projectRoot 'src/XTranslate.App/bin/Release/net8.0-windows/data/settings.json'
$targetSettings = Join-Path $targetPath 'data/settings.json'
if (-not (Test-Path -LiteralPath $targetSettings) -and (Test-Path -LiteralPath $devSettings)) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $targetSettings) -Force | Out-Null
    Copy-Item -LiteralPath $devSettings -Destination $targetSettings
    Step 'Imported settings from the source-tree build (copy; keys stay encrypted for this Windows user)'
}

if ($others.Count -gt 0) {
    Write-Host "Note: another XTranslate is running from $($others[0].Path). Exit it from the tray before starting this copy."
} elseif ($wasRunning -or $Launch) {
    Start-Process -FilePath $targetExe
    Step 'Started XTranslate'
}
Write-Host ''
Write-Host "Installed: $targetExe"
if (-not $firstInstall) { Write-Host "Previous program files (no data): $backup" }
