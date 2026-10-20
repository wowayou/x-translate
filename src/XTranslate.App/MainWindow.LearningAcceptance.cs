using System.Diagnostics;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using XTranslate.App.Learning;
using XTranslate.Core.Learning;

namespace XTranslate.App;

public partial class MainWindow
{
    // Controlled modal windows and local fixtures only. No user files, clipboard, network, or OS settings.
    private async Task RunLearningAcceptanceAsync(List<string> checks, string output, Func<int> requests)
    {
        var root = Path.Combine(Path.GetDirectoryName(output)!,"learning-fixture");
        Directory.CreateDirectory(root);
        var initialLanguage = UiLocalization.Preference; var initialTheme = UiAppearance.Preference;
        var count = requests();
        var now = new DateTimeOffset(2026,10,21,12,0,0,TimeSpan.Zero);
        var seed = new LearningContent(LearningKind.Phrase,"account for","把……考虑在内","We need to account for latency.","personal note","en","zh-CN",LearningOrigin.Personal);
        try
        {
            var collectData = Path.Combine(root,"collection-data"); var collectStore = new LearningStore(collectData);
            SourceBox.Text = "We need to account for latency.";
            ResultBox.Text = "我们需要把延迟考虑在内。"; CopyButton.IsEnabled = true; _resultSource = "en";
            SourceBox.Select(11,11); // account for, not an inferred word/translation alignment.
            var draft = LearningSeed();
            RequireStream(draft.Prompt == "account for" && draft.Answer == "" && draft.Origin == LearningOrigin.Personal,
                "A paragraph translation was presented as a selected phrase's definition");
            await WithLearningModalAsync<LearningEditorWindow>(() => OpenLearning(true,collectStore),async editor =>
            {
                var owner = (LearningWindow)editor.Owner;
                RequireStream(_settingsOpen && !ShouldAutoHide(false) && editor.PromptBox.Text == "account for" &&
                    editor.ContextBox.Text == SourceBox.Text && editor.IncludeContextBox.IsChecked == false,
                    "Collection lost context opt-in or modal/capture boundary");
                RequireStream(!Directory.Exists(collectData),"Opening collection persisted personal content");
                var peer = new ButtonAutomationPeer(editor.CancelButton);
                ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
                await Task.Delay(30); owner.Close();
            });
            RequireStream(!Directory.Exists(collectData) && !_settingsOpen && SourceBox.Text == "We need to account for latency.","Cancel wrote learning data or changed source");
            checks.Add("Manual collection preserves selected text, never assumes paragraph-to-word alignment, excludes context by default, and Cancel writes nothing");

            await WithLearningModalAsync<LearningEditorWindow>(() => OpenLearning(true,collectStore),async editor =>
            {
                var owner = (LearningWindow)editor.Owner;
                RequireStream(!await editor.SaveAsync() && editor.IsVisible && editor.AnswerBox.Text == "", "Missing answer was saved or closed the draft");
                editor.AnswerBox.Text = "把……考虑在内"; editor.NoteBox.Text = "own fixture note";
                var before = editor.PromptBox.Text;
                foreach (var language in new[] {UiLanguage.English,UiLanguage.SimplifiedChinese})
                {
                    UiLocalization.SetPreference(language);
                    RequireStream(editor.PromptBox.Text == before && editor.IncludeContextBox.IsChecked == false,"Relocalization changed editable collection");
                }
                editor.Width = editor.MinWidth; editor.Height = editor.MinHeight; editor.UpdateLayout();
                RequireStream(editor.SaveButton.ActualHeight >= 30 && editor.AnswerBox.ActualWidth > 100,"Minimum learning editor lost its fields/actions");
                RenderWindowTo(editor,Path.ChangeExtension(output,".learning-editor.zh.png"));
                RequireStream(await editor.SaveAsync(),"Confirmed collection failed"); owner.Close();
            });
            var collected = await collectStore.LoadAsync();
            RequireStream(collected.Cards.Count == 1 && collected.Cards[0].Content.Context == "" && collected.Cards[0].Content.Prompt == "account for" &&
                collected.Cards[0].Content.Note == "own fixture note","Collection saved unconfirmed context or lost explicit content");
            checks.Add("Real collection editor validates required answers, retains drafts across language changes, and saves only explicitly confirmed fields");

            var store = new LearningStore(Path.Combine(root,"book-data"));
            var book = await store.ChangeAsync(LearningBook.Empty,b => b.Add(seed,now.AddSeconds(-1)).Add(seed with {Prompt="take into account",Answer="考虑到"},now));
            var allow = false;
            await WithLearningModalAsync<LearningWindow>(() => new LearningWindow(store,() => now,_ => allow) {Owner=this}.ShowDialog(),async window =>
            {
                await window.Loading;
                RequireStream(window.Book?.Cards.Count == 2,"Notebook failed to load");
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(LearningJson.Write(window.Book!)));
                RequireStream(!await window.DeleteSelectedAsync() && window.Book!.Cards.Count == 2,"Delete ignored confirmation");
                window.LearningTabs.SelectedItem = window.ReviewTab;
                RequireStream(window.RecallAnswer.Text == "" && window.RecallAnswer.Visibility == Visibility.Collapsed && !window.EasyButton.IsEnabled &&
                    !await window.RateAsync(RecallRating.Easy),"Answer or rating available before explicit reveal");
                RequireStream(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(LearningJson.Write(await store.LoadAsync()))) == hash,
                    "Unrevealed rating wrote data");
                window.SkipButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                RequireStream(window.RecallAnswer.Text == "" && window.RecallPrompt.Text == "take into account","Skip exposed an answer or changed scheduling");
                await window.ReloadAsync();
                RequireStream(await window.SetLimitAsync(1),"Daily limit save failed");
                window.RevealButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                RequireStream(window.RecallAnswer.Text.Contains("把……考虑在内") && window.EasyButton.IsEnabled,"Reveal did not enable self-rating");
                RequireStream(await window.RateAsync(RecallRating.Easy) && window.Book!.ReviewsToday(now) == 1 && window.Book.Cards[0].DueUtc == now.AddDays(3),
                    "Rating did not persist the expected interval");
                RequireStream(!window.EasyButton.IsEnabled && window.RecallAnswer.Text == "" && window.RecallPrompt.Text == LearningUi.Text("LimitReached"),
                    "Daily limit added more tasks or retained old answer");
                window.Close();
            });
            RequireStream((await new LearningStore(Path.Combine(root,"book-data")).LoadAsync()).ReviewsToday(now) == 1,"Review state did not survive reopening");
            checks.Add("Notebook recall hides answer and rating until reveal; skip is unsaved, self-rating persists UTC schedule, and the daily limit stops extra tasks");

            var current = await store.LoadAsync();
            await WithLearningModalAsync<LearningWindow>(() => new LearningWindow(store,() => now,_ => allow) {Owner=this}.ShowDialog(),async window =>
            {
                await window.Loading;
                var backup = Path.Combine(root,"backup.json");
                RequireStream(await window.ExportToAsync(backup),"UI backup export failed");
                RequireStream(!await window.RestoreFromAsync(backup) && window.Book!.Generation == current.Generation,"Restore ignored cancellation");
                allow = true;
                RequireStream(await window.DeleteSelectedAsync() && window.Book!.Cards.Count == 1,"Confirmed delete did not persist");
                RequireStream(await window.RestoreFromAsync(backup) && window.Book!.Cards.Count == 2 && window.Book.ReviewsToday(now) == 1,
                    "Backup restore lost personal data or recall state");
                var restoredBook = window.Book ?? throw new Exception("Restored notebook missing from UI");
                var restored = restoredBook.Generation;
                var invalid = Path.Combine(root,"bad-backup.json"); await File.WriteAllTextAsync(invalid,"private_learning_marker");
                RequireStream(!await window.RestoreFromAsync(invalid) && window.Book!.Generation == restored && !window.Status.Text.Contains("private_learning_marker"),
                    "Invalid backup leaked or replaced notebook");
                RequireStream(!await window.ExportToAsync(Path.Combine(root,"book-data","settings.json"),true),"Export overwrote managed data");
                window.Close();
            });
            checks.Add("Notebook backup/export/delete/restore use explicit confirmation; invalid backups and managed destinations fail without losing data");

            var snapshot = await store.LoadAsync();
            var diskPath = Path.Combine(root,"book-data","learning","book.json");
            await WithLearningModalAsync<LearningEditorWindow>(() => new LearningEditorWindow(seed,false,async (content,token) =>
                { snapshot = await store.ChangeAsync(snapshot,b => b.Add(content,now),token); }) {Owner=this}.ShowDialog(),async editor =>
            {
                editor.PromptBox.Text = "retained draft"; editor.ContextBox.Text = "explicit context"; editor.IncludeContextBox.IsChecked = true;
                using (var held = new FileStream(diskPath,FileMode.Open,FileAccess.Read,FileShare.Read))
                {
                    RequireStream(!await editor.SaveAsync() && editor.IsVisible && editor.PromptBox.Text == "retained draft" &&
                        editor.ContextBox.Text == "explicit context" && snapshot.Cards.Count == 2,"I/O failure lost editor draft or claimed save");
                }
                RequireStream(await editor.SaveAsync(),"Manual save retry failed after releasing file lock");
            });
            RequireStream((await store.LoadAsync()).Cards.Any(c => c.Content.Prompt == "retained draft" && c.Content.Context == "explicit context"),"Explicit context was not saved on retry");
            checks.Add("Locked-file save keeps the real editor and full draft open; manual retry persists explicitly selected context without duplication");

            await WithLearningModalAsync<LearningWindow>(() => new LearningWindow(store,() => now,_ => true) {Owner=this}.ShowDialog(),async window =>
            {
                await window.Loading;
                var old = window.Book!;
                await store.ChangeAsync(old,b => b.Add(seed with {Prompt="external update"},now));
                RequireStream(!await window.DeleteSelectedAsync() && window.Book!.Generation == old.Generation,"Stale UI silently overwrote newer notebook");
                await window.ReloadAsync();
                RequireStream(window.Book!.Cards.Any(c => c.Content.Prompt == "external update"),"Refresh failed after conflict");
                foreach (var language in new[] { UiLanguage.SimplifiedChinese,UiLanguage.English })
                {
                    UiLocalization.SetPreference(language); UiAppearance.Apply(language == UiLanguage.English ? UiTheme.Dark : UiTheme.Light);
                    window.Width = window.MinWidth; window.Height = window.MinHeight; window.UpdateLayout();
                    RequireStream(window.NewButton.ActualHeight >= 30 && window.ItemsList.ActualHeight > 60 && window.ItemDetails.ActualWidth > 100,
                        "Minimum notebook layout lost controls");
                    RenderWindowTo(window,Path.ChangeExtension(output,language == UiLanguage.English ? ".learning-book.en.dark.png" : ".learning-book.zh.light.png"));
                    window.LearningTabs.SelectedItem = window.ReviewTab; window.UpdateLayout();
                    RequireStream(window.RecallAnswer.Text == "", "Language/theme/tab switch revealed an answer");
                    RenderWindowTo(window,Path.ChangeExtension(output,language == UiLanguage.English ? ".learning-review.en.dark.png" : ".learning-review.zh.light.png"));
                    window.LearningTabs.SelectedItem = window.LibraryTab;
                }
                window.Close();
            });
            checks.Add("Stale notebook UI reports conflict and refreshes explicitly; zh/light and en/dark minimum library/review layouts render without revealing answers");

            var editStore = new LearningStore(Path.Combine(root,"edit-data"));
            var editBook = await editStore.ChangeAsync(LearningBook.Empty,b => b.Add(seed,now));
            await editStore.ChangeAsync(editBook,b => b.Rate(editBook.Cards[0],RecallRating.Easy,now));
            await WithLearningModalAsync<LearningWindow>(() => new LearningWindow(editStore,() => now) {Owner=this}.ShowDialog(),async window =>
            {
                await window.Loading;
                var card = window.Book!.Cards.Single();
                await WithLearningModalAsync<LearningEditorWindow>(() => window.OpenEditor(card),async editor =>
                {
                    RequireStream(editor.IncludeContextBox.IsChecked == true && editor.ContextBox.Text == seed.Context,"Edit lost saved context");
                    editor.NoteBox.Text = "edited note";
                    RequireStream(await editor.SaveAsync(),"Note edit did not save");
                });
                var noted = window.Book!.Cards.Single();
                RequireStream(noted.Id == card.Id && noted.ReviewCount == 1 && noted.DueUtc == card.DueUtc && noted.Content.Note == "edited note",
                    "Note edit reset recall or created another card");
                await WithLearningModalAsync<LearningEditorWindow>(() => window.OpenEditor(noted),async editor =>
                {
                    editor.AnswerBox.Text = "revised reference"; editor.IncludeContextBox.IsChecked = false;
                    RequireStream(await editor.SaveAsync(),"Material edit failed");
                });
                var edited = window.Book!.Cards.Single();
                RequireStream(edited.Id == card.Id && edited.Content.Answer == "revised reference" && edited.Content.Context == "" && edited.ReviewCount == 0,
                    "Material/context edit failed to reset recall");
                window.Close();
            });
            checks.Add("Real notebook edit preserves identity and note-only recall; changing reference/context resets recall and explicitly removes unchecked context");

            var cancelRoot = Path.Combine(root,"cancel-data");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var release = new ManualResetEventSlim())
            {
                var cancelStore = new LearningStore(cancelRoot,_ =>
                {
                    entered.TrySetResult();
                    if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Cancelled save fixture stalled");
                });
                await WithLearningModalAsync<LearningEditorWindow>(() => new LearningEditorWindow(seed,false,
                    (content,token) => cancelStore.ChangeAsync(LearningBook.Empty,b => b.Add(content,now),token)) {Owner=this}.ShowDialog(),async editor =>
                {
                    var saving = editor.SaveAsync();
                    try
                    {
                        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                        editor.Close();
                        RequireStream(editor.IsVisible && !editor.SaveButton.IsEnabled,"Busy editor closed before cancellation completed");
                    }
                    finally { release.Set(); }
                    RequireStream(!await saving && !editor.IsVisible,"Pre-publication cancellation claimed save or kept closing pending");
                });
            }
            RequireStream(!File.Exists(Path.Combine(cancelRoot,"learning","book.json")) &&
                !Directory.EnumerateFiles(cancelRoot,"*.tmp",SearchOption.AllDirectories).Any(),"Cancelled save published data or leaked staging");
            checks.Add("Closing during a real pre-publication save waits for cancellation, writes no notebook, and cleans temporary content");

            await WithLearningModalAsync<LearningEditorWindow>(() => new LearningEditorWindow(seed,false,async (_,token) =>
                { await Task.Delay(30); throw new IOException("private_failure_marker"); }) {Owner=this}.ShowDialog(),async editor =>
            {
                editor.PromptBox.Text = "close race draft";
                var saving = editor.SaveAsync(); editor.Close();
                RequireStream(!await saving && editor.IsVisible && editor.PromptBox.Text == "close race draft" && editor.SaveButton.IsEnabled &&
                    !editor.Status.Text.Contains("private_failure_marker"),"Concurrent close/I/O failure discarded draft or exposed exception");
                editor.Close();
            });
            var lateStore = new LearningStore(Path.Combine(root,"late-save-data"));
            LearningEditorWindow? lateEditor = null;
            await WithLearningModalAsync<LearningEditorWindow>(() =>
            {
                lateEditor = new LearningEditorWindow(seed,false,async (content,token) =>
                {
                    await lateStore.ChangeAsync(LearningBook.Empty,b => b.Add(content,now),token);
                    lateEditor!.Close(); // Cancellation after the atomic publication must still report success.
                }) {Owner=this};
                lateEditor.ShowDialog();
            },async editor => RequireStream(await editor.SaveAsync() && editor.DialogResult == true,"Late cancellation disguised published save as failure"));
            RequireStream((await lateStore.LoadAsync()).Cards.Count == 1,"Late cancellation lost the published item");
            checks.Add("Close plus I/O failure retains the editable draft; cancellation after atomic publication still reports a saved item");

            RequireStream(requests() == count && !_settingsOpen,"Offline learning UI issued HTTP or retained modal capture suppression");
            checks.Add("All notebook collection, editing, backup, restore, queue and self-rating actions run with zero HTTP and no automatic translation history");
        }
        finally
        {
            UiLocalization.SetPreference(initialLanguage); UiAppearance.Apply(initialTheme); SourceBox.Clear();
        }
    }

    private async Task WithLearningModalAsync<T>(Action show, Func<T,Task> action) where T : Window
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? failure = null;
        var timer = new DispatcherTimer(DispatcherPriority.Send,Dispatcher) { Interval=TimeSpan.FromSeconds(12) };
        void CloseFixtures()
        {
            foreach (var child in Application.Current.Windows.Cast<Window>().Where(w => w is LearningEditorWindow or LearningWindow).Reverse().ToArray()) child.Close();
        }
        timer.Tick += (_,_) => { timer.Stop(); failure ??= new TimeoutException("Learning modal acceptance exceeded 12 seconds"); CloseFixtures(); completion.TrySetResult(); };
        // Post into the nested ShowDialog dispatcher; awaiting it before show() would deadlock the fixture.
        _ = Dispatcher.BeginInvoke(new Action(async () =>
        {
            T? window = null;
            try
            {
                var watch = Stopwatch.StartNew();
                while (window is null)
                {
                    window = Application.Current.Windows.OfType<T>().FirstOrDefault(w => w.IsVisible && w.IsLoaded);
                    if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Learning fixture did not open");
                    if (window is null) await Task.Delay(15);
                }
                await action(window);
            }
            catch (Exception error) { failure = error; }
            finally
            {
                if (failure is not null) CloseFixtures();
                else window?.Close();
                completion.TrySetResult();
            }
        }));
        timer.Start();
        try { show(); await completion.Task; }
        finally { timer.Stop(); }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
