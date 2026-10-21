#requires -Version 5.1
param([Parameter(Mandatory = $true)][string]$OutputDirectory)
# Capability experiment only, NOT the .NET 10 app adapter. Windows PowerShell provides the WinRT projection.
# Generates its own fixed image; never captures screen/clipboard, opens user images, downloads packs or calls HTTP.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Run this local probe with Windows PowerShell 5.1, not PowerShell Core.' }
$root = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $root) { throw 'The evidence directory must be new; existing evidence is never overwritten.' }
[IO.Directory]::CreateDirectory($root) | Out-Null
$clock = [Diagnostics.Stopwatch]::StartNew()
$checks = New-Object 'System.Collections.Generic.List[string]'
$runs = New-Object 'System.Collections.Generic.List[object]'
$report = [ordered]@{ success = $false; scope = 'fixed synthetic image only; no screen, clipboard or network';
    host = [Environment]::OSVersion.VersionString; powershell = $PSVersionTable.PSVersion.ToString();
    language = 'en-US'; maximumRecognitions = 2; deadlineSeconds = 30; checks = $checks; runs = $runs }
$bitmap = $null; $stream = $null
try {
    Add-Type -AssemblyName System.Drawing
    Add-Type -AssemblyName System.Runtime.WindowsRuntime
    [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime] | Out-Null
    [Windows.Media.Ocr.OcrResult, Windows.Foundation, ContentType = WindowsRuntime] | Out-Null
    [Windows.Globalization.Language, Windows.Globalization, ContentType = WindowsRuntime] | Out-Null
    [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime] | Out-Null
    [Windows.Storage.FileAccessMode, Windows.Storage, ContentType = WindowsRuntime] | Out-Null
    [Windows.Storage.Streams.IRandomAccessStream, Windows.Storage.Streams, ContentType = WindowsRuntime] | Out-Null
    [Windows.Graphics.Imaging.BitmapDecoder, Windows.Foundation, ContentType = WindowsRuntime] | Out-Null
    [Windows.Graphics.Imaging.SoftwareBitmap, Windows.Foundation, ContentType = WindowsRuntime] | Out-Null
    $asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetGenericArguments().Count -eq 1 -and
        $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
    } | Select-Object -First 1
    if ($null -eq $asTask) { throw 'The local WinRT async projection is unavailable.' }
    function Complete-Operation($Operation, [Type]$ResultType) {
        $remaining = 30000 - $clock.ElapsedMilliseconds
        if ($remaining -le 0) { throw 'The 30 second experiment deadline was exceeded.' }
        $task = $asTask.MakeGenericMethod($ResultType).Invoke($null, @($Operation))
        if (-not $task.Wait([int][Math]::Min(5000, $remaining))) {
            try { $Operation.Cancel(); $report.timeoutCancellationRequested = $true }
            catch { $report.timeoutCancellationRequested = $false; $report.timeoutCancellationErrorType = $_.Exception.GetType().FullName }
            # A cancellation request is not proof that the underlying engine has stopped.
            throw 'A local WinRT operation exceeded its bounded wait; recognition was not retried.'
        }
        return ,($task.GetAwaiter().GetResult())
    }
    $report.availableLanguages = @([Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages | ForEach-Object { $_.LanguageTag })
    $report.maxImageDimension = [Windows.Media.Ocr.OcrEngine]::MaxImageDimension
    $language = [Windows.Globalization.Language]::new('en-US')
    if (-not [Windows.Media.Ocr.OcrEngine]::IsLanguageSupported($language)) { throw 'en-US is not installed; no download or language fallback was attempted.' }
    $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($language)
    if ($null -eq $engine -or $engine.RecognizerLanguage.LanguageTag -ne 'en-US') { throw 'Explicit local OCR language could not be selected.' }
    $checks.Add('Explicit installed en-US engine selected; no profile-language fallback')
    $missing = [Windows.Globalization.Language]::new('zxx') # BCP 47: no linguistic content; not an OCR language.
    if ([Windows.Media.Ocr.OcrEngine]::IsLanguageSupported($missing) -or $null -ne [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($missing)) {
        throw 'Unsupported language unexpectedly acquired an engine.'
    }
    $checks.Add('Unsupported language reports unavailable, rather than silently switching language')

    $expected = 'HELLO WORLD 123'
    $imagePath = Join-Path $root 'fixed-en.png'
    $image = [Drawing.Bitmap]::new(960, 160)
    $graphics = $null; $font = $null
    try {
        $image.SetResolution(96, 96)
        $graphics = [Drawing.Graphics]::FromImage($image)
        $font = [Drawing.Font]::new('Segoe UI', 42, [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
        $graphics.Clear([Drawing.Color]::White)
        $graphics.DrawString($expected, $font, [Drawing.Brushes]::Black, [single]30, [single]45)
        $image.Save($imagePath, [Drawing.Imaging.ImageFormat]::Png)
    } finally {
        if ($null -ne $font) { $font.Dispose() }; if ($null -ne $graphics) { $graphics.Dispose() }; $image.Dispose()
    }
    if ((Get-Item -LiteralPath $imagePath).Length -gt 1MB) { throw 'Generated fixture exceeded its 1 MiB cap.' }
    $hasher = [Security.Cryptography.SHA256]::Create(); $inputFile = [IO.File]::OpenRead($imagePath)
    try { $report.fixtureSha256 = [BitConverter]::ToString($hasher.ComputeHash($inputFile)).Replace('-', '').ToLowerInvariant() }
    finally { $inputFile.Dispose(); $hasher.Dispose() }
    $file = Complete-Operation ([Windows.Storage.StorageFile]::GetFileFromPathAsync($imagePath)) ([Windows.Storage.StorageFile])
    $stream = Complete-Operation ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
    $decoder = Complete-Operation ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
    $bitmap = Complete-Operation ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
    if ($bitmap.PixelWidth -gt $report.maxImageDimension -or $bitmap.PixelHeight -gt $report.maxImageDimension) { throw 'Fixture exceeds the reported engine dimensions.' }
    for ($i = 1; $i -le 2; $i++) {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $result = Complete-Operation ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
        $text = $result.Text.Trim()
        $runs.Add([pscustomobject]@{ iteration = $i; text = $text; elapsedMs = $watch.Elapsed.TotalMilliseconds; lines = $result.Lines.Count })
        if ($text -cne $expected) { throw 'Fixed-image OCR did not match the finite expected output.' }
    }
    $checks.Add('Two bounded recognitions of the same synthetic image match the expected text exactly')
    $report.success = $true
} catch {
    $report.errorType = $_.Exception.GetType().FullName
    $report.error = $_.Exception.Message
    throw
} finally {
    $cleanupErrors = New-Object 'System.Collections.Generic.List[string]'
    foreach ($owned in @($bitmap, $stream)) {
        if ($null -ne $owned) {
            try { $owned.Dispose() } catch { $cleanupErrors.Add($_.Exception.GetType().FullName) }
        }
    }
    if ($cleanupErrors.Count -gt 0) { $report.success = $false; $report.cleanupErrorTypes = $cleanupErrors }
    $report.elapsedMs = $clock.Elapsed.TotalMilliseconds
    $report | ConvertTo-Json -Depth 6 | ForEach-Object {
        [IO.File]::WriteAllText((Join-Path $root 'ocr-probe.json'), $_, [Text.UTF8Encoding]::new($false))
    }
}
if (-not $report.success) { throw 'The probe did not complete successfully; inspect its report, including cleanup errors.' }
Write-Output (Join-Path $root 'ocr-probe.json')
