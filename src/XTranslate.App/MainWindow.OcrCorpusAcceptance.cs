using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using XTranslate.App.Ocr;
using XTranslate.Core;
using XTranslate.Ocr.Windows;
using Drawing = System.Drawing;

namespace XTranslate.App;

public partial class MainWindow
{
    // Only the explicit corpus-only smoke lane: no native screen/selection checks or normal translation.
    // The same embedded plan is read independently by the runner; grades are fixed before recognition.
    private static byte[] OcrCorpusPlanBytes => OcrCorpusPlan.Bytes;
    private static OcrCorpusSpec[] OcrCorpusSpecs => OcrCorpusPlan.Specs;
    private static class OcrCorpusPlan
    {
        internal static readonly byte[] Bytes = ReadOcrCorpusPlan();
        internal static readonly OcrCorpusSpec[] Specs = JsonSerializer.Deserialize<OcrCorpusSpec[]>(Bytes,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidOperationException("Missing OCR corpus plan");
    }

    internal async Task RunOcrCorpusTestAsync(string? output)
    {
        var checks = new List<string>();
        var succeeded = false;
        var writeOutput = false;
        string? failure = null;
        try
        {
            RequireStream(App.IsOcrCorpus && output is not null && !File.Exists(output), "Corpus acceptance requires a new explicit output");
            writeOutput = true;
            var pngRoot = Path.Combine(Path.GetDirectoryName(output!)!, "ocr-image-fixture");
            RequireStream(!Directory.Exists(pngRoot), "Corpus PNG evidence path already exists");
            Directory.CreateDirectory(pngRoot);
            _service.Dispose();
            using var outerHandler = new SmokeHandler();
            using var outerClient = new HttpClient(outerHandler);
            _service = new TranslationService(outerClient);
            SourceBox.Text = "unchanged corpus main source";
            await VerifyOcrPngAcceptanceAsync(checks, pngRoot);
            await VerifyOcrScrollAcceptanceAsync(checks, output!);
            RequireStream(await RunOcrCorpusAcceptanceAsync(checks, output!, () => outerHandler.RequestCount),
                "Required fixed corpus text mismatch; observational results cannot override it");
            RequireStream(outerHandler.RequestCount == 0 && !_settingsOpen, "Corpus used the outer transport or retained modal state");
            checks.Add("Corpus-only acceptance bypasses screen, selection, hotkey and normal translation regression lanes");
            succeeded = true;
        }
        catch (Exception error) { failure = error is LocalOcrException ocr ? ocr.Code.ToString() : error.GetType().Name; }
        finally
        {
            if (writeOutput)
            {
                try
                {
                    using var stream = new FileStream(output!, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    JsonSerializer.Serialize(stream, new { success = succeeded, mode = "ocr-corpus-only", processId = Environment.ProcessId,
                        checks, failure }, new JsonSerializerOptions { WriteIndented = true });
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { succeeded = false; }
            }
            _allowClose = true;
            Close();
            Application.Current.Shutdown(succeeded ? 0 : 1);
        }
    }

    private async Task<bool> RunOcrCorpusAcceptanceAsync(List<string> checks, string output, Func<int> outerRequests)
    {
        var root = Path.Combine(Path.GetDirectoryName(output)!, "ocr-corpus-fixture");
        var report = Path.Combine(Path.GetDirectoryName(output)!, "ocr-corpus.json");
        RequireStream(!Directory.Exists(root) && !File.Exists(report), "OCR corpus requires fresh evidence paths");
        Directory.CreateDirectory(root);
        var originalSource = SourceBox.Text; var originalTheme = UiAppearance.Preference; var originalLanguage = UiLocalization.Preference;
        var before = outerRequests(); var rows = new List<object>();
        var engine = new CorpusOcrEngine();
        using var handler = new CorpusTranslationHandler(); using var client = new HttpClient(handler); using var service = new TranslationService(client);
        var settings = new UserSettings { Translation = new TranslationSettings { Provider = ProviderKind.GoogleWeb, TargetLanguage = "zh-CN" } };
        var clock = Stopwatch.StartNew(); var operational = true; var requiredPassed = true;
        void WriteReport(bool complete, string? failure = null) => File.WriteAllText(report, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, success = complete && operational && requiredPassed, complete, operational, requiredPassed,
            processId = Environment.ProcessId, completedUtc = DateTimeOffset.UtcNow, elapsedMs = Math.Round(clock.Elapsed.TotalMilliseconds, 3),
            machine = new { os = Environment.OSVersion.VersionString, runtime = RuntimeInformation.FrameworkDescription,
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(), edition = AppUpdates.Flavor?.ToString() ?? "Dev",
                version = AppUpdates.Current.ToString(), processRenderMode = System.Windows.Media.RenderOptions.ProcessRenderMode.ToString(),
                remoteSession = SystemParameters.IsRemoteSession },
            configuration = new
            {
                generatedOnly = true, screenPixelsRead = false, clipboard = false, userData = false,
                network = "none: fixed in-process HttpMessageHandler", retries = 0, recognitionLimit = OcrCorpusSpecs.Length,
                translationLimit = OcrCorpusSpecs.Length, planSha256 = Convert.ToHexString(SHA256.HashData(OcrCorpusPlanBytes)),
                grade = "ordinal text after removing whitespace only; no case/punctuation/order repair",
                accuracyClaim = "limited fixed generated images; observational mismatches/skips are not accuracy passes"
            },
            plannedCases = OcrCorpusSpecs, installedLanguages = engine.Installed,
            languageEnumerations = engine.LanguageCalls, recognitions = engine.RecognitionCalls, mockTranslations = handler.Requests.Count,
            mockRequests = handler.Requests.Select(request => new { request.Host, request.Text, request.Target }).ToArray(),
            outerMockRequests = outerRequests() - before, mainSourceUnchanged = SourceBox.Text == originalSource,
            modalClosed = !_settingsOpen && !Application.Current.Windows.OfType<OcrWindow>().Any(window => window.IsVisible),
            cases = rows, failure
        }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        try
        {
            WriteReport(false);
            RequireStream(OcrCorpusSpecs.Length == 10 && OcrCorpusSpecs.Count(spec => spec.Required) == 4 &&
                OcrCorpusSpecs.Select(spec => spec.Id).Distinct(StringComparer.Ordinal).Count() == 10, "Corpus plan differs from its bounded scope");
            foreach (var spec in OcrCorpusSpecs)
            {
                RequireStream(spec.Id.Length is > 0 and <= 40 && spec.Id.All(character => char.IsAsciiLetterLower(character) || character is '-' or >= '0' and <= '9') &&
                    spec.ExpectedText.Length is > 0 and <= 3000 && spec.Dpi is >= 96 and <= 300, "Invalid bounded corpus specification");
                LocalOcrLimits.ValidateImage(spec.Width, spec.Height, checked(spec.Width * spec.Height * 4));
                LocalOcrLimits.ValidateLanguage(spec.Language); LocalOcrLimits.ValidateText(spec.ExpectedText);
            }
            var languages = await engine.LanguagesAsync(CancellationToken.None);
            RequireStream(languages.Any(language => language.Tag == "en-US"), "OCR corpus requires installed en-US; no language download/fallback");
            foreach (var spec in OcrCorpusSpecs)
            {
                var path = Path.Combine(root, spec.Id + ".png");
                var raw = CreateOcrCorpusPixels(spec);
                try
                {
                    SaveOcrAcceptancePng(path, spec.Width, spec.Height, PixelFormats.Bgra32, raw, spec.Width * 4, dpi: spec.Dpi);
                    FlattenCorpusPixels(raw);
                    var pngHash = HashOcrCorpusFile(path); var pixelHash = Convert.ToHexString(SHA256.HashData(raw));
                    if (!languages.Any(language => language.Tag == spec.Language))
                    {
                        rows.Add(new { spec.Id, spec.Language, spec.Required, status = "language-unavailable", spec.Width, spec.Height, spec.Dpi,
                            spec.ExpectedText, actualText = (string?)null, whitespaceOnlyMatch = false, grade = "language-unavailable",
                            pngSha256 = pngHash, pixelSha256 = pixelHash, loadCalls = 0, languageEnumerations = 0, recognitions = 0, mockTranslations = 0 });
                        if (spec.Required) requiredPassed = false;
                        WriteReport(false); continue; // Never attempt the sample in another language.
                    }
                    engine.Expected = raw; engine.Spec = spec; engine.LastResult = null; engine.LastError = null;
                    OcrImage? admitted = null; var loadCalls = 0; var readsBefore = engine.RecognitionCalls; var languageBefore = engine.LanguageCalls;
                    var requestsBefore = handler.Requests.Count; string? actual = null; var status = "not-exercised";
                    var previewExact = false; var rawPreserved = false; var closedCleared = false; var safe = false;
                    string? comparison = null;
                    await WithLearningModalAsync<OcrWindow>(() => new OcrWindow(engine, _ => true, async (imagePath, token) =>
                    {
                        RequireStream(imagePath == path, "Corpus loader attempted a non-generated path"); loadCalls++;
                        return admitted = await OcrImageLoader.LoadAsync(imagePath, token);
                    }, translate: (text, selected, token) => service.TranslateAsync(text, selected, token), settings: settings)
                    { Owner = this }.ShowDialog(), async view =>
                    {
                        try
                        {
                            await view.Loading;
                            RequireStream(view.LanguageBox.SelectedIndex == -1 && engine.RecognitionCalls == readsBefore && handler.Requests.Count == requestsBefore,
                                "Corpus initialization guessed language or performed work");
                            RequireStream(await view.LoadImageAsync(path) && admitted is not null, "Corpus PNG failed production loading");
                            var image = admitted!; var preview = new byte[raw.Length];
                            try
                            {
                                image.Preview.CopyPixels(preview, spec.Width * 4, 0);
                                previewExact = image.Width == spec.Width && image.Height == spec.Height && image.Preview.DpiX == 96 && image.Preview.DpiY == 96 &&
                                    image.Pixels.AsSpan().SequenceEqual(raw) && preview.AsSpan().SequenceEqual(raw);
                            }
                            finally { Array.Clear(preview); }
                            RequireStream(previewExact && engine.RecognitionCalls == readsBefore && handler.Requests.Count == requestsBefore,
                                "Corpus PNG changed physical pixels/preview or implicitly recognized/sent text");
                            view.LanguageBox.SelectedItem = view.LanguageBox.Items.Cast<LocalOcrLanguage>().Single(language => language.Tag == spec.Language);
                            if (await view.RecognizeAsync())
                            {
                                var result = engine.LastResult ?? throw new InvalidOperationException("Corpus backend result missing");
                                actual = result.Text; status = "recognized";
                                rawPreserved = view.TextBox.Text == actual && result.Language == spec.Language;
                                RequireStream(rawPreserved && view.ResultBox.Text.Length == 0 && handler.Requests.Count == requestsBefore &&
                                    image.Pixels.AsSpan().SequenceEqual(raw), "Corpus recognition changed raw text/image or implicitly translated");
                                handler.ExpectedText = actual;
                                RequireStream(await view.TranslateAsync() && handler.Requests.Count == requestsBefore + 1 &&
                                    handler.Requests[^1].Text == actual && handler.Requests[^1].Target == "zh-CN" &&
                                    handler.Requests[^1].Host == "translate.googleapis.com" && view.ResultBox.Text == handler.Result && view.CopyTranslationButton.IsEnabled,
                                    "Corpus explicit translation did not send exact raw text once to the selected mock service");
                                if (spec.Id is "multiline-en" or "columns-en" or "mixed-zh")
                                {
                                    UiLocalization.SetPreference(spec.Language == "en-US" ? UiLanguage.English : UiLanguage.SimplifiedChinese);
                                    UiAppearance.Apply(spec.Id == "columns-en" ? UiTheme.Dark : UiTheme.Light);
                                    RequireStream(handler.Requests.Count == requestsBefore + 1 && view.TextBox.Text == actual && view.ResultBox.Text == handler.Result &&
                                        view.CopyTranslationButton.IsEnabled && view.ResultInfo.Text.Contains(UiLocalization.Get("SourceNotChecked"), StringComparison.Ordinal),
                                        "Corpus relocalization changed work/content or retained old-language provenance");
                                    view.ComparisonViewport.ScrollToTop(); view.UpdateLayout();
                                    comparison = spec.Id + ".comparison.png"; RenderWindowTo(view, Path.Combine(root, comparison));
                                }
                                safe = true;
                            }
                            else
                            {
                                status = engine.LastError == LocalOcrError.EmptyText ? "empty-text" : "engine-failure";
                                safe = engine.LastError == LocalOcrError.EmptyText && view.StatusKey == "OcrErrorEmptyText" &&
                                    view.TextBox.Text.Length == 0 && view.ResultBox.Text.Length == 0 && !view.CopyTranslationButton.IsEnabled &&
                                    handler.Requests.Count == requestsBefore && image.Pixels.AsSpan().SequenceEqual(raw);
                            }
                        }
                        finally
                        {
                            view.Close();
                            closedCleared = admitted is not null && admitted.Pixels.All(value => value == 0) && view.ImagePreview.Source is null &&
                                view.TextBox.Text.Length == 0 && view.ResultBox.Text.Length == 0;
                        }
                    });
                    var fileUnchanged = pngHash == HashOcrCorpusFile(path);
                    var match = actual is not null && CorpusWhitespace(actual) == CorpusWhitespace(spec.ExpectedText);
                    var recognitions = engine.RecognitionCalls - readsBefore; var translations = handler.Requests.Count - requestsBefore;
                    operational &= safe && previewExact && closedCleared && fileUnchanged && loadCalls == 1 && recognitions == 1 &&
                        engine.LanguageCalls - languageBefore == 1 && translations == (actual is null ? 0 : 1);
                    if (spec.Required) requiredPassed &= match;
                    rows.Add(new
                    {
                        spec.Id, spec.Language, spec.Required, status, spec.Width, spec.Height, spec.Dpi, spec.ExpectedText, actualText = actual,
                        whitespaceOnlyMatch = match, grade = actual is null ? status : match ? "exact-whitespace-only" : "text-mismatch",
                        pngSha256 = pngHash, pixelSha256 = pixelHash, previewInputExact = previewExact, rawTextPreserved = rawPreserved,
                        sourceFileUnchanged = fileUnchanged, closedPixelsCleared = closedCleared, safe, loadCalls,
                        languageEnumerations = engine.LanguageCalls - languageBefore, recognitions, mockTranslations = translations, comparison,
                        comparisonSha256 = comparison is null ? null : HashOcrCorpusFile(Path.Combine(root, comparison)),
                        recognitionMs = engine.LastResult?.DurationMs, errorCode = engine.LastError?.ToString()
                    });
                    WriteReport(false);
                    RequireStream(operational, "OCR corpus safety/execution failed; preserve fixed corpus evidence (no retry)");
                }
                finally { engine.Expected = null; Array.Clear(raw); }
            }
            RequireStream(SourceBox.Text == originalSource && outerRequests() == before, "OCR corpus changed main source or used the outer HTTP handler");
            WriteReport(true); // A complete accuracy failure stays complete, not an operational failure.
            if (requiredPassed)
            {
                checks.Add("Fixed generated multiline, transparent, dark and DPI-metadata text meets whitespace-only expectations; columns/table/skew/contrast/noise/mixed-Chinese retain separate observational grades");
                checks.Add("Each exercised PNG uses production decoding/local OCR and one explicit raw-text mock translation; preview/pixels and file hashes stay exact, close clears owned content, no screen/user/clipboard/network input");
            }
            return requiredPassed;
        }
        catch (Exception error)
        {
            operational = false;
            WriteReport(false, error is LocalOcrException ocr ? ocr.Code.ToString() : error.GetType().Name);
            throw;
        }
        finally { UiLocalization.SetPreference(originalLanguage); UiAppearance.Apply(originalTheme); }
    }

    private sealed class CorpusTranslationHandler : HttpMessageHandler
    {
        internal readonly List<(string Host, string Text, string Target)> Requests = new();
        internal readonly string Result = "固定样本译文 · fixed corpus translation";
        internal string? ExpectedText;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var uri = request.RequestUri ?? throw new InvalidOperationException("Corpus mock request has no URI");
            RequireStream(Requests.Count < OcrCorpusSpecs.Length && request.Method == HttpMethod.Get && uri.Scheme == "https" &&
                uri.Host == "translate.googleapis.com" && uri.AbsolutePath == "/translate_a/single", "Corpus mock transport/limit differs");
            var query = uri.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2))
                .ToDictionary(part => part[0], part => Uri.UnescapeDataString(part[1]));
            var text = query["q"]; var target = query["tl"];
            RequireStream(text == ExpectedText && target == "zh-CN", "Corpus mock request differs from the exact recognized source");
            Requests.Add((uri.Host, text, target));
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new object?[] { new object[] { new object[] { Result, text } }, null, "auto" }),
                    System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private static byte[] ReadOcrCorpusPlan()
    {
        using var resource = typeof(MainWindow).Assembly.GetManifestResourceStream("XTranslate.OcrCorpusPlan.json")
            ?? throw new InvalidOperationException("Missing embedded OCR plan");
        using var bytes = new MemoryStream(); resource.CopyTo(bytes); return bytes.ToArray();
    }
    private static string CorpusWhitespace(string text) => new(text.Where(character => !char.IsWhiteSpace(character)).ToArray());
    private static string HashOcrCorpusFile(string path)
    { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void FlattenCorpusPixels(byte[] pixels)
    {
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            for (var channel = 0; channel < 3; channel++) pixels[i + channel] = (byte)((pixels[i + channel] * alpha + 255 * (255 - alpha) + 127) / 255);
            pixels[i + 3] = 255;
        }
    }
    private static byte[] CreateOcrCorpusPixels(OcrCorpusSpec spec)
    {
        using var bitmap = new Drawing.Bitmap(spec.Width, spec.Height, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        using (var font = new Drawing.Font(spec.Language == "en-US" ? "Segoe UI" : "Microsoft YaHei UI", spec.Id is "table-en" or "low-contrast-en" or "noise-en" ? 32 : 42,
            Drawing.FontStyle.Regular, Drawing.GraphicsUnit.Pixel))
        {
            RequireStream(font.FontFamily.Name == (spec.Language == "en-US" ? "Segoe UI" : "Microsoft YaHei UI"), "Corpus font unavailable; no silent substitute");
            graphics.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(spec.Id == "transparent-en" ? Drawing.Color.Transparent : spec.Id == "dark-en" ? Drawing.Color.FromArgb(32, 32, 32) : Drawing.Color.White);
            using var brush = new Drawing.SolidBrush(spec.Id == "dark-en" ? Drawing.Color.White : spec.Id == "low-contrast-en" ? Drawing.Color.FromArgb(192, 192, 192) : Drawing.Color.Black);
            switch (spec.Id)
            {
                case "columns-en":
                    graphics.DrawString("LEFT ONE 111", font, brush, 30, 45); graphics.DrawString("LEFT TWO 222", font, brush, 30, 140);
                    graphics.DrawString("RIGHT ONE 333", font, brush, 640, 45); graphics.DrawString("RIGHT TWO 444", font, brush, 640, 140); break;
                case "table-en":
                    using (var pen = new Drawing.Pen(Drawing.Color.Gray, 2))
                    {
                        for (var row = 0; row < 4; row++) graphics.DrawLine(pen, 25, 25 + row * 90, 975, 25 + row * 90);
                        foreach (var x in new[] { 25, 525, 975 }) graphics.DrawLine(pen, x, 25, x, 295);
                    }
                    for (var row = 0; row < 3; row++)
                    { graphics.DrawString(new[] { "ITEM", "ALPHA", "BETA" }[row], font, brush, 50, 48 + row * 90); graphics.DrawString(new[] { "VALUE", "123", "456" }[row], font, brush, 555, 48 + row * 90); }
                    break;
                case "skew-en":
                    graphics.TranslateTransform(35, 100); graphics.RotateTransform(-6); graphics.DrawString(spec.ExpectedText, font, brush, 0, 0); break;
                case "noise-en":
                    var random = new Random(7421);
                    using (var noise = new Drawing.SolidBrush(Drawing.Color.FromArgb(160, 160, 160)))
                        for (var dot = 0; dot < 1500; dot++) graphics.FillRectangle(noise, random.Next(spec.Width), random.Next(spec.Height), 2, 2);
                    graphics.DrawString(spec.ExpectedText, font, brush, 30, 55); break;
                default:
                    var lines = spec.ExpectedText.Split('\n');
                    for (var line = 0; line < lines.Length; line++) graphics.DrawString(lines[line], font, brush, 30, 40 + line * 80);
                    break;
            }
        }
        var bits = bitmap.LockBits(new Drawing.Rectangle(0, 0, spec.Width, spec.Height), Drawing.Imaging.ImageLockMode.ReadOnly, Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            RequireStream(bits.Stride == spec.Width * 4, "Corpus bitmap layout differs");
            var pixels = new byte[spec.Width * spec.Height * 4]; Marshal.Copy(bits.Scan0, pixels, 0, pixels.Length); return pixels;
        }
        finally { bitmap.UnlockBits(bits); }
    }
    private sealed record OcrCorpusSpec(string Id, string Language, bool Required, int Width, int Height, string ExpectedText, int Dpi);
    private sealed class CorpusOcrEngine : ILocalOcrEngine
    {
        private readonly WindowsOcrEngine _engine = new();
        internal int LanguageCalls, RecognitionCalls;
        internal IReadOnlyList<LocalOcrLanguage> Installed = Array.Empty<LocalOcrLanguage>();
        internal byte[]? Expected; internal OcrCorpusSpec? Spec;
        internal LocalOcrResult? LastResult; internal LocalOcrError? LastError;
        public async Task<IReadOnlyList<LocalOcrLanguage>> LanguagesAsync(CancellationToken token)
        { LanguageCalls++; return Installed = await _engine.LanguagesAsync(token); }
        public async Task<LocalOcrResult> RecognizeAsync(ReadOnlyMemory<byte> pixels, int width, int height, string language, CancellationToken token)
        {
            RequireStream(RecognitionCalls < OcrCorpusSpecs.Length && Spec is not null && width == Spec.Width && height == Spec.Height &&
                language == Spec.Language && Expected is not null && pixels.Span.SequenceEqual(Expected), "Corpus recognition input/limit differs");
            RecognitionCalls++;
            try { return LastResult = await _engine.RecognizeAsync(pixels, width, height, language, token); }
            catch (LocalOcrException error) { LastError = error.Code; throw; }
        }
    }
}
