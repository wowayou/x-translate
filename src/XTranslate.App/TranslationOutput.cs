using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;

namespace XTranslate.App;

/// <summary>
/// Selectable, read-only plain-text output, using WPF's document editor and text automation.
/// Updating a Run avoids TextBox.Text's selection reset, which can synchronously wait on TSF input
/// service callbacks even when IME is disabled. No reflection or process-wide input-method changes.
/// Server output is assigned as text, never parsed as XAML, HTML, RTF or interactive document content.
/// </summary>
public sealed class TranslationOutput : RichTextBox
{
    private readonly Run _run = new();
    private bool _appending;

    public TranslationOutput()
    {
        IsReadOnly = true;
        IsUndoEnabled = false;
        Document = new FlowDocument(new Paragraph(_run) { Margin = new Thickness(0), TextAlignment = TextAlignment.Left })
        {
            PagePadding = new Thickness(0)
        };
        Document.SetBinding(FlowDocument.FontFamilyProperty, new Binding(nameof(FontFamily)) { Source = this });
        Document.SetBinding(FlowDocument.FontSizeProperty, new Binding(nameof(FontSize)) { Source = this });
        Document.SetBinding(FlowDocument.ForegroundProperty, new Binding(nameof(Foreground)) { Source = this });
        Document.SetBinding(FlowDocument.FlowDirectionProperty, new Binding(nameof(FlowDirection)) { Source = this });
        Document.SetBinding(FlowDocument.LanguageProperty, new Binding(nameof(Language)) { Source = this });
    }

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(TranslationOutput),
        new FrameworkPropertyMetadata("", OnTextValueChanged, (_, value) => value ?? ""));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private static void OnTextValueChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var output = (TranslationOutput)sender;
        if (!output._appending) output._run.Text = (string)args.NewValue;
    }

    public new void AppendText(string delta)
    {
        if (string.IsNullOrEmpty(delta)) return;
        var combined = Text + delta;
        _appending = true;
        try
        {
            // Append without replacing the existing run/selection: users may select provisional text.
            _run.ContentEnd.InsertTextInRun(delta);
            SetCurrentValue(TextProperty, combined);
        }
        finally { _appending = false; }
    }

    public void Clear() => Text = "";

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs args)
    {
        base.OnPropertyChanged(args);
        if (args.Property == FlowDirectionProperty && _run?.Parent is Paragraph paragraph)
            paragraph.TextAlignment = (FlowDirection)args.NewValue == FlowDirection.RightToLeft ? TextAlignment.Right : TextAlignment.Left;
    }
}
