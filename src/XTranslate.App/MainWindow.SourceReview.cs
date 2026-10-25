using System.Text;
using System.Windows;
using XTranslate.Core;

namespace XTranslate.App;

public partial class MainWindow
{
    private SourceReview? _sourceReview;
    // Session-only opt-in: opening the app or receiving a selection cannot enable billable review.
    private bool ReviewRequested => ReviewSourceBox.IsChecked == true;
    private bool ShouldAutoTranslateSelection => _settings.AutoTranslateSelection && !ReviewRequested;

    private void ReviewSource_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        CancelRequest();
        ClearResult();
        UpdateRequestHint();
        RefreshSourceReview();
        SetStatus(ReviewRequested ? "StatusReviewSelected" : "StatusUpdated");
        UpdateInputState();
    }

    private void RefreshSourceReview()
    {
        ReviewSourceBox.Content = UiLocalization.Get("ReviewSource");
        if (_pending is null) TranslateButton.Content = UiLocalization.Get(ReviewRequested ? "ReviewTranslate" : "Translate");
        SourceReviewHelp.Visibility = ReviewRequested ? Visibility.Visible : Visibility.Collapsed;
        SourceReviewHelp.Text = UiLocalization.Get(_settings.Translation.Provider != ProviderKind.OpenAI
            ? "ErrorUnsupportedSourceReview" : "SourceReviewHelp");
        // Source/review/result chrome is measured by the viewport layout. Reserve bounded editor
        // space only when needed, rather than forcing every normal window to scroll a 660-DIP page.
        MainLayout.RowDefinitions[1].Height = ReviewRequested ? new GridLength(140) : new GridLength(1, GridUnitType.Star);
        ReviewDetails.Header = UiLocalization.Get("SourceReviewDetails");
        System.Windows.Automation.AutomationProperties.SetName(SourceReviewText, UiLocalization.Get("SourceReviewDetails"));
        if (_sourceReview is null)
        {
            SourceReviewSummary.Text = UiLocalization.Get(!ReviewRequested ? "SourceNotChecked" :
                _settings.Translation.Provider != ProviderKind.OpenAI ? "ErrorUnsupportedSourceReview" :
                _pending is null ? "SourceReviewNotCompleted" : "SourceReviewWaiting");
            ReviewDetails.Visibility = Visibility.Collapsed;
            SourceReviewText.Clear();
            return;
        }
        SourceReviewSummary.Text = UiLocalization.Get(_sourceReview.Status switch
        {
            SourceReviewStatus.NoObviousIssues => "SourceReviewNoObviousIssues",
            SourceReviewStatus.Issues => "SourceReviewIssues",
            _ => "SourceReviewUncertain"
        });
        var text = new StringBuilder();
        foreach (var issue in _sourceReview.Issues)
        {
            text.AppendLine(UiLocalization.Get("SourceIssue" + issue.Kind));
            text.AppendLine(UiLocalization.Format("SourceIssueQuote", issue.Quote));
            text.AppendLine(issue.Explanation);
            if (issue.Suggestion.Length > 0) text.AppendLine(UiLocalization.Format("SourceIssueSuggestion", issue.Suggestion));
            text.AppendLine();
        }
        text.AppendLine(UiLocalization.Get("SourceBasis" + _sourceReview.TranslationBasis));
        if (_sourceReview.RevisedSource.Length > 0)
            text.AppendLine(UiLocalization.Format("SourceRevised", _sourceReview.RevisedSource));
        if (_sourceReview.Interpretation.Length > 0)
            text.AppendLine(UiLocalization.Format("SourceInterpretation", _sourceReview.Interpretation));
        text.AppendLine();
        text.Append(UiLocalization.Get("SourceReviewDisclaimer"));
        SourceReviewText.Text = text.ToString();
        ReviewDetails.Visibility = Visibility.Visible;
    }
}
