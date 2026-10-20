# Real foreground update/restart using only UI Automation on this runner's temporary copy.
# Run manually on an interactive Windows desktop. No network, clipboard, key injection or registry writes.
# Refuses to run beside another normal XTranslate instance; cleanup targets only its own install path.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$FromPackage,
    [Parameter(Mandatory)][string]$ToPackage,
    [Parameter(Mandatory)][string]$Output,
    [ValidateSet('Settings', 'Banner')][string]$RestartVia = 'Settings',
    [ValidateRange(10, 120)][int]$StepTimeoutSeconds = 30
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Security
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class RestartAcceptanceNative {
    private delegate bool EnumWindowProc(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int size);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    public static IntPtr[] Windows(int pid, bool dialogsOnly) {
        var result = new List<IntPtr>();
        EnumWindows((window, data) => {
            uint owner;
            GetWindowThreadProcessId(window, out owner);
            if (owner == pid && IsWindowVisible(window)) {
                var name = new StringBuilder(256);
                GetClassName(window, name, name.Capacity);
                if (!dialogsOnly || name.ToString() == "#32770") result.Add(window);
            }
            return true;
        }, IntPtr.Zero);
        return result.ToArray();
    }
    public static void Exit(int pid) {
        foreach (var window in Windows(pid, false))
            PostMessage(window, RegisterWindowMessage("XTranslate.Exit.v01"), IntPtr.Zero, IntPtr.Zero);
    }
}
'@
function Require([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
function Wait-For([scriptblock]$probe, [string]$description) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        $value = & $probe
        if ($value) { return $value }
        Start-Sleep -Milliseconds 100
    } while ($watch.Elapsed.TotalSeconds -lt $StepTimeoutSeconds)
    throw "Timed out waiting for $description"
}
function Find-Control($window, [string]$id) {
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}
function Owned-Window([int]$processId, [string]$controlId) {
    foreach ($handle in [RestartAcceptanceNative]::Windows($processId, $false)) {
        $window = [Windows.Automation.AutomationElement]::FromHandle($handle)
        if (Find-Control $window $controlId) { return $window }
    }
    return $null
}
function Invoke-Control($window, [string]$id) {
    $control = Find-Control $window $id
    Require ($null -ne $control -and $control.Current.IsEnabled) "Missing or disabled control: $id"
    ([Windows.Automation.InvokePattern]$control.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)).Invoke()
}
function Open-Settings([int]$processId, $main) {
    Invoke-Control $main 'SettingsButton'
    $settings = Wait-For { Owned-Window $processId 'SettingsSections' } 'the owned Settings window'
    $about = Find-Control $settings 'SettingsTabAbout'
    ([Windows.Automation.SelectionItemPattern]$about.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    return $settings
}
function Package-Info([string]$path) {
    $path = [IO.Path]::GetFullPath($path)
    Require (Test-Path -LiteralPath $path -PathType Leaf) "Package not found: $path"
    Require ((Split-Path -Leaf $path) -match '^XTranslate-(\d+\.\d+\.\d+)-win-x64-(portable|lite)\.zip$') 'Invalid package name'
    $version = $Matches[1]; $flavor = $Matches[2]
    Require (Test-Path -LiteralPath "$path.sha256") "Checksum missing: $path.sha256"
    $expected = (Get-Content -Raw -LiteralPath "$path.sha256").Trim().Split(' ')[0]
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    Require ($expected -match '^[0-9a-fA-F]{64}$' -and $expected -eq $hash) "Checksum mismatch: $path"
    return [pscustomobject]@{ path = $path; version = $version; flavor = $flavor; sha256 = $hash }
}
$from = Package-Info $FromPackage
$to = Package-Info $ToPackage
Require ($from.flavor -eq $to.flavor -and [version]$to.version -gt [version]$from.version) 'Expected a newer package of the same edition'
$normalMutex = $null
if ([Threading.Mutex]::TryOpenExisting('Local\XTranslate.v01', [ref]$normalMutex)) {
    $normalMutex.Dispose()
    throw 'A normal XTranslate instance exists; no process was started or stopped.'
}
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$env:DOTNET_ROOT = Join-Path $root '.tools\dotnet'
$session = [IO.Path]::GetFullPath($Output)
Require (-not (Test-Path -LiteralPath $session)) 'Use a new report directory for each invocation'
New-Item -ItemType Directory -Path $session | Out-Null
$work = Join-Path ([IO.Path]::GetTempPath()) ('xtranslate restart ' + [char]0x6D4B + [char]0x8BD5 + ' ' + [Guid]::NewGuid().ToString('N'))
$install = Join-Path $work 'app'
$exe = Join-Path $install 'XTranslate.exe'
$checks = [Collections.Generic.List[string]]::new()
$parent = $null; $replacement = $null; $success = $false; $failure = $null
$beforeForeground = $false; $afterForeground = $false
try {
    Expand-Archive -LiteralPath $from.path -DestinationPath $install
    # This fixture deliberately has no automatic activity and contains only a fake DPAPI key.
    $secret = 'restart-fixture-not-a-real-key'
    $encrypted = [Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect(
        [Text.Encoding]::UTF8.GetBytes($secret), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser))
    $modern = [version]$from.version -ge [version]'0.4.0'
    $seed = [ordered]@{
        UiLanguage = 2; CaptureHotkey = 'Ctrl+Alt+Shift+F11'; AutoTranslateSelection = $false
        AllowCopyCapture = $false; AutoCheckUpdates = $false; AutoFallback = $false
        Translation = [ordered]@{ Provider = 0; TargetLanguage = 'es' }
        AiServices = @([ordered]@{ Id = 'restart-fixture'; Name = 'Offline restart fixture'; Protocol = 0;
            Endpoint = 'https://restart.example.test/v1'; Model = 'fixture-model'; Streaming = $false })
        ActiveAiServiceId = 'restart-fixture'; AiSecrets = @{ 'restart-fixture' = $encrypted }
    }
    if ($modern) { $seed.Theme = 2; $seed.CaptureHotkeyEnabled = $false }
    New-Item -ItemType Directory -Path (Join-Path $install 'data') | Out-Null
    $settingsPath = Join-Path $install 'data/settings.json'
    $seed | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $settingsPath -Encoding UTF8
    $dataHash = (Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash
    $parent = Start-Process -FilePath $exe -WorkingDirectory $install -PassThru -WindowStyle Normal
    $main = Wait-For { Owned-Window $parent.Id 'SettingsButton' } 'the original main window'
    Require ((Get-Item -LiteralPath $exe).VersionInfo.ProductVersion -like "$($from.version)*") 'Original package version mismatch'
    $settings = Open-Settings $parent.Id $main
    $versionText = (Find-Control $settings 'SettingsVersion').Current.Name
    Require ($versionText.Contains($from.version)) 'About does not show the original running version'
    Invoke-Control $settings 'SettingsUpdateFromFile'
    $picker = Wait-For {
        $handles = [RestartAcceptanceNative]::Windows($parent.Id, $true)
        if ($handles.Count -eq 1) { [Windows.Automation.AutomationElement]::FromHandle($handles[0]) }
    } 'the owned file picker'
    # Look up only standard file-name controls. Never read the picker file list or its current value.
    $editor = Find-Control $picker '1001'
    if ($null -eq $editor) {
        $hostControl = Find-Control $picker 'FileNameControlHost'
        if ($null -eq $hostControl) { $hostControl = Find-Control $picker '1148' }
        if ($null -ne $hostControl) {
            $editor = $hostControl.FindFirst([Windows.Automation.TreeScope]::Descendants,
                [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Edit))
        }
    }
    Require ($null -ne $editor) 'File picker filename edit was not found'
    ([Windows.Automation.ValuePattern]$editor.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).SetValue($to.path)
    Invoke-Control $picker '1'
    Wait-For { (Find-Control $settings 'SettingsUpdateStatus').Current.Name -eq 'The new version is ready and takes effect after a restart.' } 'the verified update-ready status' | Out-Null
    Require (@(Get-ChildItem -LiteralPath $install -Filter '*.old' -File).Count -gt 0) 'Update did not use in-place replacement'
    Require ((Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash -eq $dataHash) 'Applying the package modified data'
    $checks.Add('The actual From file dialog applied the verified package in place without changing data')
    if ($RestartVia -eq 'Banner') {
        Invoke-Control $settings 'SettingsCancel'
        Wait-For { $control = Find-Control $main 'InfoBarAction'; $control -and -not $control.Current.IsOffscreen } 'the main-window restart banner' | Out-Null
        $restartWindow = $main; $restartControl = 'InfoBarAction'
    } else { $restartWindow = $settings; $restartControl = 'SettingsInstallUpdate' }
    $handle = [IntPtr]$restartWindow.Current.NativeWindowHandle
    [RestartAcceptanceNative]::SetForegroundWindow($handle) | Out-Null
    $beforeForeground = [RestartAcceptanceNative]::GetForegroundWindow() -eq $handle
    Require $beforeForeground 'Windows did not grant foreground to the owned fixture; no foreground restart claim is possible'
    Invoke-Control $restartWindow $restartControl
    Require ($parent.WaitForExit($StepTimeoutSeconds * 1000) -and $parent.ExitCode -eq 0) 'Original process did not exit gracefully'
    $replacement = Wait-For {
        Get-Process XTranslate -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $parent.Id -and $_.Path -eq $exe } | Select-Object -First 1
    } 'the replacement process in the owned install directory'
    $newMain = Wait-For { Owned-Window $replacement.Id 'SettingsButton' } 'the replacement main window'
    $afterForeground = [RestartAcceptanceNative]::GetForegroundWindow() -eq [IntPtr]$newMain.Current.NativeWindowHandle
    Require $afterForeground 'The new process was visible but did not own the foreground'
    Require (@(Get-ChildItem -LiteralPath $install -Filter '*.old' -File).Count -eq 0 -and
        @(Get-ChildItem -LiteralPath $install -Filter '.update-*' -Directory).Count -eq 0) 'Replacement startup did not clean old files'
    $checks.Add("$RestartVia restart exited the old PID, started a different foreground PID and cleaned the update leftovers")
    $newSettings = Open-Settings $replacement.Id $newMain
    Require ((Find-Control $newSettings 'SettingsVersion').Current.Name.Contains($to.version)) 'Replacement About does not report the new running version'
    Invoke-Control $newSettings 'SettingsCancel'
    $saved = Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json
    $decoded = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect(
        [Convert]::FromBase64String($saved.AiSecrets.'restart-fixture'), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser))
    Require ($saved.UiLanguage -eq 2 -and $saved.CaptureHotkey -eq 'Ctrl+Alt+Shift+F11' -and $saved.Translation.TargetLanguage -eq 'es' -and
        $saved.ActiveAiServiceId -eq 'restart-fixture' -and $decoded -eq $secret -and -not $saved.AutoCheckUpdates -and -not $saved.AutoTranslateSelection -and
        -not (Get-Content -Raw -LiteralPath $settingsPath).Contains($secret)) 'Restart lost preferences or encrypted credentials, or enabled automatic activity'
    if ($modern) { Require ($saved.Theme -eq 2 -and $saved.CaptureHotkeyEnabled -eq $false) 'Restart lost the appearance/shortcut preferences' }
    if ($RestartVia -eq 'Banner') { Require ((Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash -eq $dataHash) 'Cancel/banner restart rewrote settings' }
    $checks.Add('The new running version retained UI/target language, service, shortcut and decryptable credentials without plaintext or automatic activity')
    [RestartAcceptanceNative]::Exit($replacement.Id)
    Require ($replacement.WaitForExit($StepTimeoutSeconds * 1000) -and $replacement.ExitCode -eq 0) 'Replacement did not exit through its normal exit path'
    $success = $true
} catch { $failure = $_.Exception.ToString() }
finally {
    # No broadcast exit, broad Stop-Process, user-directory cleanup, or unowned process termination.
    $cleanupErrors = @()
    foreach ($owned in @(Get-Process XTranslate -ErrorAction SilentlyContinue | Where-Object Path -eq $exe)) {
        try {
            try {
                $dialog = Owned-Window $owned.Id 'SettingsCancel'
                if ($null -ne $dialog) { Invoke-Control $dialog 'SettingsCancel' }
                [RestartAcceptanceNative]::Exit($owned.Id)
            } catch { $cleanupErrors += $_.Exception.Message }
            if (-not $owned.WaitForExit(5000)) { $owned.Kill(); $owned.WaitForExit(5000) | Out-Null }
        } catch { $cleanupErrors += $_.Exception.Message }
        finally { $owned.Dispose() }
    }
    if ($cleanupErrors.Count -gt 0) { $success = $false }
    [ordered]@{ success = $success; from = $from; to = $to; restartVia = $RestartVia;
        originalProcessId = if ($null -ne $parent) { $parent.Id } else { $null };
        replacementProcessId = if ($null -ne $replacement) { $replacement.Id } else { $null };
        foregroundBeforeRestart = $beforeForeground; foregroundAfterRestart = $afterForeground;
        checks = $checks; error = $failure; cleanupErrors = $cleanupErrors; network = 'none'; clipboard = $false; userData = $false } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $session 'restart-e2e.json') -Encoding UTF8
    if ($null -ne $parent) { $parent.Dispose() }
    if ($null -ne $replacement) { $replacement.Dispose() }
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
$checks | ForEach-Object { Write-Host "PASS $_" }
if (-not $success) { Write-Host "FAIL $failure"; exit 1 }
