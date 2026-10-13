# End-to-end in-place update with real published binaries, in a temporary folder.
# Installs portable X.Y.Z, builds X.Y.(Z+1), applies it headlessly, then checks version, data, cleanup,
# tampered digest and downgrade refusal. No network, no registry, no running user instance is touched.
param([string]$Output = (Join-Path $PSScriptRoot '..\..\artifacts\verification\update-e2e'))
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$sdk = Join-Path $root '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$project = Join-Path $root 'src/XTranslate.App/XTranslate.App.csproj'
$version = [string](([xml](Get-Content -Raw -LiteralPath $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
$parts = $version.Split('.'); $next = "$($parts[0]).$($parts[1]).$([int]$parts[2] + 1)"
$work = Join-Path ([IO.Path]::GetTempPath()) ('xtranslate-update-e2e-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$install = Join-Path $work 'XTranslate'
$checks = [Collections.Generic.List[string]]::new()
function Require([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
function Publish([string]$v, [string]$dir) {
    & $sdk publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=false `
        -p:IncludeNativeLibrariesForSelfExtract=false -p:UpdateFlavor=Portable "-p:Version=$v" -p:DebugType=None -p:DebugSymbols=false -o $dir --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Publish $v failed" }
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
    $success = $true
} catch { $failure = $_.Exception.Message; $success = $false }
finally {
    New-Item -ItemType Directory -Path $Output -Force | Out-Null
    [ordered]@{ success = $success; from = $version; to = $next; checks = $checks; error = $failure } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Output 'update-e2e.json')
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
$checks | ForEach-Object { Write-Host "PASS $_" }
if (-not $success) { Write-Host "FAIL $failure"; exit 1 }
