# Portable: self-contained single EXE plus WPF/SQLite native DLLs and licenses; no .NET install or registry writes.
# Lite: framework-dependent; includes translation/OCR and SQLite dependencies, no bundled .NET runtime.
param([ValidateSet('Portable', 'Lite')][string]$Flavor = 'Portable', [switch]$SkipTests)
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
    $folderName = "XTranslate-$version-win-x64-" + $Flavor.ToLowerInvariant()
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
    # Single-file compression was measured to roughly double private memory, and native
    # self-extraction would write to %TEMP%; both stay off so the folder is the whole footprint.
    $flavorArguments = if ($Flavor -eq 'Portable') {
        @('--self-contained', 'true', '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=false', '-p:IncludeNativeLibrariesForSelfExtract=false')
    } else { @('--self-contained', 'false') }
    # Publish into a fresh, isolated directory. This prevents old logs, screenshots,
    # smoke data, or user files from becoming accidental archive inputs.
    & $sdk publish src/XTranslate.App/XTranslate.App.csproj -c Release -r win-x64 @flavorArguments "-p:UpdateFlavor=$Flavor" -p:DebugType=None -p:DebugSymbols=false -o $stagingDirectory --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    # Chinese file name built from code points: Windows PowerShell 5.1 reads BOM-less scripts as ANSI.
    $readmeName = (-join [char[]](0x4F7F, 0x7528, 0x8BF4, 0x660E)) + '.md'
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $stagingDirectory $readmeName)

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
    # Sidecar digest for "Update from file" and for the release page (GitHub also shows its own digest).
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $archivePath).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($archivePath + '.sha256', "$hash  $(Split-Path -Leaf $archivePath)`n")
    Move-Item -LiteralPath $stagingDirectory -Destination $publishDirectory
    Write-Host ('Ready: ' + $publishExecutable)
} finally { Pop-Location }
