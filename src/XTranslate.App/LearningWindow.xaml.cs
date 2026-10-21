using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using XTranslate.App.Learning;
using XTranslate.Core.Learning;

namespace XTranslate.App;

public partial class LearningWindow : Window
{
    private readonly LearningStore _store;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<string, bool> _confirm;
    private LearningBook? _book;
    private LearningCard? _reviewCard;
    private bool _revealed;
    private bool _suppressLimit;
    private bool _closeRequested;
    private readonly HashSet<string> _skipped = new(StringComparer.Ordinal);
    private CancellationTokenSource? _operation;
    private Exception? _error;
    private string _statusKey = "";
    internal LearningBook? Book => _book;
    internal Task Loading { get; private set; } = Task.CompletedTask;

    internal LearningWindow(LearningStore store, Func<DateTimeOffset>? now = null, Func<string, bool>? confirm = null, LearningContent? initial = null)
    {
        _store = store; _now = now ?? (() => DateTimeOffset.UtcNow);
        _confirm = confirm ?? (message => MessageBox.Show(this, message, LearningUi.Text("Book"), MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
        InitializeComponent(); WindowPlacement.Attach(this);
        LimitBox.ItemsSource = Enumerable.Range(1,50); Localize();
        async void Initialize(object sender, RoutedEventArgs e)
        {
            Loaded -= Initialize; // Theme/template changes can raise Loaded again, even during ShowDialog.
            var loading = ReloadAsync(); Loading = loading;
            var loaded = await loading;
            // Do not enter a nested ShowDialog from the parent's Loaded/layout/ShowWindow stack. Allow
            // the parent's initialization to settle first; closing the child must unwind only its frame.
            await Dispatcher.InvokeAsync(() =>
            {
                if (loaded && initial is not null && IsVisible) OpenEditor(null,initial);
            }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
        Loaded += Initialize;
        Closing += OnClosing; UiLocalization.Changed += Changed;
        Closed += (_, _) => UiLocalization.Changed -= Changed;
        RefreshControls();
    }

    private void Changed(object? sender, EventArgs e) => Localize();
    private void Localize()
    {
        Title = Heading.Text = LearningUi.Text("Book"); PrivacyHelp.Text = LearningUi.Text("BookHelp");
        NewButton.Content = LearningUi.Text("New"); EditButton.Content = LearningUi.Text("Edit"); DeleteButton.Content = LearningUi.Text("Delete");
        ExportButton.Content = LearningUi.Text("Export"); RestoreButton.Content = LearningUi.Text("Restore"); RefreshButton.Content = LearningUi.Text("Refresh");
        LimitLabel.Text = LearningUi.Text("DailyLimit"); LibraryTab.Header = LearningUi.Text("Library"); ReviewTab.Header = LearningUi.Text("Review");
        ReviewHelp.Text = LearningUi.Text("ReviewHelp"); RecallPromptLabel.Text = LearningUi.Text("RecallPrompt"); ReferenceLabel.Text = LearningUi.Text("ReferenceAnswer");
        HiddenAnswer.Text = LearningUi.Text("HiddenAnswer"); RevealButton.Content = LearningUi.Text("Reveal"); SkipButton.Content = LearningUi.Text("Skip");
        ForgottenButton.Content = LearningUi.Text("Forgotten"); EffortfulButton.Content = LearningUi.Text("Effortful"); EasyButton.Content = LearningUi.Text("Easy");
        CloseButton.Content = LearningUi.Text("Close");
        System.Windows.Automation.AutomationProperties.SetName(ItemsList, LearningUi.Text("List"));
        System.Windows.Automation.AutomationProperties.SetName(ItemDetails, LearningUi.Text("Detail"));
        System.Windows.Automation.AutomationProperties.SetName(RecallContext, LearningUi.Text("Context"));
        if (_error is not null) Status.Text = LearningUi.Error(_error);
        else if (_statusKey.Length > 0) Status.Text = LearningUi.Text(_statusKey);
        RenderDetails(); RenderReview(); RefreshControls();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_operation is null) return;
        e.Cancel = true; _closeRequested = true; _operation.Cancel(); SetStatus("Cancelling");
    }
    private void SetStatus(string key)
    {
        _error = null; _statusKey = key; Status.Text = key.Length == 0 ? "" : LearningUi.Text(key);
        Status.SetResourceReference(TextBlock.ForegroundProperty,"TextFillColorSecondaryBrush");
    }
    private async Task<bool> RunAsync(Func<CancellationToken, Task> action, string success = "")
    {
        if (_operation is not null) return false;
        using var cancel = new CancellationTokenSource(); _operation = cancel; SetStatus("Working"); RefreshControls();
        var completed = false;
        try { await action(cancel.Token); completed = true; SetStatus(success); }
        catch (Exception error)
        {
            _error = error; _statusKey = ""; Status.Text = LearningUi.Error(error);
            Status.SetResourceReference(TextBlock.ForegroundProperty,"SystemFillColorCriticalBrush");
        }
        finally { _operation = null; RefreshControls(); }
        if (_closeRequested) Close();
        return completed;
    }

    internal Task<bool> ReloadAsync() => RunAsync(async token =>
    {
        var book = await _store.LoadAsync(token); _skipped.Clear(); ApplyBook(book);
    });
    private void ApplyBook(LearningBook book)
    {
        var selected = (ItemsList.SelectedItem as LearningCard)?.Id;
        _book = book; ItemsList.ItemsSource = book.Cards;
        ItemsList.SelectedItem = book.Cards.FirstOrDefault(c => c.Id == selected) ?? book.Cards.FirstOrDefault();
        _suppressLimit = true; LimitBox.SelectedItem = book.DailyLimit; _suppressLimit = false;
        NextReview(); RefreshControls();
    }
    private void RefreshControls()
    {
        var ready = _book is not null && _operation is null;
        NewButton.IsEnabled = ExportButton.IsEnabled = RestoreButton.IsEnabled = LimitBox.IsEnabled = ready;
        EditButton.IsEnabled = DeleteButton.IsEnabled = ready && ItemsList.SelectedItem is LearningCard;
        RefreshButton.IsEnabled = _operation is null; LearningTabs.IsEnabled = _operation is null;
        var canRate = ready && _reviewCard is not null && _revealed;
        ForgottenButton.IsEnabled = EffortfulButton.IsEnabled = EasyButton.IsEnabled = canRate;
        RevealButton.IsEnabled = ready && _reviewCard is not null && !_revealed;
        SkipButton.IsEnabled = ready && _reviewCard is not null;
        if (_book is not null) Summary.Text = LearningUi.Format("Summary",_book.Cards.Count,_book.Due(_now()).Count,_book.ReviewsToday(_now()),_book.DailyLimit);
    }
    private void Item_Changed(object sender, SelectionChangedEventArgs e) { RenderDetails(); RefreshControls(); }
    private void RenderDetails()
    {
        ItemDetails.Text = ItemsList.SelectedItem is LearningCard card ? Detail(card) : "";
    }
    private static string Detail(LearningCard card)
    {
        var c = card.Content;
        var text = new StringBuilder().AppendLine(c.Prompt).AppendLine().AppendLine(c.Answer).AppendLine();
        text.AppendLine($"{c.SourceLanguage} → {c.TargetLanguage} · {LearningUi.Text(c.Kind.ToString())} · {LearningUi.Text(c.Origin.ToString())}");
        if (c.Context.Length > 0) text.AppendLine().AppendLine(LearningUi.Text("Context")).AppendLine(c.Context);
        if (c.Note.Length > 0) text.AppendLine().AppendLine(LearningUi.Text("Note")).AppendLine(c.Note);
        text.AppendLine().Append(LearningUi.Format("Due",card.DueUtc.ToLocalTime().ToString("g"),card.ReviewCount));
        return text.ToString();
    }
    private void Tabs_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != LearningTabs || _book is null) return;
        // Each visit starts concealed; no old answer is retained across queue changes.
        NextReview();
    }
    private void NextReview()
    {
        _revealed = false;
        _reviewCard = _book is null || _book.ReviewsToday(_now()) >= _book.DailyLimit ? null :
            _book.Due(_now()).FirstOrDefault(c => !_skipped.Contains(c.Id));
        RenderReview(); RefreshControls();
    }
    private void RenderReview()
    {
        RecallPrompt.Text = _reviewCard?.Content.Prompt ?? LearningUi.Text(_book is not null && _book.ReviewsToday(_now()) >= _book.DailyLimit ? "LimitReached" : "NothingDue");
        RecallContext.Text = _reviewCard?.Content.Context ?? "";
        RecallContext.Visibility = RecallContext.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecallAnswer.Text = _revealed && _reviewCard is not null ? _reviewCard.Content.Answer + "\n\n" +
            LearningUi.Text(_reviewCard.Content.Origin.ToString()) +
            (_reviewCard.Content.Note.Length == 0 ? "" : "\n\n" + _reviewCard.Content.Note) : "";
        RecallAnswer.Visibility = _revealed ? Visibility.Visible : Visibility.Collapsed;
        HiddenAnswer.Visibility = !_revealed && _reviewCard is not null ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is not null || _reviewCard is null) return;
        _revealed = true; RenderReview(); RefreshControls();
    }
    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is not null || _reviewCard is null) return;
        _skipped.Add(_reviewCard.Id); NextReview();
    }
    private async void Rate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string rating } && Enum.TryParse<RecallRating>(rating,out var value)) await RateAsync(value);
    }
    internal Task<bool> RateAsync(RecallRating rating)
    {
        if (!_revealed || _reviewCard is null || _book is null) return Task.FromResult(false);
        var expected = _reviewCard; var snapshot = _book; var now = _now();
        return RunAsync(async token => ApplyBook(await _store.ChangeAsync(snapshot,b => b.Rate(expected,rating,now),token)),"Saved");
    }

    private void New_Click(object sender, RoutedEventArgs e) => OpenEditor(null);
    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is LearningCard card) OpenEditor(card);
    }
    internal void OpenEditor(LearningCard? card, LearningContent? seed = null)
    {
        if (_book is null || _operation is not null) return;
        var expected = _book;
        var initial = card?.Content ?? seed ?? new LearningContent(LearningKind.Phrase,"","","","","und","und",LearningOrigin.Personal);
        var editor = new LearningEditorWindow(initial,card is not null && initial.Context.Length > 0,async (content,token) =>
        {
            var now = _now();
            ApplyBook(await _store.ChangeAsync(expected,b => card is null ? b.Add(content,now) : b.Edit(card,content,now),token));
        }) { Owner = this };
        if (editor.ShowDialog() == true) SetStatus("Saved");
    }
    private async void Delete_Click(object sender, RoutedEventArgs e) => await DeleteSelectedAsync();
    internal Task<bool> DeleteSelectedAsync()
    {
        if (_book is null || _operation is not null || ItemsList.SelectedItem is not LearningCard card || !_confirm(LearningUi.Text("ConfirmDelete"))) return Task.FromResult(false);
        var snapshot = _book;
        return RunAsync(async token => ApplyBook(await _store.ChangeAsync(snapshot,b => b.Delete(card),token)),"Saved");
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ReloadAsync();
    private async void Limit_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLimit || _book is null || _operation is not null || LimitBox.SelectedItem is not int limit || limit == _book.DailyLimit) return;
        await SetLimitAsync(limit);
    }
    internal async Task<bool> SetLimitAsync(int limit)
    {
        if (_book is null) return false;
        var snapshot = _book;
        var saved = await RunAsync(async token => ApplyBook(await _store.ChangeAsync(snapshot,b => b.SetDailyLimit(limit),token)),"Saved");
        if (!saved) { _suppressLimit = true; LimitBox.SelectedItem = _book.DailyLimit; _suppressLimit = false; }
        return saved;
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter=LearningUi.Text("BackupFilter"), FileName="learning-backup.json", OverwritePrompt=true };
        if (dialog.ShowDialog(this) == true) await ExportToAsync(dialog.FileName,true);
    }
    internal Task<bool> ExportToAsync(string path, bool overwrite = false)
    {
        if (_book is null) return Task.FromResult(false);
        var snapshot = _book;
        return RunAsync(token => _store.ExportAsync(snapshot,path,overwrite,token),"Exported");
    }
    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter=LearningUi.Text("BackupFilter") };
        if (dialog.ShowDialog(this) == true) await RestoreFromAsync(dialog.FileName);
    }
    internal async Task<bool> RestoreFromAsync(string path)
    {
        if (_book is null) return false;
        var snapshot = _book; var restored = false;
        var completed = await RunAsync(async token =>
        {
            var captured = await _store.ReadBackupAsync(path,token);
            token.ThrowIfCancellationRequested();
            if (!_confirm(LearningUi.Format("ConfirmRestore",captured.Cards.Count,captured.DailyLimit,snapshot.Cards.Count))) return;
            ApplyBook(await _store.RestoreAsync(snapshot,captured,token)); restored = true;
        });
        if (completed) SetStatus(restored ? "Restored" : "Cancelled");
        return restored;
    }
}
