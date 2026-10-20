using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XTranslate.Core;
using XTranslate.Core.Learning;

namespace XTranslate.App;

public partial class LearningEditorWindow : Window
{
    private readonly Func<LearningContent, CancellationToken, Task> _save;
    private CancellationTokenSource? _saving;
    private bool _closeRequested;
    private Exception? _error;

    internal LearningEditorWindow(LearningContent initial, bool includeContext,
        Func<LearningContent, CancellationToken, Task> save)
    {
        InitializeComponent(); WindowPlacement.Attach(this); _save = save;
        foreach (var kind in Enum.GetValues<LearningKind>()) KindBox.Items.Add(new ComboBoxItem { Tag = kind });
        foreach (var origin in Enum.GetValues<LearningOrigin>()) OriginBox.Items.Add(new ComboBoxItem { Tag = origin });
        foreach (var box in new[] { SourceLanguageBox, TargetLanguageBox })
        {
            box.Items.Add(new ComboBoxItem { Tag = "und" });
            foreach (var language in TranslationLanguages.All) box.Items.Add(new ComboBoxItem { Tag = language.Code, Content = language.NativeName });
        }
        KindBox.SelectedIndex = (int)initial.Kind; OriginBox.SelectedIndex = (int)initial.Origin;
        SelectLanguage(SourceLanguageBox, initial.SourceLanguage); SelectLanguage(TargetLanguageBox, initial.TargetLanguage);
        PromptBox.Text = initial.Prompt; AnswerBox.Text = initial.Answer; ContextBox.Text = initial.Context; NoteBox.Text = initial.Note;
        IncludeContextBox.IsChecked = includeContext; Localize();
        UiLocalization.Changed += Changed; Closing += OnClosing;
        Closed += (_, _) => UiLocalization.Changed -= Changed;
        Loaded += (_, _) => PromptBox.Focus();
    }
    private static void SelectLanguage(ComboBox box, string code) => box.SelectedItem = box.Items.Cast<ComboBoxItem>()
        .FirstOrDefault(i => (string)i.Tag == code) ?? box.Items[0];
    private void Changed(object? sender, EventArgs e) => Localize();
    private void Localize()
    {
        Title = Heading.Text = LearningUi.Text("Editor"); PrivacyHelp.Text = LearningUi.Text("Privacy");
        KindLabel.Text = LearningUi.Text("Kind"); OriginLabel.Text = LearningUi.Text("Origin");
        SourceLanguageLabel.Text = LearningUi.Text("SourceLanguage"); TargetLanguageLabel.Text = LearningUi.Text("TargetLanguage");
        PromptLabel.Text = LearningUi.Text("Prompt"); AnswerLabel.Text = LearningUi.Text("Answer");
        IncludeContextBox.Content = new TextBlock { Text = LearningUi.Text("IncludeContext"), TextWrapping = TextWrapping.Wrap };
        NoteLabel.Text = LearningUi.Text("Note"); EditHelp.Text = LearningUi.Text("EditHelp");
        SaveButton.Content = LearningUi.Text("Save"); CancelButton.Content = LearningUi.Text("Cancel");
        foreach (ComboBoxItem item in KindBox.Items) item.Content = LearningUi.Text(item.Tag.ToString()!);
        foreach (ComboBoxItem item in OriginBox.Items) item.Content = LearningUi.Text(item.Tag.ToString()!);
        foreach (var box in new[] { SourceLanguageBox, TargetLanguageBox }) ((ComboBoxItem)box.Items[0]).Content = LearningUi.Text("UnknownLanguage");
        if (_error is not null) Status.Text = LearningUi.Error(_error);
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_saving is null) return;
        e.Cancel = true; _closeRequested = true; _saving.Cancel(); Status.Text = LearningUi.Text("Cancelling");
    }
    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();
    internal async Task<bool> SaveAsync()
    {
        if (_saving is not null) return false;
        var saved = false;
        using var cancel = new CancellationTokenSource(); _saving = cancel;
        EditorFields.IsEnabled = SaveButton.IsEnabled = false; Status.Text = LearningUi.Text("Working"); _error = null;
        try
        {
            var content = new LearningContent((LearningKind)((ComboBoxItem)KindBox.SelectedItem).Tag, PromptBox.Text, AnswerBox.Text,
                IncludeContextBox.IsChecked == true ? ContextBox.Text : "", NoteBox.Text,
                (string)((ComboBoxItem)SourceLanguageBox.SelectedItem).Tag, (string)((ComboBoxItem)TargetLanguageBox.SelectedItem).Tag,
                (LearningOrigin)((ComboBoxItem)OriginBox.SelectedItem).Tag);
            LearningBook.Validate(content);
            await _save(content, cancel.Token); saved = true;
        }
        catch (Exception error) { _error = error; Status.Text = LearningUi.Error(error); }
        finally { _saving = null; EditorFields.IsEnabled = SaveButton.IsEnabled = true; }
        if (saved) DialogResult = true;
        else if (_closeRequested && _error is OperationCanceledException) Close();
        // A close request is not permission to discard a draft after an unrelated I/O/conflict failure.
        else _closeRequested = false;
        return saved;
    }
    private async void Editor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; await SaveAsync(); }
    }
}
