using System.Diagnostics;
using System.Windows;
using XTranslate.Core;

namespace XTranslate.App;

internal static class UpdateWorker
{
    // Runs from the verified staging directory before ordinary windows, tray, settings or OCR are created.
    internal static async Task<int> FinishAsync(string[] args)
    {
        var workerDirectory = AppUpdates.InstallDirectory;
        var stage = Directory.GetParent(workerDirectory)?.FullName ?? "";
        var install = Directory.GetParent(stage)?.FullName ?? "";
        var report = Path.GetFullPath(args.Length == 6 && args[3] != "-" ? args[3] : Path.Combine(install, AppUpdates.ResultName));
        var failurePath = Path.Combine(workerDirectory, "worker-failure.json");
        if (Path.GetFileName(workerDirectory) != ".update-worker") return 2;
        try { return await FinishCoreAsync(args); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Last-resort safe failure evidence. A bad event/IO boundary must not kill the WPF
            // Dispatcher without a receipt or leak user content through an exception body.
            var failure = new { success = false, phase = "complete", code = "Apply", boundary = "unexpected-worker", errorType = error.GetType().Name };
            try { AppUpdates.WriteReport(report, failure); AppUpdates.WriteReport(failurePath, failure); }
            catch (Exception write) when (write is IOException or UnauthorizedAccessException) { }
            return 1;
        }
    }

    private static async Task<int> FinishCoreAsync(string[] args)
    {
        var workerDirectory = AppUpdates.InstallDirectory;
        var stage = Directory.GetParent(workerDirectory)?.FullName ?? "";
        var install = Directory.GetParent(stage)?.FullName ?? "";
        var requestedReport = args.Length == 6 && args[3] != "-" ? args[3] : "";
        var resultPath = Path.Combine(install, AppUpdates.ResultName);
        PreparedUpdate.Plan? plan = null;
        var success = false;
        var errorCode = "Apply";
        var restart = false;
        var parentExitConfirmed = false;
        var ownsInstallMutex = false;
        var boundary = "validation";
        string? errorType = null;
        try
        {
            if (args.Length != 6 || args[0] != "--finish-update" || !int.TryParse(args[1], out var parentId) || parentId <= 0 ||
                !long.TryParse(args[2], out var parentStartTicks) || args[4] is not "restart" and not "exit" ||
                !args[5].StartsWith("Local\\XTranslate.UpdateReady.", StringComparison.Ordinal) || AppUpdates.Flavor is not UpdateFlavor flavor)
                throw new UpdateException("Invalid updater arguments.", UpdateErrorCode.Package);
            if (Path.GetFileName(workerDirectory) != ".update-worker")
                throw new UpdateException("Updater directory mismatch.", UpdateErrorCode.Package);
            restart = args[4] == "restart";
            plan = PreparedUpdate.Read(stage, install);
            if (plan.To != AppUpdates.Current.ToString() || plan.Flavor != flavor) throw new UpdateException("Updater identity mismatch.", UpdateErrorCode.Package);
            // Obtain the parent handle before waiting. Never replace while it is alive or after a bad exit.
            // A reused PID/start time or an unrelated executable cannot authorize a transaction.
            boundary = "parent-handoff";
            using (var parent = Process.GetProcessById(parentId))
            {
                _ = parent.Handle; // Retain the actual parent handle before signaling; PID lookup is not lifetime ownership.
                if (parentId == Environment.ProcessId || parent.StartTime.ToUniversalTime().Ticks != parentStartTicks ||
                    !string.Equals(parent.MainModule?.FileName, Path.Combine(install, "XTranslate.exe"), StringComparison.OrdinalIgnoreCase))
                    throw new UpdateException("Updater parent identity mismatch.", UpdateErrorCode.Apply);
                using var allow = EventWaitHandle.OpenExisting(args[5] + ".Apply");
                using (var ready = EventWaitHandle.OpenExisting(args[5])) ready.Set();
                boundary = "wait-parent";
                var exited = await Task.Run(() => parent.WaitForExit(15_000));
                if (!exited || parent.ExitCode != 0 || !allow.WaitOne(0)) throw new UpdateException("The previous process did not exit normally.", UpdateErrorCode.Apply);
                parentExitConfirmed = true;
            }
            // The normal mutex also prevents any new application process from starting mid-swap. It is
            // owned/released on this Dispatcher thread; only file work moves onto the background pool.
            boundary = "mutex";
            if (!((App)Application.Current).TryOwnUpdateMutex())
                throw new UpdateException("An application instance is running.", UpdateErrorCode.Apply);
            ownsInstallMutex = true;
            AppUpdates.WriteReport(resultPath, new { success = false, phase = "applying", from = plan.From, to = plan.To });
            boundary = "copy-and-swap";
            await Task.Run(() => PreparedUpdate.Apply(stage, install, AppUpdates.Current, flavor,
                AppUpdates.ReadProductVersion, AppUpdates.FrameworkAvailable));
            success = true;
        }
        catch (UpdateException error) { errorCode = error.Code.ToString(); errorType = error.GetType().Name; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception or WaitHandleCannotBeOpenedException)
        { errorCode = "Apply"; errorType = error.GetType().Name; }
        var result = new
        {
            success, phase = "complete", from = plan?.From, to = plan?.To, code = success ? null : errorCode,
            updaterProcessId = Environment.ProcessId, stagingDirectory = stage, boundary, errorType,
            cleanupPending = true
        };
        // Preserve completion before attempting UI startup. An interrupted worker retains its stage,
        // while failed installation never discards .old/payload recovery bytes.
        if (plan is not null) AppUpdates.WriteReport(resultPath, result);
        if (requestedReport.Length > 0) AppUpdates.WriteReport(Path.GetFullPath(requestedReport), result);
        if (restart && parentExitConfirmed && ownsInstallMutex && errorCode != UpdateErrorCode.RollbackIncomplete.ToString())
        {
            try
            {
                using var replacement = Process.Start(new ProcessStartInfo(Path.Combine(install, "XTranslate.exe"))
                {
                    UseShellExecute = false,
                    WorkingDirectory = install,
                    ArgumentList = { "--after-update", Environment.ProcessId.ToString() }
                }) ?? throw new IOException("Could not start the application after update.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
            {
                var failedStart = new { success = false, phase = "complete", code = "Restart", from = plan?.From, to = plan?.To,
                    updaterProcessId = Environment.ProcessId, stagingDirectory = stage, boundary = "restart", errorType = error.GetType().Name };
                AppUpdates.WriteReport(resultPath, failedStart);
                if (requestedReport.Length > 0) AppUpdates.WriteReport(Path.GetFullPath(requestedReport), failedStart);
                return 1;
            }
        }
        return success ? 0 : 1;
    }
}
