# End-to-end in-place update with real published binaries, in a temporary folder.
# Installs portable X.Y.Z, builds X.Y.(Z+1), applies it headlessly, then checks version, data, cleanup,
# tampered digest and downgrade refusal. No network, no registry, no running user instance is touched.
param([string]$Output = (Join-Path $PSScriptRoot '..\..\artifacts\verification\update-e2e'))
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$sdk = Join-Path $root '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_ROOT = Split-Path -Parent $sdk # Lite (framework-dependent) builds run on the SDK's runtime.
$project = Join-Path $root 'src/XTranslate.App/XTranslate.App.csproj'
$version = [string](([xml](Get-Content -Raw -LiteralPath $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
$parts = $version.Split('.'); $next = "$($parts[0]).$($parts[1]).$([int]$parts[2] + 1)"
# Spaces and CJK characters in the path catch quoting and encoding bugs.
$work = Join-Path ([IO.Path]::GetTempPath()) ('xtranslate update e2e ' + [string][char]0x6D4B + [string][char]0x8BD5 + ' ' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$install = Join-Path $work 'XTranslate'
$checks = [Collections.Generic.List[string]]::new()
function Require([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
function Publish([string]$v, [string]$dir, [string]$flavor = 'Portable') {
    $flavorArgs = if ($flavor -eq 'Portable') { @('--self-contained', 'true', '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=false', '-p:IncludeNativeLibrariesForSelfExtract=false') }
                  else { @('--self-contained', 'false') }
    & $sdk publish $project -c Release -r win-x64 @flavorArgs "-p:UpdateFlavor=$flavor" "-p:Version=$v" -p:DebugType=None -p:DebugSymbols=false -o $dir --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Publish $v $flavor failed" }
}
function Run([string]$exe, [string[]]$arguments) {
    $p = Start-Process -FilePath $exe -ArgumentList ($arguments | ForEach-Object { '"' + $_ + '"' }) -PassThru -Wait -WindowStyle Hidden
    return $p.ExitCode
}
try {
    New-Item -ItemType Directory -Path $work | Out-Null
    Publish $version $install
    $newDir = Join-Path $work 'new'; Publish $next $newDir
    New-Item -ItemType Directory -Path (Join-Path $install 'data') | Out-Null
    Set-Content -LiteralPath (Join-Path $install 'data/settings.json') -Value '{"marker":"user-data"}' -NoNewline
    $zip = Join-Path $work "XTranslate-$next-win-x64-portable.zip"
    Compress-Archive -Path (Join-Path $newDir '*') -DestinationPath $zip
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash.ToLowerInvariant()
    $exe = Join-Path $install 'XTranslate.exe'

    Set-Content -LiteralPath "$zip.sha256" -Value ('0' * 64) -NoNewline
    Require ((Run $exe @('--apply-update', $zip, "$work\tampered.json")) -eq 1) 'Tampered digest was accepted'
    Require ((Get-Content -Raw "$work\tampered.json" | ConvertFrom-Json).code -eq 'Integrity') 'Tampered digest gave the wrong error'
    Require ((Get-Item $exe).VersionInfo.ProductVersion -like "$version*") 'Rejected update changed the program'
    $checks.Add('A package whose SHA-256 does not match is refused and nothing changes')

    Set-Content -LiteralPath "$zip.sha256" -Value "$hash  $(Split-Path -Leaf $zip)" -NoNewline
    Require ((Run $exe @('--apply-update', $zip, "$work\apply.json")) -eq 0) "Update failed: $(Get-Content -Raw "$work\apply.json")"
    Require ((Get-Item $exe).VersionInfo.ProductVersion -like "$next*") 'Program version did not change'
    Require ((Get-Content -Raw (Join-Path $install 'data/settings.json')) -eq '{"marker":"user-data"}') 'data/ was modified'
    Require (@(Get-ChildItem $install -Filter '*.old').Count -gt 0) 'In-place replacement did not use the rename path'
    $checks.Add("The running $version EXE replaced itself with $next in place; data/ was untouched")

    Require ((Run $exe @('--cleanup-only')) -eq 0) 'Cleanup run failed'
    Require (@(Get-ChildItem $install -Filter '*.old').Count -eq 0 -and @(Get-ChildItem $install -Directory -Filter '.update-*').Count -eq 0) 'Leftovers were not cleaned'
    $checks.Add('The next start removed every *.old file and staging folder')

    $old = Join-Path $work "XTranslate-$version-win-x64-portable.zip"
    Compress-Archive -Path (Join-Path $install '*.dll'), $exe -DestinationPath $old
    Require ((Run $exe @('--apply-update', $old, "$work\downgrade.json")) -eq 1) 'Downgrade was accepted'
    Require ((Get-Content -Raw "$work\downgrade.json" | ConvertFrom-Json).code -eq 'NotNewer') 'Downgrade gave the wrong error'
    $checks.Add('An older or equal package is refused')

    # Lite edition: a normal upgrade, and a package that needs a runtime this PC lacks.
    $lite = Join-Path $work 'Lite'; Publish $version $lite 'Lite'
    $liteNew = Join-Path $work 'lite-new'; Publish $next $liteNew 'Lite'
    $liteExe = Join-Path $lite 'XTranslate.exe'
    $future = Join-Path $work 'lite-future'; Copy-Item -LiteralPath $liteNew -Destination $future -Recurse
    $config = Join-Path $future 'XTranslate.runtimeconfig.json'
    (Get-Content -Raw $config) -replace '"version": "10\.0\.0"', '"version": "99.0.0"' | Set-Content -LiteralPath $config -NoNewline
    $futureZip = Join-Path $work "future-lite.zip"; Compress-Archive -Path (Join-Path $future '*') -DestinationPath $futureZip
    Require ((Run $liteExe @('--apply-update', $futureZip, "$work\runtime.json")) -eq 1) 'A package needing a missing runtime was accepted'
    Require ((Get-Content -Raw "$work\runtime.json" | ConvertFrom-Json).code -eq 'RuntimeMissing') 'Missing runtime gave the wrong error'
    Require ((Get-Item $liteExe).VersionInfo.ProductVersion -like "$version*") 'Refused Lite update changed the program'
    $checks.Add('Lite: a package that needs a .NET runtime this PC lacks is refused before any change')
    $liteZip = Join-Path $work "XTranslate-$next-win-x64-lite.zip"; Compress-Archive -Path (Join-Path $liteNew '*') -DestinationPath $liteZip
    Require ((Run $liteExe @('--apply-update', $liteZip, "$work\lite.json")) -eq 0) "Lite update failed: $(Get-Content -Raw "$work\lite.json")"
    Require ((Get-Item $liteExe).VersionInfo.ProductVersion -like "$next*") 'Lite version did not change'
    Require ((Run $liteExe @('--cleanup-only')) -eq 0 -and @(Get-ChildItem $lite -Filter '*.old').Count -eq 0) 'Lite cleanup failed'
    $checks.Add("Lite: $version replaced itself with $next (runtime check passed) and cleaned up")
    $success = $true
} catch { $failure = $_.Exception.Message; $success = $false }
finally {
    New-Item -ItemType Directory -Path $Output -Force | Out-Null
    [ordered]@{ success = $success; from = $version; to = $next; checks = $checks; error = $failure } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Output 'update-e2e.json')
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
$checks | ForEach-Object { Write-Host "PASS $_" }
if (-not $success) { Write-Host "FAIL $failure"; exit 1 }
