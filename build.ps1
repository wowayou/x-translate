param([switch]$SelfContained, [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools/cli'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$sdk = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet -ErrorAction Stop).Source }
Push-Location $projectRoot
try {
    if (-not $SkipTests) {
        & $sdk run --project tests/XTranslate.Core.Tests/XTranslate.Core.Tests.csproj -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
    }
    $projectXml = [xml](Get-Content -Raw -LiteralPath 'src/XTranslate.App/XTranslate.App.csproj')
    $version = [string]($projectXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
    if ($version -notmatch '^\d+\.\d+\.\d+([.-][A-Za-z0-9]+)*$') { throw 'Invalid or missing release version.' }
    $folderName = if ($SelfContained) { "XTranslate-$version-win-x64-standalone" } else { "XTranslate-$version-win-x64" }
    $publishDirectory = Join-Path $projectRoot ('artifacts/' + $folderName)
    $archivePath = Join-Path $projectRoot ('artifacts/' + $folderName + '.zip')
    if (Test-Path -LiteralPath $publishDirectory) {
        throw "Refusing to reuse existing publish directory '$publishDirectory'. Move it aside manually before rebuilding; no files or user data were deleted."
    }
    if (Test-Path -LiteralPath $archivePath) {
        throw "Refusing to overwrite existing archive '$archivePath'. Move it aside manually before rebuilding."
    }
    $publishExecutable = Join-Path $publishDirectory 'XTranslate.exe'
    $runningRelease = Get-Process XTranslate -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $publishExecutable }
    if ($runningRelease) { throw 'This release is running. Exit it from its tray menu before rebuilding; no process was stopped.' }
    $stagingRoot = Join-Path $projectRoot 'artifacts/.publish-staging'
    $stagingDirectory = Join-Path $stagingRoot ($folderName + '-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null
    $contained = if ($SelfContained) { 'true' } else { 'false' }
    # Publish into a fresh, isolated directory. This prevents old logs, screenshots,
    # smoke data, or user files from becoming accidental archive inputs.
    & $sdk publish src/XTranslate.App/XTranslate.App.csproj -c Release -r win-x64 --self-contained $contained -p:DebugType=None -p:DebugSymbols=false -o $stagingDirectory --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $stagingDirectory '使用说明.md')

    $forbiddenNames = '^(data|smoke-data|benchmark-data|verification)(-|$)'
    $forbidden = Get-ChildItem -LiteralPath $stagingDirectory -Recurse -Force |
        Where-Object { $_.Name -match $forbiddenNames }
    if ($forbidden) {
        $names = ($forbidden | ForEach-Object { $_.FullName }) -join ', '
        throw "Publish staging contains forbidden user/test data: $names"
    }

    # The archive source is exactly this new staging tree, including runtime
    # localization folders produced by dotnet publish. No pre-existing directory
    # or broad workspace glob participates in packaging.
    Compress-Archive -Path (Join-Path $stagingDirectory '*') -DestinationPath $archivePath -Force
    Move-Item -LiteralPath $stagingDirectory -Destination $publishDirectory
    Write-Host ('Ready: ' + $publishExecutable)
} finally { Pop-Location }
