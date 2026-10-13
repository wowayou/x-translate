using System.Diagnostics;
using System.Reflection;
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
    public static async Task ApplyPackageAsync(string zip, string? sha256)
    {
        if (Flavor is not UpdateFlavor flavor) throw new UpdateException("Development builds do not self-update.", UpdateErrorCode.Package);
        await Task.Run(() =>
        {
            if (sha256 is not null) UpdateInstaller.VerifySha256(zip, sha256);
            var staging = UpdateInstaller.Stage(zip, InstallDirectory, flavor, Current, ReadProductVersion);
            UpdateInstaller.Apply(staging, InstallDirectory);
        });
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
