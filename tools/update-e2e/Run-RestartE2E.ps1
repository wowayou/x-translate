# Real foreground update/restart on this runner's temporary copy.
# WPF uses UI Automation; the standard file picker receives bounded messages to its owned Edit/Open HWNDs.
# No network, clipboard, key injection or program registry writes. Cleanup targets only the owned install path.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$FromPackage,
    [Parameter(Mandatory)][string]$ToPackage,
    [Parameter(Mandatory)][string]$Output,
    [ValidateSet('Settings', 'Banner')][string]$RestartVia = 'Settings',
    [string]$DataFixtureDirectory,
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
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr dialog, int id);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowProc callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, string text, uint flags, uint timeout, out IntPtr result);
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
    public static bool SelectPackage(IntPtr dialog, int pid, string path) {
        uint owner;
        GetWindowThreadProcessId(dialog, out owner);
        if (owner != pid || !IsWindowVisible(dialog)) throw new InvalidOperationException("File picker is not owned by the fixture.");
        var edit = GetDlgItem(dialog, 1001);
        var kind = new StringBuilder(256);
        if (edit != IntPtr.Zero) GetClassName(edit, kind, kind.Capacity);
        if (kind.ToString() != "Edit") {
            edit = IntPtr.Zero;
            var host = GetDlgItem(dialog, 1148);
            if (host != IntPtr.Zero) {
                int examined = 0;
                EnumChildWindows(host, (child, data) => {
                    if (++examined > 128) return false;
                    var name = new StringBuilder(256);
                    GetClassName(child, name, name.Capacity);
                    if (name.ToString() == "Edit") { edit = child; return false; }
                    return true;
                }, IntPtr.Zero);
            }
        }
        var open = GetDlgItem(dialog, 1);
        if (edit == IntPtr.Zero || open == IntPtr.Zero || !IsWindowEnabled(edit) || !IsWindowEnabled(open)) return false;
        GetWindowThreadProcessId(edit, out owner);
        if (owner != pid) throw new InvalidOperationException("Filename edit is not owned by the fixture.");
        IntPtr result;
        if (SendMessageTimeout(edit, 0x000c, IntPtr.Zero, path, 0x0002, 2000, out result) == IntPtr.Zero)
            throw new InvalidOperationException("Owned filename edit did not respond.");
        if (!PostMessage(open, 0x00f5, IntPtr.Zero, IntPtr.Zero))
            throw new InvalidOperationException("Owned Open button could not be invoked.");
        return true;
    }
    public static void CancelDialog(IntPtr dialog) {
        var cancel = GetDlgItem(dialog, 2);
        if (cancel != IntPtr.Zero) PostMessage(cancel, 0x00f5, IntPtr.Zero, IntPtr.Zero);
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
    $control = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
    # The released 0.3.0 Cancel button has only its WPF x:Name, not the later explicit AutomationId.
    if ($null -eq $control -and $id -eq 'SettingsCancel') {
        $control = $window.FindFirst([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, 'CancelButton'))
    }
    return $control
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
$dataSnapshots = [Collections.Generic.List[object]]::new()
. (Join-Path $PSScriptRoot 'UpdateData.ps1')
$fixtureFiles = @()
if ($DataFixtureDirectory) {
    $DataFixtureDirectory = [IO.Path]::GetFullPath($DataFixtureDirectory)
    $fixtureFiles = @(ReadDataFixtureFiles $DataFixtureDirectory)
}
function Snapshot-Data([string]$phase) {
    $identity = DataHashes $install
    $rows = ConvertFrom-Json -InputObject $identity
    $dataSnapshots.Add([ordered]@{ phase = $phase; files = @($rows) })
    return $identity
}
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
    AddDataFixture $install $DataFixtureDirectory $fixtureFiles
    $dataBefore = Snapshot-Data 'before-start'
    $parent = Start-Process -FilePath $exe -WorkingDirectory $install -PassThru -WindowStyle Normal
    $null = $parent.Handle # Keep the process handle so fast graceful exits retain their exit code.
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
    # PowerShell's managed UIA client can expose native Edit/Button HWNDs as generic panes.
    # Use only the standard filename/Open children of this PID's picker; never read values or file lists.
    Wait-For { [RestartAcceptanceNative]::SelectPackage([IntPtr]$picker.Current.NativeWindowHandle, $parent.Id, $to.path) } 'the owned filename edit and Open button' | Out-Null
    Wait-For { (Find-Control $settings 'SettingsUpdateStatus').Current.Name -eq 'The new version is ready and takes effect after a restart.' } 'the verified update-ready status' | Out-Null
    Require (@(Get-ChildItem -LiteralPath $install -Filter '*.old' -File).Count -gt 0) 'Update did not use in-place replacement'
    Require ((Snapshot-Data 'update-ready') -eq $dataBefore) 'Applying the package modified a data file'
    $checks.Add('The actual From file dialog accepted the verified package without changing any hidden/nested data file')
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
    $null = $replacement.Handle
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
    $dataAfter = Snapshot-Data 'after-restart'
    AssertRestartData $dataBefore $dataAfter ($RestartVia -eq 'Settings')
    $checks.Add('Restart preserves all non-settings data; the banner also preserves settings bytes, while Settings saves retain semantic preferences/credentials')
    [RestartAcceptanceNative]::Exit($replacement.Id)
    Require ($replacement.WaitForExit($StepTimeoutSeconds * 1000) -and $replacement.ExitCode -eq 0) 'Replacement did not exit through its normal exit path'
    $success = $true
} catch { $failure = $_.Exception.ToString() + "`n" + $_.ScriptStackTrace }
finally {
    # No broadcast exit, broad Stop-Process, user-directory cleanup, or unowned process termination.
    $cleanupErrors = @()
    foreach ($owned in @(Get-Process XTranslate -ErrorAction SilentlyContinue | Where-Object Path -eq $exe)) {
        try {
            try {
                foreach ($dialogHandle in [RestartAcceptanceNative]::Windows($owned.Id, $true)) { [RestartAcceptanceNative]::CancelDialog($dialogHandle) }
                $dialog = Owned-Window $owned.Id 'SettingsSections'
                if ($null -ne $dialog) { Invoke-Control $dialog 'SettingsCancel' }
                [RestartAcceptanceNative]::Exit($owned.Id)
            } catch { $cleanupErrors += $_.Exception.Message }
            if (-not $owned.WaitForExit(5000)) {
                $cleanupErrors += 'Owned fixture required forced termination; graceful cleanup did not pass'
                $owned.Kill(); $owned.WaitForExit(5000) | Out-Null
            }
        } catch { $cleanupErrors += $_.Exception.Message }
        finally { $owned.Dispose() }
    }
    if ($cleanupErrors.Count -gt 0) { $success = $false }
    [ordered]@{ success = $success; from = $from; to = $to; restartVia = $RestartVia;
        originalProcessId = if ($null -ne $parent) { $parent.Id } else { $null };
        replacementProcessId = if ($null -ne $replacement) { $replacement.Id } else { $null };
        foregroundBeforeRestart = $beforeForeground; foregroundAfterRestart = $afterForeground;
        dataFixtureFileCount = $fixtureFiles.Count; dataSnapshots = $dataSnapshots;
        checks = $checks; error = $failure; cleanupErrors = $cleanupErrors; network = 'none'; clipboard = $false; userData = $false } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $session 'restart-e2e.json') -Encoding UTF8
    if ($null -ne $parent) { $parent.Dispose() }
    if ($null -ne $replacement) { $replacement.Dispose() }
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
$checks | ForEach-Object { Write-Host "PASS $_" }
if (-not $success) { Write-Host "FAIL $failure"; exit 1 }
exit 0
