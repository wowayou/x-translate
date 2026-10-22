using System.Windows;
using System.Windows.Controls;
using XTranslate.Core;

namespace XTranslate.App;

public partial class MainWindow
{
    private async Task RunUpdatePreparationAcceptanceAsync(List<string> checks)
    {
        if (!AppUpdates.CanSelfUpdate) return; // No development self-update policy bypass.
        var fixture = Path.Combine(_storage.DirectoryPath, "update-prepare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var zip = Path.Combine(fixture, "candidate.zip");
        var stagesBefore = Directory.EnumerateDirectories(AppUpdates.InstallDirectory, ".update-*").Order().ToArray();
        using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("XTranslate.exe").Open())) writer.Write("fixed version fixture");
            if (AppUpdates.Flavor == UpdateFlavor.Lite)
            {
                using (var writer = new StreamWriter(archive.CreateEntry("XTranslate.dll").Open())) writer.Write("fixed");
                using var configWriter = new StreamWriter(archive.CreateEntry("XTranslate.runtimeconfig.json").Open());
                configWriter.Write("{\"runtimeOptions\":{\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.0\"}}}");
            }
        }
        var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<UpdateVersion>? pending = null;
        try
        {
            using var cancel = new CancellationTokenSource();
            pending = AppUpdates.PrepareFixturePackageAsync(zip, cancel.Token, _ =>
            {
                readEntered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
                return "99.0.0";
            });
            await readEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            try
            {
                await AppUpdates.PreparePackageAsync(zip, null, CancellationToken.None);
                throw new Exception("Concurrent preparation was accepted");
            }
            catch (UpdateException error) { RequireUx(error.Code == UpdateErrorCode.Apply, "Wrong preparation concurrency error"); }
            cancel.Cancel(); release.TrySetResult();
            try { await pending; throw new Exception("Cancelled prepared update was admitted"); }
            catch (OperationCanceledException) { }
            RequireUx(!AppUpdates.HasPrepared && !AppUpdates.WorkerStarted &&
                stagesBefore.SequenceEqual(Directory.EnumerateDirectories(AppUpdates.InstallDirectory, ".update-*").Order()),
                "Cancelled preparation retained an active plan/worker or staging directory");
            checks.Add("One preparing update at a time; cancellation at verified staging cleans only that stage and never activates a late plan or worker");

            var invalidStage = Path.Combine(AppUpdates.InstallDirectory, ".update-00000000");
            try { AppUpdates.StartFixturePrepared(invalidStage, Path.Combine(invalidStage, ".update-worker")); throw new Exception("Missing prepared worker admitted"); }
            catch (UpdateException) { }
            RequireUx(!AppUpdates.HasPrepared && !AppUpdates.WorkerStarted, "Failed handoff retained a prepared plan or prevents normal exit");
            checks.Add("Missing prepared worker fails closed and revokes the update instead of trapping normal Exit or silently retrying");
            await RunSettingsDialogAsync(dialog =>
            {
                ((TabControl)dialog.FindName("SectionTabs")).SelectedItem = dialog.FindName("AboutTab");
                RequireUx(!dialog.UpdateApplied, "Cancelled package became a settings restart action");
                CancelStreamingSettingsDialog(dialog);
            });
        }
        finally
        {
            release.TrySetResult();
            if (pending is not null)
            {
                try { await pending.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (OperationCanceledException) { }
            }
            Directory.Delete(fixture, recursive: true);
        }
    }
}
