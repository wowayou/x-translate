using System.Windows;
using XTranslate.Core;
using XTranslate.App.Learning;
using XTranslate.Core.Learning;

namespace XTranslate.App;

public partial class MainWindow
{
    private void Learning_Click(object sender, RoutedEventArgs e) => OpenLearning(false);
    private void CollectLearning_Click(object sender, RoutedEventArgs e) => OpenLearning(true);

    internal LearningContent LearningSeed()
    {
        var source = SourceBox.Text;
        var selection = SourceBox.SelectedText;
        var prompt = selection.Length > 0 ? selection : source;
        // A paragraph translation is not an aligned definition for a selected word/phrase.
        var canPrefillAnswer = _pending is null && CopyButton.IsEnabled && (selection.Length == 0 || selection == source);
        var answer = canPrefillAnswer ? ResultBox.Text : "";
        var origin = !canPrefillAnswer ? LearningOrigin.Personal : _sourceReview is null ? LearningOrigin.MachineTranslation : LearningOrigin.AiReview;
        return new(LearningKind.Phrase,prompt,answer,source,"",TranslationLanguages.Find(_resultSource)?.Code ?? "und",
            _settings.Translation.TargetLanguage,origin);
    }
    internal void OpenLearning(bool collect, LearningStore? store = null)
    {
        if (_settingsOpen) return;
        var initial = collect ? LearningSeed() : null;
        CancelRequest(); _capturePending?.Cancel(); _settingsOpen = true; ShowForUser(false);
        try { new LearningWindow(store ?? _storage.CreateLearningStore(),initial: initial) { Owner=this }.ShowDialog(); }
        finally { _settingsOpen = false; UpdateInputState(); }
    }
}
