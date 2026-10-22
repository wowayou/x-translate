using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using XTranslate.Core;

namespace XTranslate.App;

/// <summary>Only verified staging changes while the application runs; a staged worker swaps files after exit.</summary>
internal static class AppUpdates
{
    public const string ResultName = ".update-result.json";
    public static UpdateVersion Current { get; } = ReadCurrent();
    public static UpdateFlavor? Flavor { get; } = ReadFlavor();
    public static string InstallDirectory => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    public static bool CanSelfUpdate => Flavor is not null;
    public static bool HasPrepared => _prepared is not null;
    public static bool PreparationFailed { get; private set; }
    public static bool WorkerStarted { get; private set; }
    public static bool StartupUpdateFailed { get; private set; }
    private static string? _prepared;
    private static string? _workerDirectory;
    private static bool _preparing;

    private static UpdateVersion ReadCurrent()
    {
        var text = typeof(AppUpdates).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return UpdateVersion.TryParse(text, out var version) ? version : new UpdateVersion(0, 0, 0);
    }

    private static UpdateFlavor? ReadFlavor()
    {
        var value = typeof(AppUpdates).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "UpdateFlavor")?.Value;
        return Enum.TryParse<UpdateFlavor>(value, out var flavor) ? flavor : null;
    }

    public static string? ReadProductVersion(string exe) => FileVersionInfo.GetVersionInfo(exe).ProductVersion;

    public static string? ReadSidecarSha256(string zip)
    {
        var path = zip + ".sha256";
        if (!File.Exists(path)) return null;
        var token = File.ReadAllText(path).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return token.Length == 64 && token.All(Uri.IsHexDigit) ? token : throw new UpdateException("Invalid .sha256 file.", UpdateErrorCode.Integrity);
    }

    /// <summary>Verifies and seals a package, but never replaces an active process's EXE or DLLs.</summary>
    public static Task<UpdateVersion> PreparePackageAsync(string zip, string? sha256, CancellationToken token = default)
        => PreparePackageCoreAsync(zip, sha256, token, ReadProductVersion);

    internal static Task<UpdateVersion> PrepareFixturePackageAsync(string zip, CancellationToken token, Func<string, string?> versionReader)
    {
        if (!App.IsSmokeTest) throw new InvalidOperationException("Fixture version reader is smoke-only.");
        return PreparePackageCoreAsync(zip, null, token, versionReader);
    }

    private static async Task<UpdateVersion> PreparePackageCoreAsync(string zip, string? sha256, CancellationToken token, Func<string, string?> versionReader)
    {
        token.ThrowIfCancellationRequested();
        if (Flavor is not UpdateFlavor flavor) throw new UpdateException("Development builds do not self-update.", UpdateErrorCode.Package);
        if (HasPrepared || StartupUpdateFailed || PreparationFailed || _preparing) throw new UpdateException("An earlier update is prepared or needs recovery.", UpdateErrorCode.Apply);
        _preparing = true;
        (string staging, UpdateVersion candidate, string worker) prepared;
        try
        {
            prepared = await Task.Run(() =>
            {
                if (sha256 is not null) UpdateInstaller.VerifySha256(zip, sha256);
                var candidate = Current;
                string? InspectVersion(string path)
                {
                    var metadata = versionReader(path);
                    if (UpdateVersion.TryParse(metadata, out var version)) candidate = version;
                    return metadata;
                }
                var staging = UpdateInstaller.Stage(zip, InstallDirectory, flavor, Current, InspectVersion, FrameworkAvailable);
                string worker;
                try
                {
                    PreparedUpdate.Seal(staging, Current, candidate, flavor);
                    token.ThrowIfCancellationRequested();
                    worker = PreparedUpdate.CopyWorker(staging, InstallDirectory);
                    token.ThrowIfCancellationRequested();
                }
                catch { Directory.Delete(staging, recursive: true); throw; }
                return (staging, candidate, worker);
            });
            if (token.IsCancellationRequested)
            {
                Directory.Delete(prepared.staging, recursive: true);
                token.ThrowIfCancellationRequested();
            }
            _prepared = prepared.staging;
            _workerDirectory = prepared.worker;
            return prepared.candidate;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new UpdateException("Could not write the prepared update; active program files were not changed.", UpdateErrorCode.ReadOnly); }
        finally { _preparing = false; }
    }

    public static bool FrameworkAvailable(string name, Version needed)
    {
        var runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar);
        var shared = Directory.GetParent(runtime)?.Parent?.FullName;
        if (shared is null || !Directory.Exists(Path.Combine(shared, name))) return false;
        return Directory.EnumerateDirectories(Path.Combine(shared, name))
            .Select(path => Version.TryParse(Path.GetFileName(path).Split('-')[0], out var installed) ? installed : null)
            .Any(installed => installed is not null && installed.Major == needed.Major && installed >= needed);
    }

    public static async Task<object> UpdateOnlineAsync(CancellationToken token)
    {
        if (Flavor is not UpdateFlavor flavor) throw new UpdateException("Development builds do not self-update.", UpdateErrorCode.Package);
        using var client = new UpdateClient();
        var release = await client.GetLatestAsync(flavor, Current, token);
        if (!(release.Version > Current)) return new { success = true, updated = false, latest = release.Version.ToString() };
        var zip = Path.Combine(Path.GetTempPath(), "xtranslate-" + Guid.NewGuid().ToString("N")[..8] + ".zip");
        UpdateVersion prepared;
        try
        {
            await client.DownloadAsync(release, Current, zip, null, token);
            prepared = await PreparePackageAsync(zip, release.Sha256, token);
        }
        finally { try { File.Delete(zip); } catch (IOException) { } }
        return new { success = true, prepared = true, latest = release.Version.ToString(), to = prepared.ToString() };
    }

    /// <summary>
    /// A successful preparing parent exits; the staged worker owns the final report/exit code. Script callers
    /// must wait for phase=complete and that report's owned updater PID, not treat the parent's exit as install success.
    /// </summary>
    public static async Task<int> HeadlessAsync(string report, Func<Task<object>> work)
    {
        if (File.Exists(report)) return 2; // Never let a stale receipt serve as this invocation's result.
        try
        {
            var result = await work();
            if (HasPrepared) StartPrepared(restart: false, report);
            else WriteReport(report, result);
            return 0;
        }
        catch (UpdateException error)
        {
            WriteReport(report, new { success = false, phase = "complete", code = error.Code.ToString(), status = error.StatusCode });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            WriteReport(report, new { success = false, phase = "complete", code = error is OperationCanceledException ? "Timeout" : "Apply" });
        }
        return 1;
    }

    public static void StartReplacement() => StartPrepared(restart: true);

    public static void StartPrepared(bool restart, string? report = null)
    {
        if (_prepared is null || _workerDirectory is null || WorkerStarted) throw new UpdateException("No unstarted prepared update is available.", UpdateErrorCode.Apply);
        try { StartPreparedCore(_prepared, _workerDirectory, restart, report); WorkerStarted = true; }
        catch
        {
            // A failed handoff revokes its authorization. The old instance stays open with an error,
            // but a later normal Exit cannot silently retry or become permanently blocked by it.
            _prepared = null; _workerDirectory = null; PreparationFailed = true;
            throw;
        }
    }

    private static void StartPreparedCore(string prepared, string workerDirectory, bool restart, string? report)
    {
        PreparedUpdate.VerifyWorker(prepared, InstallDirectory);
        var readyName = "Local\\XTranslate.UpdateReady." + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
        using var allow = new EventWaitHandle(false, EventResetMode.ManualReset, readyName + ".Apply");
        using var parent = Process.GetCurrentProcess();
        var info = new ProcessStartInfo(Path.Combine(workerDirectory, "XTranslate.exe"))
        {
            UseShellExecute = false,
            WorkingDirectory = workerDirectory,
            ArgumentList = { "--finish-update", Environment.ProcessId.ToString(), parent.StartTime.ToUniversalTime().Ticks.ToString(),
                report ?? "-", restart ? "restart" : "exit", readyName }
        };
        using var worker = Process.Start(info) ?? throw new IOException("Could not start the prepared updater.");
        try
        {
            if (!ready.WaitOne(TimeSpan.FromSeconds(5)))
                throw new UpdateException("The prepared updater did not acquire the parent handle.", UpdateErrorCode.Apply);
            var waiting = new { success = false, phase = "waiting-parent", updaterProcessId = worker.Id, stagingDirectory = prepared };
            // Receipt precedes authorization. A same-path launch must not erase the staged worker
            // in the gap between the old process releasing its mutex and the worker acquiring it.
            WriteReport(Path.Combine(InstallDirectory, ResultName), waiting);
            if (report is not null) WriteReport(report, waiting);
            allow.Set();
        }
        catch
        {
            if (!worker.HasExited) { worker.Kill(); worker.WaitForExit(5000); }
            throw;
        }
    }

    internal static void StartFixturePrepared(string stage, string worker)
    {
        if (!App.IsSmokeTest || HasPrepared) throw new InvalidOperationException("Fixture preparation is smoke-only.");
        _prepared = stage; _workerDirectory = worker;
        try { StartPrepared(restart: false); }
        finally { PreparationFailed = false; }
    }

    public static bool WaitForPrevious(string[] args)
    {
        var index = Array.IndexOf(args, "--after-update");
        if (index < 0) return true;
        if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out var pid) || pid <= 0 || pid == Environment.ProcessId) return false;
        try
        {
            using var previous = Process.GetProcessById(pid);
            return previous.WaitForExit(15_000);
        }
        catch (ArgumentException) { return true; } // Already gone; no running process can still own the path.
    }

    /// <summary>Only after the startup mutex is owned. Incomplete/failed transactions keep recovery files intact.</summary>
    public static void CleanAfterStart()
    {
        if (!CanSelfUpdate) return;
        var result = Path.Combine(InstallDirectory, ResultName);
        StartupUpdateFailed = false;
        if (File.Exists(result))
        {
            try
            {
                if (new FileInfo(result).Length > 64 * 1024) throw new IOException("Update result too large.");
                using var document = JsonDocument.Parse(File.ReadAllText(result));
                StartupUpdateFailed = document.RootElement.GetProperty("success").ValueKind != JsonValueKind.True;
            }
            catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or KeyNotFoundException or UnauthorizedAccessException)
            { StartupUpdateFailed = true; }
            if (StartupUpdateFailed) return;
        }
        UpdateInstaller.CleanUp(InstallDirectory);
        if (File.Exists(result)) File.Delete(result);
    }

    internal static void WriteReport(string path, object value)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        for (var attempt = 0; ; attempt++)
        {
            try { File.Move(temporary, path, overwrite: true); return; }
            // Readers/virus scanners can briefly hold the existing receipt without share-delete.
            // Bounded local metadata retry only: never repeat download, staging or program replacement.
            catch (Exception error) when (attempt < 3 && (error is IOException or UnauthorizedAccessException))
            { Thread.Sleep(25); }
        }
    }
}
