using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using XTranslate.Core;

namespace XTranslate.App;

public partial class MainWindow
{
    private async Task VerifyOcrScrollAcceptanceAsync(List<string> checks, string output)
    {
        var originalLanguage = UiLocalization.Preference; var originalTheme = UiAppearance.Preference;
        var rows = new List<object>(); var captures = 0; var sends = 0;
        var fake = new OcrFixtureEngine(); var translated = "short fixed translation";
        var report = Path.Combine(Path.GetDirectoryName(output)!, "ocr-scroll.json");
        RequireStream(!File.Exists(report), "OCR scroll evidence already exists");
        var completed = false; string? failure = null;
        try
        {
            await WithLearningModalAsync<OcrWindow>(() => new OcrWindow(fake, _ => true,
                capture: _ => { captures++; return Task.FromResult<Ocr.OcrImage?>(NewScreenFixture()); },
                translate: (_, settings, _) => { sends++; return Task.FromResult(new TranslationResult(translated, "Fixed mock", "in-process", null, 0, "en", settings.TargetLanguage)); })
            { Owner = this }.ShowDialog(), async view =>
            {
                await view.Loading; view.LanguageBox.SelectedIndex = 0;
                foreach (var longText in new[] { false, true })
                {
                    fake.Text = longText ? string.Join('\n', Enumerable.Range(1, 100).Select(index => $"SOURCE LINE {index:D3}")) : "short fixed source";
                    translated = longText ? string.Join('\n', Enumerable.Range(1, 160).Select(index => $"TRANSLATION LINE {index:D3} — fixed generated text only.")) : "short fixed translation";
                    RequireStream(await view.CaptureAsync(), "Fixed scroll fixture did not recognize/translate");
                    foreach (var language in new[] { UiLanguage.SimplifiedChinese, UiLanguage.English })
                    {
                        UiLocalization.SetPreference(language); UiAppearance.Apply(language == UiLanguage.English ? UiTheme.Dark : UiTheme.Light);
                        foreach (var minimum in new[] { false, true })
                        {
                            view.Width = minimum ? view.MinWidth : 1000; view.Height = minimum ? view.MinHeight : 760;
                            view.ComparisonViewport.ScrollToTop(); view.TextBox.ScrollToHome(); view.ResultBox.ScrollToHome();
                            await SettleOcrScrollAsync(view);
                            var sourceHost = (ScrollViewer)view.TextBox.Template.FindName("PART_ContentHost", view.TextBox);
                            var resultHost = (ScrollViewer)view.ResultBox.Template.FindName("PART_ContentHost", view.ResultBox);
                            var shape = new { language = language.ToString(), longText, minimum, width = view.ActualWidth, height = view.ActualHeight,
                                bodyHeight = view.BodyFrame.ActualHeight, sourceHeight = view.TextBox.ActualHeight, resultHeight = view.ResultBox.ActualHeight,
                                outer = OcrScrollMetrics(view.ComparisonViewport), source = OcrScrollMetrics(sourceHost), result = OcrScrollMetrics(resultHost) };
                            var editorsOwnScroll = ScrollAncestorCount(view.TextBox, view.ComparisonViewport) == 1 &&
                                ScrollAncestorCount(view.ResultBox, view.ComparisonViewport) == 1;
                            var barsExpected = view.ComparisonViewport.ComputedVerticalScrollBarVisibility == (minimum ? Visibility.Visible : Visibility.Collapsed) &&
                                sourceHost.ComputedVerticalScrollBarVisibility == (longText ? Visibility.Visible : Visibility.Collapsed) &&
                                resultHost.ComputedVerticalScrollBarVisibility == (longText ? Visibility.Visible : Visibility.Collapsed) &&
                                new[] { view.ComparisonViewport, sourceHost, resultHost }.All(host => host.ComputedHorizontalScrollBarVisibility == Visibility.Collapsed);
                            var footerVisible = new[] { view.CopyTranslationButton, view.UseTextButton, view.CancelButton }.All(button =>
                                button.ActualHeight >= 30 && FullyInsideOcrElement(button, (FrameworkElement)view.Content));
                            var thumbInside = new[] { view.ComparisonViewport, sourceHost, resultHost }.All(OcrScrollBarInside);
                            var interaction = await ExerciseOcrScrollAsync(view, sourceHost, resultHost, longText, minimum);
                            var contentUnchanged = view.TextBox.Text == fake.Text && view.ResultBox.Text == translated && view.CopyTranslationButton.IsEnabled &&
                                view.ResultInfo.Text.Contains(UiLocalization.Get("SourceNotChecked"), StringComparison.Ordinal) && captures == sends && sends == (longText ? 2 : 1);
                            var row = new { shape, editorsOwnScroll, barsExpected, footerVisible, thumbInside, interaction, contentUnchanged };
                            rows.Add(row);
                            RequireStream(editorsOwnScroll && barsExpected && footerVisible && thumbInside && interaction && contentUnchanged,
                                $"OCR scroll contract differs: language={language}, long={longText}, minimum={minimum}, hierarchy={editorsOwnScroll}, bars={barsExpected}, footer={footerVisible}, track={thumbInside}, interaction={interaction}, content={contentUnchanged}");
                            var name = $".ocr-scroll.{(longText ? "long" : "short")}.{(minimum ? "minimum" : "default")}.{(language == UiLanguage.English ? "en.dark" : "zh.light")}.png";
                            var images = new List<object>();
                            void SaveScrollImage(string suffix, double scale = 1)
                            {
                                var path = Path.ChangeExtension(output, suffix); RenderWindowTo(view, path, scale);
                                images.Add(new { name = Path.GetFileName(path), scale, sha256 = HashOcrCorpusFile(path) });
                            }
                            SaveScrollImage(name);
                            if (longText && language == UiLanguage.English)
                                foreach (var scale in new[] { 1.5, 2.0 }) SaveScrollImage(name.Replace(".png", $".{scale * 100:0}.png", StringComparison.Ordinal), scale);
                            rows[^1] = new { row.shape, row.editorsOwnScroll, row.barsExpected, row.footerVisible, row.thumbInside,
                                row.interaction, row.contentUnchanged, images };
                        }
                    }
                }
                view.Close();
            });
            RequireStream(captures == 2 && fake.LanguageReads == 1 && fake.Recognitions == 2 && sends == 2, "OCR scroll acceptance repeated its fixed work");
            completed = true;
            checks.Add("OCR scroll has no redundant left-column scroll: short default content needs no bars, long editors scroll independently, minimum windows scroll the page; wheel boundary handoff/end reachability and fixed footer pass bilingual light/dark layouts (routed input, not physical mouse)");
        }
        catch (Exception error) { failure = error.Message; throw; }
        finally
        {
            File.WriteAllText(report, System.Text.Json.JsonSerializer.Serialize(new { success = completed, processId = Environment.ProcessId,
                generatedOnly = true, screenPixelsRead = false, clipboard = false, userData = false, realRecognitions = 0,
                syntheticCaptures = captures, languageEnumerations = fake.LanguageReads, syntheticRecognitions = fake.Recognitions,
                mockTranslations = sends, rows, failure }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            UiLocalization.SetPreference(originalLanguage); UiAppearance.Apply(originalTheme);
        }
    }
    private async Task<bool> ExerciseOcrScrollAsync(OcrWindow view, ScrollViewer source, ScrollViewer result, bool longText, bool minimum)
    {
        var valid = true;
        void CheckScroll(bool condition, string phase)
        {
            RequireStream(condition, "OCR routed-scroll contract failed at " + phase);
            valid &= condition;
        }
        foreach (var (editor, host) in new (System.Windows.Controls.Primitives.TextBoxBase, ScrollViewer)[] { (view.TextBox, source), (view.ResultBox, result) })
        {
            view.ComparisonViewport.ScrollToTop(); host.ScrollToTop(); await SettleOcrScrollAsync(view);
            OcrWheel(editor, -120); await SettleOcrScrollAsync(view);
            CheckScroll(longText ? host.VerticalOffset > 0 && view.ComparisonViewport.VerticalOffset == 0 :
                minimum ? view.ComparisonViewport.VerticalOffset > 0 : view.ComparisonViewport.VerticalOffset == 0,
                $"initial-wheel/{editor.Name}: editor={host.VerticalOffset}, outer={view.ComparisonViewport.VerticalOffset}");
            host.ScrollToBottom(); view.ComparisonViewport.ScrollToTop(); await SettleOcrScrollAsync(view);
            var end = host.VerticalOffset;
            OcrWheel(editor, -120); await SettleOcrScrollAsync(view);
            CheckScroll(Math.Abs(host.VerticalOffset - end) <= 1 && (!minimum || view.ComparisonViewport.VerticalOffset > 0),
                $"bottom-handoff/{editor.Name}: editor={host.VerticalOffset}, expectedEnd={end}, outer={view.ComparisonViewport.VerticalOffset}");
            view.ComparisonViewport.ScrollToBottom(); host.ScrollToTop(); await SettleOcrScrollAsync(view);
            var pageEnd = view.ComparisonViewport.VerticalOffset;
            OcrWheel(editor, 120); await SettleOcrScrollAsync(view);
            CheckScroll(!minimum || view.ComparisonViewport.VerticalOffset < pageEnd,
                $"top-handoff/{editor.Name}: outer={view.ComparisonViewport.VerticalOffset}, expectedLess={pageEnd}");
        }
        view.ComparisonViewport.ScrollToBottom(); view.TextBox.ScrollToEnd(); view.ResultBox.ScrollToEnd();
        await SettleOcrScrollAsync(view);
        CheckScroll(Math.Abs(source.VerticalOffset - source.ScrollableHeight) <= 1 && Math.Abs(result.VerticalOffset - result.ScrollableHeight) <= 1 &&
            FullyInsideOcrElement(view.TextBox, view.ComparisonViewport) && FullyInsideOcrElement(view.ReviewHelp, view.ComparisonViewport),
            $"source-end-reachable: source={source.VerticalOffset}/{source.ScrollableHeight}, result={result.VerticalOffset}/{result.ScrollableHeight}");
        view.ResultBox.BringIntoView(); await SettleOcrScrollAsync(view);
        CheckScroll(FullyInsideOcrElement(view.ResultBox, view.ComparisonViewport) && FullyInsideOcrElement(view.ResultInfo, view.ComparisonViewport),
            "result-end-reachable");
        view.ComparisonViewport.ScrollToBottom(); await SettleOcrScrollAsync(view);
        return valid;
    }
    private static void OcrWheel(System.Windows.Controls.Primitives.TextBoxBase editor, int delta)
    {
        var host = (ScrollViewer)editor.Template.FindName("PART_ContentHost", editor);
        var preview = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
            { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = host };
        host.RaiseEvent(preview);
        // A synthetic Preview event alone does not create WPF's paired bubbling MouseWheel event.
        // Mirror the native routing pair unless the boundary handler already consumed it.
        if (!preview.Handled) host.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
            { RoutedEvent = Mouse.MouseWheelEvent, Source = host });
    }
    private async Task SettleOcrScrollAsync(OcrWindow view)
    {
        view.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ContextIdle); view.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle); view.UpdateLayout();
    }
    private static int ScrollAncestorCount(DependencyObject element, ScrollViewer outer)
    {
        var count = 0;
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ScrollViewer) count++;
            if (ReferenceEquals(parent, outer)) return count;
        }
        return -1;
    }
    private static bool FullyInsideOcrElement(FrameworkElement child, FrameworkElement viewport)
    {
        var bounds = child.TransformToAncestor(viewport).TransformBounds(new Rect(0, 0, child.ActualWidth, child.ActualHeight));
        return bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= viewport.ActualWidth + 1 && bounds.Bottom <= viewport.ActualHeight + 1;
    }
    private static bool OcrScrollBarInside(ScrollViewer host)
    {
        if (host.ComputedVerticalScrollBarVisibility != Visibility.Visible) return true;
        return host.Template.FindName("PART_VerticalScrollBar", host) is FrameworkElement bar && bar.ActualWidth > 0 && bar.ActualHeight > 0 && FullyInsideOcrElement(bar, host);
    }
    private static object OcrScrollMetrics(ScrollViewer viewer) => new
    {
        actualWidth = viewer.ActualWidth, actualHeight = viewer.ActualHeight, extentHeight = viewer.ExtentHeight, viewportHeight = viewer.ViewportHeight,
        scrollableHeight = viewer.ScrollableHeight, verticalOffset = viewer.VerticalOffset, verticalBar = viewer.ComputedVerticalScrollBarVisibility.ToString(),
        horizontalBar = viewer.ComputedHorizontalScrollBarVisibility.ToString()
    };
}
