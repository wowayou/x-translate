using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace XTranslate.App;

public partial class MainWindow
{
    // Fixed smoke text only. This inspects actual WPF bounds, not the entire unscrolled content bitmap.
    private async Task RunMainScrollAcceptanceAsync(List<string> checks, string output, Func<int> requests)
    {
        var language = UiLocalization.Preference; var theme = UiAppearance.Preference;
        var width = Width; var height = Height; var rows = new List<object>();
        var requestCount = requests(); var complete = false; string? failure = null;
        try
        {
            ReviewSourceBox.IsChecked = false;
            foreach (var longText in new[] { false, true })
            foreach (var ui in new[] { UiLanguage.SimplifiedChinese, UiLanguage.English })
            foreach (var minimum in new[] { false, true })
            {
                UiLocalization.SetPreference(ui); UiAppearance.Apply(ui == UiLanguage.English ? UiTheme.Dark : UiTheme.Light);
                SourceBox.Text = longText ? string.Join('\n', Enumerable.Range(1, 100).Select(i => $"SOURCE LINE {i:D3}")) : "confirmed exact OCR";
                ResultBox.Text = longText ? string.Join('\n', Enumerable.Range(1, 140).Select(i => $"FIXED TRANSLATION LINE {i:D3}")) : "Fixed local translation";
                var source = SourceBox.Text; var result = ResultBox.Text;
                Width = minimum ? MinWidth : 620; Height = minimum ? MinHeight : 660;
                WindowViewport.ScrollToTop(); SourceBox.ScrollToHome(); ResultBox.ScrollToHome();
                await SettleMainScrollAsync();
                var sourceHost = (ScrollViewer)SourceBox.Template.FindName("PART_ContentHost", SourceBox);
                var resultHost = (ScrollViewer)ResultBox.Template.FindName("PART_ContentHost", ResultBox);
                var noRedundantPage = minimum || WindowViewport.ComputedVerticalScrollBarVisibility == Visibility.Collapsed;
                var editors = sourceHost.ComputedVerticalScrollBarVisibility == (longText ? Visibility.Visible : Visibility.Collapsed) &&
                    resultHost.ComputedVerticalScrollBarVisibility == (longText ? Visibility.Visible : Visibility.Collapsed);
                var headerFixed = ScrollAncestorCount(SettingsButton, WindowViewport) == -1 && FullyInsideOcrElement(SettingsButton, ViewportFrame);
                var gutter = true;
                if (WindowViewport.ComputedVerticalScrollBarVisibility == Visibility.Visible)
                {
                    var bar = (FrameworkElement)WindowViewport.Template.FindName("PART_VerticalScrollBar", WindowViewport);
                    var cardRight = MainLayout.TransformToAncestor(WindowViewport).Transform(new Point(MainLayout.ActualWidth, 0)).X;
                    var barLeft = bar.TransformToAncestor(WindowViewport).Transform(new Point(0, 0)).X;
                    gutter = barLeft >= cardRight - 1 && OcrScrollBarInside(WindowViewport);
                }
                var shape = new { ui = ui.ToString(), longText, minimum, width = ActualWidth, height = ActualHeight,
                    sourceHeight = SourceBox.ActualHeight, resultHeight = ResultBox.ActualHeight, minimumBodyHeight = MainLayout.MinHeight,
                    bodyRows = MainLayout.RowDefinitions.Select(row => row.ActualHeight).ToArray(),
                    outer = OcrScrollMetrics(WindowViewport), source = OcrScrollMetrics(sourceHost), result = OcrScrollMetrics(resultHost) };
                rows.Add(new { shape, noRedundantPage, editors, headerFixed, gutter });
                RequireAppearance(noRedundantPage && editors && headerFixed && gutter && SourceBox.ActualHeight >= 60 && ResultBox.ActualHeight >= 60,
                    $"Main scroll differs: ui={ui}, long={longText}, minimum={minimum}, outer={WindowViewport.ScrollableHeight:0.0}, editors={editors}, header={headerFixed}, gutter={gutter}, sourceHeight={SourceBox.ActualHeight:0.0}, resultHeight={ResultBox.ActualHeight:0.0}");
                if (longText)
                {
                    OcrWheel(SourceBox, -120); await SettleMainScrollAsync();
                    RequireAppearance(sourceHost.VerticalOffset > 0 && resultHost.VerticalOffset == 0 && WindowViewport.VerticalOffset == 0,
                        "Source wheel moved another editor or the page before its own content");
                    OcrWheel(ResultBox, -120); await SettleMainScrollAsync();
                    RequireAppearance(resultHost.VerticalOffset > 0 && WindowViewport.VerticalOffset == 0, "Result wheel moved the page before its own content");
                }
                foreach (var editor in new System.Windows.Controls.Primitives.TextBoxBase[] { SourceBox, ResultBox })
                {
                    var host = (ScrollViewer)editor.Template.FindName("PART_ContentHost", editor);
                    host.ScrollToBottom(); await SettleMainScrollAsync();
                    WindowViewport.ScrollToTop(); await SettleMainScrollAsync();
                    OcrWheel(editor, -120); await SettleMainScrollAsync();
                    RequireAppearance(WindowViewport.ScrollableHeight <= 0 || WindowViewport.VerticalOffset > 0,
                        "Editor bottom wheel did not hand off to the body viewport");
                    host.ScrollToTop(); await SettleMainScrollAsync();
                    WindowViewport.ScrollToBottom(); await SettleMainScrollAsync();
                    var pageEnd = WindowViewport.VerticalOffset;
                    OcrWheel(editor, 120); await SettleMainScrollAsync();
                    RequireAppearance(WindowViewport.ScrollableHeight <= 0 || WindowViewport.VerticalOffset < pageEnd,
                        "Editor top wheel did not hand off to the body viewport");
                }
                SourceBox.ScrollToEnd(); ResultBox.ScrollToEnd(); await SettleMainScrollAsync();
                WindowViewport.ScrollToBottom(); await SettleMainScrollAsync();
                var footerBounds = UiLanguageBox.TransformToAncestor(ViewportFrame).TransformBounds(new Rect(0, 0, UiLanguageBox.ActualWidth, UiLanguageBox.ActualHeight));
                RequireAppearance(Math.Abs(sourceHost.VerticalOffset - sourceHost.ScrollableHeight) <= 1 &&
                    Math.Abs(resultHost.VerticalOffset - resultHost.ScrollableHeight) <= 1 &&
                    FullyInsideOcrElement(UiLanguageBox, ViewportFrame) && FullyInsideOcrElement(SettingsButton, ViewportFrame) &&
                    SourceBox.Text == source && ResultBox.Text == result && requests() == requestCount,
                    $"Main scroll reachability: source={sourceHost.VerticalOffset}/{sourceHost.ScrollableHeight}, result={resultHost.VerticalOffset}/{resultHost.ScrollableHeight}, page={WindowViewport.VerticalOffset}/{WindowViewport.ScrollableHeight}, footer={FullyInsideOcrElement(UiLanguageBox, ViewportFrame)} bounds={footerBounds} frame={ViewportFrame.ActualWidth}x{ViewportFrame.ActualHeight}, header={FullyInsideOcrElement(SettingsButton, ViewportFrame)}, text={SourceBox.Text == source && ResultBox.Text == result}, requests={requests()}/{requestCount}");
                WindowViewport.ScrollToTop(); SourceBox.ScrollToHome(); ResultBox.ScrollToHome(); await SettleMainScrollAsync();
                var imageSuffix = $".main-scroll.{(longText ? "long" : "short")}.{(minimum ? "minimum" : "default")}.{(ui == UiLanguage.English ? "en.dark" : "zh.light")}";
                RenderWindowTo(this, Path.ChangeExtension(output, imageSuffix + ".png"));
                if (longText && ui == UiLanguage.English)
                    foreach (var scale in new[] { 1.5, 2.0 })
                        RenderWindowTo(this, Path.ChangeExtension(output, imageSuffix + $".{scale * 100:0}.png"), scale);
            }
            complete = true;
            checks.Add("Main window default short/long layouts have no redundant page scrollbar; title stays outside the body viewport, small-window scrollbar has its own gutter, editor wheel/end/boundary handoff and footer remain reachable without HTTP (bilingual light/dark routed-input fixtures)");
        }
        catch (Exception error) { failure = error.Message; throw; }
        finally
        {
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(output)!, "main-scroll.json"), System.Text.Json.JsonSerializer.Serialize(
                new { success = complete, processId = Environment.ProcessId, generatedOnly = true, screenPixelsRead = false,
                    clipboard = false, realHttpRequests = 0, requestsUnchanged = requests() == requestCount, rows, failure },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Width = width; Height = height; UiLocalization.SetPreference(language); UiAppearance.Apply(theme);
            SourceBox.Clear(); ClearResult(); WindowViewport.ScrollToTop();
        }
    }

    // Fluent animates BringIntoView scrolling. Wait for real, stable bounds, not just a
    // dispatcher turn or a guessed animation delay; one request, bounded wait, same assertion.
    private async Task RevealMainElementAsync(FrameworkElement element)
    {
        // Expander transitions also move the target itself. Settle the real transform before
        // asking WPF to reveal it; requesting during expansion freezes an obsolete target offset.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var previousBounds = Rect.Empty; var geometryStable = 0;
        while (watch.Elapsed < TimeSpan.FromSeconds(3))
        {
            await Task.Delay(25); UpdateLayout();
            var currentBounds = element.TransformToAncestor(WindowViewport).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            geometryStable = currentBounds == previousBounds ? geometryStable + 1 : 0;
            if (geometryStable >= 4) break;
            previousBounds = currentBounds;
        }
        RequireAppearance(geometryStable >= 4, "Main body target did not settle within three seconds");
        element.BringIntoView();
        watch.Restart();
        var previous = double.NaN; var stable = 0;
        while (watch.Elapsed < TimeSpan.FromSeconds(3))
        {
            await Task.Delay(25); UpdateLayout();
            var offset = WindowViewport.VerticalOffset;
            stable = FullyInsideOcrElement(element, WindowViewport) && Math.Abs(previous - offset) < 0.1 ? stable + 1 : 0;
            if (stable >= 2) return;
            previous = offset;
        }
        var bounds = element.TransformToAncestor(WindowViewport).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        throw new Exception($"Main body element cannot be reached after bounded scrolling: {element.Name}, bounds={bounds}, viewport={WindowViewport.ActualWidth}x{WindowViewport.ActualHeight}, offset={WindowViewport.VerticalOffset}/{WindowViewport.ScrollableHeight}");
    }

    private async Task SettleMainScrollAsync()
    {
        UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ContextIdle); UpdateLayout();
    }
}
