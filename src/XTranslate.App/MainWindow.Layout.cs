using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace XTranslate.App;

public partial class MainWindow
{
    private void UpdateMainViewportMinimum()
    {
        if (!IsVisible || MainLayout.ActualWidth <= 0) return;
        // Auto rows can grow with locale, recipient/error text, notification bars and expanded
        // source-review details. Keep those measured sizes; only the two editors may shrink.
        var fixedRows = MainLayout.RowDefinitions[0].ActualHeight + MainLayout.RowDefinitions[2].ActualHeight;
        var sourceChrome = MainCardChromeHeight(SourceCard, SourceCardLayout, SourceBox);
        var resultChrome = MainCardChromeHeight(ResultCard, ResultCardLayout, ResultBox);
        // Each editor owns its own minimum; equal-height cards must not force the source to
        // grow just to compensate for the result's extra provenance/review chrome.
        var sourceMinimum = Math.Max(ReviewRequested ? 140 : 0, sourceChrome + 60);
        var resultMinimum = resultChrome + 60;
        var sourceRow = MainLayout.RowDefinitions[1]; var resultRow = MainLayout.RowDefinitions[3];
        if (Math.Abs(sourceRow.MinHeight - sourceMinimum) > 1) sourceRow.MinHeight = sourceMinimum;
        if (Math.Abs(resultRow.MinHeight - resultMinimum) > 1) resultRow.MinHeight = resultMinimum;
        var minimum = Math.Ceiling(fixedRows + sourceMinimum + resultMinimum + 2);
        if (Math.Abs(MainLayout.MinHeight - minimum) > 1) MainLayout.MinHeight = minimum;
    }

    private void MainText_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0 || sender is not TextBoxBase editor ||
            editor.Template.FindName("PART_ContentHost", editor) is not ScrollViewer scroll) return;
        if (e.Delta > 0 ? scroll.VerticalOffset > 0.5 : scroll.VerticalOffset < scroll.ScrollableHeight - 0.5) return;
        if (WindowViewport.ScrollableHeight <= 0) return;
        e.Handled = true;
        WindowViewport.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            { RoutedEvent = MouseWheelEvent, Source = WindowViewport });
    }

    private static double MainCardChromeHeight(Border card, Grid content, FrameworkElement editor)
    {
        var autoRows = 0d;
        foreach (var row in content.RowDefinitions)
            if (row.Height.IsAuto) autoRows += row.ActualHeight;
        return card.BorderThickness.Top + card.BorderThickness.Bottom + card.Padding.Top + card.Padding.Bottom +
            content.Margin.Top + content.Margin.Bottom + autoRows + editor.Margin.Top + editor.Margin.Bottom;
    }
}
