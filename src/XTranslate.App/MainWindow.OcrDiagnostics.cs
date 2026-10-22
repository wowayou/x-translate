using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using XTranslate.App.Ocr;
using XTranslate.Core;
using XTranslate.Ocr.Windows;
using Drawing = System.Drawing;

namespace XTranslate.App;

public partial class MainWindow
{
    // Opt-in diagnostic lane only: real Windows OCR of generated pixels and the injected fixed HTTP
    // handler. Off-screen comparison windows, never a screen/file/clipboard read or a real request.
    private async Task<object> RunOcrDiagnosticsBatchAsync(Func<int> requests)
    {
        const int width = 960, height = 160, windows = 3;
        var pixels = CreateOcrDiagnosticPixels(width, height);
        var hash = Convert.ToHexString(SHA256.HashData(pixels));
        var before = requests();
        var engine = new DiagnosticOcrEngine();
        var watch = Stopwatch.StartNew();
        try
        {
            for (var iteration = 0; iteration < windows; iteration++)
            {
                OcrImage? admitted = null;
                var view = new OcrWindow(engine, _ => true,
                    capture: _ =>
                    {
                        var copied = pixels.ToArray();
                        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, copied, width * 4);
                        bitmap.Freeze();
                        admitted = new OcrImage(width, height, bitmap, copied);
                        return Task.FromResult<OcrImage?>(admitted);
                    },
                    translate: (text, settings, token) => _service.TranslateAsync(text, settings, token), settings: _settings)
                {
                    Owner = this, ShowActivated = false, ShowInTaskbar = false,
                    WindowStartupLocation = System.Windows.WindowStartupLocation.Manual, Left = -10000, Top = -10000
                };
                try
                {
                    view.Show();
                    await Dispatcher.Yield(DispatcherPriority.Loaded);
                    await view.Loading.WaitAsync(TimeSpan.FromSeconds(15));
                    var loading = view.Loading;
                    var english = view.LanguageBox.Items.Cast<LocalOcrLanguage>().FirstOrDefault(language => language.Tag == "en-US")
                        ?? throw new LocalOcrException(LocalOcrError.UnsupportedLanguage);
                    view.LanguageBox.SelectedItem = english;
                    view.Hide(); view.Show();
                    await Dispatcher.InvokeAsync(view.UpdateLayout, DispatcherPriority.Render);
                    CheckOcrDiagnostic(ReferenceEquals(loading, view.Loading), "Opening OCR repeated initialization");
                    CheckOcrDiagnostic(await view.CaptureAsync().WaitAsync(TimeSpan.FromSeconds(15)), "Generated OCR capture did not complete");
                    CheckOcrDiagnostic(view.TextBox.Text == "HELLO WORLD 123" && view.ResultBox.Text == "本地诊断译文。" &&
                        view.ImagePreview.Source is not null && requests() == before + iteration * 2 + 1, "Generated OCR comparison or request count differs");
                    CheckOcrDiagnostic(admitted is not null && admitted.Pixels.AsSpan().SequenceEqual(pixels), "Recognition modified the admitted raw image");
                    CheckOcrDiagnostic(await view.RecognizeAsync().WaitAsync(TimeSpan.FromSeconds(15)) &&
                        view.TextBox.Text == "HELLO WORLD 123" && view.ResultBox.Text.Length == 0 && requests() == before + iteration * 2 + 1,
                        "Local reread kept a stale translation or sent another request");
                    CheckOcrDiagnostic(await view.TranslateAsync().WaitAsync(TimeSpan.FromSeconds(5)) &&
                        view.ResultBox.Text == "本地诊断译文。" && requests() == before + (iteration + 1) * 2, "Explicit OCR retranslation differs");
                }
                finally { view.Close(); }
                CheckOcrDiagnostic(admitted is not null && admitted.Pixels.All(value => value == 0) &&
                    view.ImagePreview.Source is null && view.TextBox.Text.Length == 0 && view.ResultBox.Text.Length == 0,
                    "Closed OCR retained its pixels/text/preview");
            }
            CheckOcrDiagnostic(engine.LanguageEnumerations == windows && engine.Recognitions == windows * 2 &&
                hash == Convert.ToHexString(SHA256.HashData(pixels)), "OCR workload changed counts or its generated source");
            Hide();
            return new
            {
                mode = "real installed en-US OCR; fixed generated BGRA; off-screen production comparison",
                windows, languageEnumerations = engine.LanguageEnumerations, recognitions = engine.Recognitions,
                mockTranslations = requests() - before, width, height, pixelSha256 = hash,
                exactTextAndTranslation = true, callerPixelsUnchanged = true, closedPixelsCleared = true,
                recognitionMinMs = engine.Durations.Min(), recognitionMaxMs = engine.Durations.Max(), elapsedMs = Math.Round(watch.Elapsed.TotalMilliseconds, 3),
                screenPixelsRead = false, filesOpened = false, clipboard = false, retries = 0
            };
        }
        finally { Array.Clear(pixels); }
    }

    private static byte[] CreateOcrDiagnosticPixels(int width, int height)
    {
        using var bitmap = new Drawing.Bitmap(width, height, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        using (var font = new Drawing.Font("Segoe UI", 42, Drawing.FontStyle.Regular, Drawing.GraphicsUnit.Pixel))
        {
            graphics.Clear(Drawing.Color.White);
            graphics.DrawString("HELLO WORLD 123", font, Drawing.Brushes.Black, 30, 45);
        }
        var bits = bitmap.LockBits(new Drawing.Rectangle(0, 0, width, height), Drawing.Imaging.ImageLockMode.ReadOnly, Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            if (bits.Stride != width * 4) throw new InvalidOperationException("Generated OCR fixture layout differs");
            var pixels = new byte[width * height * 4];
            Marshal.Copy(bits.Scan0, pixels, 0, pixels.Length);
            return pixels;
        }
        finally { bitmap.UnlockBits(bits); }
    }

    private static void CheckOcrDiagnostic(bool value, string error) { if (!value) throw new InvalidOperationException(error); }
    private sealed class DiagnosticOcrEngine : ILocalOcrEngine
    {
        private readonly WindowsOcrEngine _engine = new();
        internal int LanguageEnumerations, Recognitions;
        internal readonly List<long> Durations = new();
        public Task<IReadOnlyList<LocalOcrLanguage>> LanguagesAsync(CancellationToken token)
        { LanguageEnumerations++; return _engine.LanguagesAsync(token); }
        public async Task<LocalOcrResult> RecognizeAsync(ReadOnlyMemory<byte> pixels, int width, int height, string language, CancellationToken token)
        {
            Recognitions++;
            var result = await _engine.RecognizeAsync(pixels, width, height, language, token);
            Durations.Add(result.DurationMs);
            return result;
        }
    }
}
