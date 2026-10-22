using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XTranslate.Core;

/// <summary>
/// A verified flat package kept intact while an updater runs from it. Installing copies into a separate
/// payload first: neither a running bundle pathname nor any lazy-loaded updater dependency is replaced.
/// This is a same-user local update transaction, not protection against malicious concurrent filesystem edits.
/// </summary>
public static partial class PreparedUpdate
{
    public const string ManifestName = ".update-plan.json";
    private const int MaxManifestBytes = 64 * 1024;
    private const string PayloadName = ".payload";
    private const string WorkerName = ".update-worker";
    private static readonly JsonSerializerOptions Json = new() { AllowDuplicateProperties = false };

    public sealed record FileIdentity(string Name, string Sha256);
    public sealed record Plan(string From, string To, UpdateFlavor Flavor, FileIdentity[] Files);

    public static void Seal(string staging, UpdateVersion from, UpdateVersion to, UpdateFlavor flavor)
    {
        RequireStagePath(staging, Directory.GetParent(Path.GetFullPath(staging))!.FullName);
        var files = ReadFiles(staging);
        var plan = new Plan(from.ToString(), to.ToString(), flavor, files);
        using var stream = new FileStream(Path.Combine(staging, ManifestName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, plan, Json);
    }

    public static Plan Read(string staging, string installDirectory)
    {
        RequireStagePath(staging, installDirectory);
        var path = Path.Combine(staging, ManifestName);
        if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > MaxManifestBytes || IsLink(path))
            throw InvalidPlan();
        var protectedFiles = ReadFiles(staging); // Validate paths/links before opening manifest-controlled payload.
        Plan plan;
        try
        {
            using var stream = File.OpenRead(path);
            plan = JsonSerializer.Deserialize<Plan>(stream, Json) ?? throw InvalidPlan();
        }
        catch (JsonException) { throw InvalidPlan(); }
        if (!UpdateVersion.TryParse(plan.From, out var from) || !UpdateVersion.TryParse(plan.To, out var to) || !(to > from) ||
            !Enum.IsDefined(plan.Flavor) || plan.Files is null || plan.Files.Length is 0 or > 64 ||
            plan.Files.Any(file => file is null || !SafeName(file.Name) || file.Sha256 is null || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit)) ||
            plan.Files.Select(file => file.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Files.Length ||
            !plan.Files.Any(file => file.Name.Equals("XTranslate.exe", StringComparison.Ordinal)))
            throw InvalidPlan();
        CompareFiles(protectedFiles, plan.Files);
        return plan;
    }

    public static int Apply(string staging, string installDirectory, UpdateVersion updaterVersion, UpdateFlavor updaterFlavor,
        Func<string, string?> readProductVersion, Func<string, Version, bool>? frameworkAvailable = null)
    {
        var plan = Read(staging, installDirectory);
        if (plan.To != updaterVersion.ToString() || plan.Flavor != updaterFlavor ||
            !UpdateVersion.TryParse(readProductVersion(Path.Combine(installDirectory, "XTranslate.exe")), out var installed) ||
            installed.ToString() != plan.From)
            throw new UpdateException("The installed version or updater does not match the prepared package.", UpdateErrorCode.Package);
        if (updaterFlavor == UpdateFlavor.Lite && frameworkAvailable is not null)
            foreach (var (name, needed) in UpdateInstaller.RequiredFrameworks(Path.Combine(staging, "XTranslate.runtimeconfig.json")))
                if (!frameworkAvailable(name, needed))
                    throw new UpdateException("The prepared update needs an unavailable runtime.", UpdateErrorCode.RuntimeMissing);
        UpdateInstaller.EnsureWritable(installDirectory);
        var payload = Path.Combine(staging, PayloadName);
        if (Directory.Exists(payload) || File.Exists(payload)) throw InvalidPlan(); // Never reuse a partial payload.
        Directory.CreateDirectory(payload);
        var keepPayload = false;
        try
        {
            foreach (var file in plan.Files)
            {
                var destination = Path.Combine(payload, file.Name);
                File.Copy(Path.Combine(staging, file.Name), destination, overwrite: false);
                UpdateInstaller.VerifySha256(destination, file.Sha256);
            }
            return UpdateInstaller.Apply(payload, installDirectory);
        }
        catch (UpdateException error) when (error.Code == UpdateErrorCode.RollbackIncomplete)
        { keepPayload = true; throw; }
        finally
        {
            // The updater's original files and manifest must remain until it exits. Only its copied payload
            // can be removed here. A later normal startup performs the existing staging/old-file cleanup.
            if (!keepPayload && Directory.Exists(payload)) Directory.Delete(payload, recursive: true);
        }
    }

    public static string CopyWorker(string staging, string installDirectory)
    {
        var plan = Read(staging, installDirectory);
        var worker = Path.Combine(staging, WorkerName);
        if (Directory.Exists(worker) || File.Exists(worker)) throw InvalidPlan();
        Directory.CreateDirectory(worker);
        foreach (var file in plan.Files)
        {
            var destination = Path.Combine(worker, file.Name);
            File.Copy(Path.Combine(staging, file.Name), destination, overwrite: false);
            UpdateInstaller.VerifySha256(destination, file.Sha256);
        }
        return worker;
    }

    public static void VerifyWorker(string staging, string installDirectory)
    {
        var plan = Read(staging, installDirectory);
        var worker = Path.Combine(staging, WorkerName);
        if (!Directory.Exists(worker) || IsLink(worker) || Directory.EnumerateDirectories(worker).Any()) throw InvalidPlan();
        CompareFiles(ReadFiles(worker), plan.Files);
    }

    private static void CompareFiles(FileIdentity[] actual, FileIdentity[] expected)
    {
        if (actual.Length != expected.Length || actual.Any(file =>
            !expected.Any(identity => identity.Name == file.Name && identity.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))))
            throw new UpdateException("The prepared package changed after verification.", UpdateErrorCode.Integrity);
    }

    private static FileIdentity[] ReadFiles(string staging)
    {
        if (Directory.EnumerateDirectories(staging).Any(path => Path.GetFileName(path) != WorkerName || IsLink(path))) throw InvalidPlan();
        var result = new List<FileIdentity>();
        var bytes = 0L;
        foreach (var path in Directory.EnumerateFiles(staging))
        {
            var name = Path.GetFileName(path);
            if (name == ManifestName) continue;
            if (result.Count >= 64 || !SafeName(name) || IsLink(path)) throw InvalidPlan();
            bytes += new FileInfo(path).Length;
            if (bytes > UpdateClient.MaxPackageBytes) throw InvalidPlan();
            using var stream = File.OpenRead(path);
            result.Add(new FileIdentity(name, Convert.ToHexString(SHA256.HashData(stream))));
        }
        return result.OrderBy(file => file.Name, StringComparer.Ordinal).ToArray();
    }

    private static void RequireStagePath(string staging, string installDirectory)
    {
        var stage = Path.GetFullPath(staging).TrimEnd(Path.DirectorySeparatorChar);
        var install = Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Directory.GetParent(stage)?.FullName, install, StringComparison.OrdinalIgnoreCase) ||
            !StageName().IsMatch(Path.GetFileName(stage)) || !Directory.Exists(stage) || IsLink(stage) || IsLink(install))
            throw InvalidPlan();
    }

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static bool SafeName(string? name) => !string.IsNullOrEmpty(name) && name is not "." and not ".." &&
        !name.EndsWith('.') && !name.EndsWith(' ') &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.Contains('/') && !name.Contains('\\') && !name.Contains(':') &&
        !name.Equals("data", StringComparison.OrdinalIgnoreCase) && !name.StartsWith(".update-", StringComparison.OrdinalIgnoreCase) &&
        !name.EndsWith(UpdateInstaller.OldSuffix, StringComparison.OrdinalIgnoreCase);
    private static UpdateException InvalidPlan() => new("The prepared update manifest or directory is invalid.", UpdateErrorCode.Package);
    [GeneratedRegex(@"^\.update-[0-9a-f]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex StageName();
}
