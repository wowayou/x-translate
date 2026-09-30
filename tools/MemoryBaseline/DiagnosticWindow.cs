using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MemoryBaseline;

internal sealed class DiagnosticWindow : Window
{
    public DiagnosticWindow()
    {
        Width = 650;
        Height = 740;
        MinWidth = 490;
        MinHeight = 560;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        Background = System.Windows.Media.Brushes.White;
        Content = new Grid();
        Title = "XTranslate memory baseline";
    }
}
