using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using XTranslate.Core;

namespace XTranslate.App;

/// <summary>
/// Version, edition and in-place update for this copy. The edition is stamped at publish time
/// (build.ps1 / install.ps1); "Dev" builds under bin/ never replace themselves.
/// </summary>
internal static class AppUpdates
{
    public static UpdateVersion Current { get; } = ReadCurrent();
    public static UpdateFlavor? Flavor { get; } = ReadFlavor();
    public static string InstallDirectory => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    public static bool CanSelfUpdate => Flavor is not null;

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

    /// <summary>Optional sidecar "&lt;zip&gt;.sha256" (first token is the hex digest), as written by build.ps1.</summary>
    public static string? ReadSidecarSha256(string zip)
    {
        var path = zip + ".sha256";
        if (!File.Exists(path)) return null;
        var token = File.ReadAllText(path).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return token.Length == 64 && token.All(Uri.IsHexDigit) ? token : throw new UpdateException("Invalid .sha256 file.", UpdateErrorCode.Integrity);
    }

    /// <summary>Verifies (when a digest is given), stages and applies a package. Program files only.</summary>
    public static async Task<UpdateVersion> ApplyPackageAsync(string zip, string? sha256)
    {
        if (Flavor is not UpdateFlavor flavor) throw new UpdateException("Development builds do not self-update.", UpdateErrorCode.Package);
        return await Task.Run(() =>
        {
            if (sha256 is not null) UpdateInstaller.VerifySha256(zip, sha256);
            var stagedVersion = Current;
            string? InspectVersion(string path)
            {
                var metadata = ReadProductVersion(path);
                if (UpdateVersion.TryParse(metadata, out var version)) stagedVersion = version;
                return metadata;
            }
            var staging = UpdateInstaller.Stage(zip, InstallDirectory, flavor, Current, InspectVersion, FrameworkAvailable);
            UpdateInstaller.Apply(staging, InstallDirectory);
            // Windows can return the old loaded module's version for the same EXE path after a rename.
            // Report the version we actually verified in staging, not a second lookup of the running image.
            return stagedVersion;
        });
    }

    /// <summary>
    /// Roll-forward check the host itself applies: same major version, installed minor.patch at least the required one.
    /// Frameworks are looked up beside the runtime this process runs on (dotnet\shared\&lt;name&gt;\&lt;version&gt;).
    /// </summary>
    public static bool FrameworkAvailable(string name, Version needed)
    {
        var runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar);
        var shared = Directory.GetParent(runtime)?.Parent?.FullName;
        if (shared is null || !Directory.Exists(Path.Combine(shared, name))) return false;
        return Directory.EnumerateDirectories(Path.Combine(shared, name))
            .Select(path => Version.TryParse(Path.GetFileName(path).Split('-')[0], out var installed) ? installed : null)
            .Any(installed => installed is not null && installed.Major == needed.Major && installed >= needed);
    }

    /// <summary>Checks GitHub and, if a newer release exists, downloads, verifies and applies it (no restart).</summary>
    public static async Task<object> UpdateOnlineAsync(CancellationToken token)
    {
        if (Flavor is not UpdateFlavor flavor) throw new UpdateException("Development builds do not self-update.", UpdateErrorCode.Package);
        using var client = new UpdateClient();
        var release = await client.GetLatestAsync(flavor, Current, token);
        if (!(release.Version > Current)) return new { success = true, updated = false, latest = release.Version.ToString() };
        var zip = Path.Combine(Path.GetTempPath(), "xtranslate-" + Guid.NewGuid().ToString("N")[..8] + ".zip");
        UpdateVersion installed;
        try
        {
            await client.DownloadAsync(release, Current, zip, null, token);
            installed = await ApplyPackageAsync(zip, release.Sha256);
        }
        finally { try { File.Delete(zip); } catch (IOException) { } }
        return new { success = true, updated = true, latest = release.Version.ToString(), to = installed.ToString() };
    }

    /// <summary>Runs a headless update action for scripts and tests; writes a JSON report; exit code 0 on success.</summary>
    public static async Task<int> HeadlessAsync(string report, Func<Task<object>> work)
    {
        object result;
        var ok = false;
        try { result = await work(); ok = true; }
        catch (UpdateException error) { result = new { success = false, code = error.Code.ToString(), error = error.Message, status = error.StatusCode }; }
        catch (OperationCanceledException) { result = new { success = false, code = "Timeout" }; }
        File.WriteAllText(report, JsonSerializer.Serialize(result));
        return ok ? 0 : 1;
    }

    /// <summary>Starts the new EXE, which waits for this process to exit before taking the single-instance lock.</summary>
    public static void StartReplacement()
    {
        var exe = Path.Combine(InstallDirectory, "XTranslate.exe");
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, ArgumentList = { "--after-update", Environment.ProcessId.ToString() } });
    }

    /// <summary>Called first thing on startup of a published copy.</summary>
    public static void AfterStart(string[] args)
    {
        var index = Array.IndexOf(args, "--after-update");
        if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var pid))
        {
            try { using var previous = Process.GetProcessById(pid); previous.WaitForExit(15_000); }
            catch (ArgumentException) { /* Already gone. */ }
        }
        if (CanSelfUpdate) UpdateInstaller.CleanUp(InstallDirectory);
    }
}
