using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace XTranslate.Core;

/// <summary>Stable releases only: MAJOR.MINOR.PATCH, optional leading "v". Pre-release tags are ignored.</summary>
public sealed record UpdateVersion(int Major, int Minor, int Patch) : IComparable<UpdateVersion>
{
    public static bool TryParse(string? text, out UpdateVersion version)
    {
        version = new UpdateVersion(0, 0, 0);
        var value = (text ?? "").Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        var plus = value.IndexOf('+');
        if (plus >= 0) value = value[..plus]; // Build metadata (e.g. +commit) does not affect ordering.
        var parts = value.Split('.');
        if (parts.Length != 3 || !parts.All(part => part.Length is > 0 and <= 6 && part.All(char.IsAsciiDigit))) return false;
        version = new UpdateVersion(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
        return true;
    }

    public int CompareTo(UpdateVersion? other) =>
        other is null ? 1 : (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));

    public static bool operator >(UpdateVersion a, UpdateVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(UpdateVersion a, UpdateVersion b) => a.CompareTo(b) < 0;
    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

public enum UpdateFlavor { Portable, Lite }

/// <summary>Asset fields are null when the release is not newer than this copy (nothing to download).</summary>
public sealed record UpdateRelease(UpdateVersion Version, string Tag, Uri PageUrl, Uri? AssetUrl, string? AssetName, long AssetSize, string? Sha256);

public enum UpdateErrorCode { SourceUnavailable, NoAsset, Network, Integrity, Package, NotNewer, Apply }

public sealed class UpdateException(string message, UpdateErrorCode code, int? statusCode = null) : Exception(message)
{
    public UpdateErrorCode Code { get; } = code;
    public int? StatusCode { get; } = statusCode;
}

/// <summary>
/// Reads the latest stable GitHub release and downloads its asset. One request per call, no retries,
/// no cookies or credentials; redirects are followed manually and only to GitHub hosts over HTTPS.
/// </summary>
public sealed class UpdateClient : IDisposable
{
    public const string Repository = "wowayou/x-translate";
    public const long MaxPackageBytes = 256L * 1024 * 1024;
    private const int MaxMetadataBytes = 1_048_576;
    private static readonly string[] AllowedHosts =
        { "api.github.com", "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com" };
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public UpdateClient(HttpClient? client = null)
    {
        _http = client ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _ownsClient = client is null;
    }

    public static string AssetName(UpdateVersion version, UpdateFlavor flavor) =>
        $"XTranslate-{version}-win-x64-{(flavor == UpdateFlavor.Portable ? "portable" : "lite")}.zip";

    public async Task<UpdateRelease> GetLatestAsync(UpdateFlavor flavor, UpdateVersion current, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await SendAsync(new Uri($"https://api.github.com/repos/{Repository}/releases/latest"), "application/vnd.github+json", current, deadline.Token);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UpdateException("Update source unavailable (private repository or no release).", UpdateErrorCode.SourceUnavailable, (int)response.StatusCode);
        if (!response.IsSuccessStatusCode)
            throw new UpdateException("Update check failed.", UpdateErrorCode.Network, (int)response.StatusCode);
        if (response.Content.Headers.ContentLength > MaxMetadataBytes) throw new UpdateException("Release metadata too large.", UpdateErrorCode.Package);
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var buffer = new MemoryStream();
        await CopyBoundedAsync(stream, buffer, MaxMetadataBytes, null, deadline.Token);
        try
        {
            using var document = JsonDocument.Parse(buffer.ToArray());
            return ParseRelease(document.RootElement, flavor, current);
        }
        catch (JsonException) { throw new UpdateException("Release metadata is invalid.", UpdateErrorCode.Package); }
    }

    /// <summary>
    /// Selects the exact asset for this flavor. Drafts, pre-releases and non-semver tags are rejected.
    /// A release that is not newer than <paramref name="current"/> needs no asset (older naming is fine).
    /// </summary>
    public static UpdateRelease ParseRelease(JsonElement root, UpdateFlavor flavor, UpdateVersion? current = null)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String ||
            !UpdateVersion.TryParse(tag.GetString(), out var version) ||
            root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True ||
            root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True)
            throw new UpdateException("No stable release.", UpdateErrorCode.NoAsset);
        var page = root.TryGetProperty("html_url", out var html) && Uri.TryCreate(html.GetString(), UriKind.Absolute, out var htmlUri) && IsAllowed(htmlUri)
            ? htmlUri : new Uri($"https://github.com/{Repository}/releases");
        if (current is not null && !(version > current)) return new UpdateRelease(version, tag.GetString()!, page, null, null, 0, null);
        var wanted = AssetName(version, flavor);
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object || !asset.TryGetProperty("name", out var name) || name.GetString() != wanted) continue;
                if (!asset.TryGetProperty("browser_download_url", out var url) || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var assetUri) || !IsAllowed(assetUri))
                    throw new UpdateException("Release asset URL is not allowed.", UpdateErrorCode.Package);
                var size = asset.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var bytes) ? bytes : 0;
                if (size <= 0 || size > MaxPackageBytes) throw new UpdateException("Release asset size is invalid.", UpdateErrorCode.Package);
                string? sha = null;
                if (asset.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String &&
                    digest.GetString() is { } value && value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && IsSha256(value[7..]))
                    sha = value[7..].ToLowerInvariant();
                return new UpdateRelease(version, tag.GetString()!, page, assetUri, wanted, size, sha);
            }
        throw new UpdateException("The release has no package for this edition.", UpdateErrorCode.NoAsset);
    }

    /// <summary>Downloads to <paramref name="destination"/>, verifying size and SHA-256. Without a digest it refuses.</summary>
    public async Task DownloadAsync(UpdateRelease release, UpdateVersion current, string destination, IProgress<double>? progress, CancellationToken token)
    {
        if (release.AssetUrl is null) throw new UpdateException("The release has no package for this edition.", UpdateErrorCode.NoAsset);
        if (release.Sha256 is null) throw new UpdateException("The release does not publish a SHA-256 digest.", UpdateErrorCode.Integrity);
        var uri = release.AssetUrl;
        for (var hop = 0; ; hop++)
        {
            using var response = await SendAsync(uri, "application/octet-stream", current, token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (hop >= 5 || !IsAllowed(next)) throw new UpdateException("Download redirect is not allowed.", UpdateErrorCode.Network);
                uri = next;
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new UpdateException("Download failed.", UpdateErrorCode.Network, (int)response.StatusCode);
            await using var body = await response.Content.ReadAsStreamAsync(token);
            await using (var file = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                await CopyBoundedAsync(body, file, release.AssetSize, progress, token);
            if (new FileInfo(destination).Length != release.AssetSize) throw new UpdateException("Download is incomplete.", UpdateErrorCode.Integrity);
            UpdateInstaller.VerifySha256(destination, release.Sha256);
            return;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, string accept, UpdateVersion current, CancellationToken token)
    {
        if (!IsAllowed(uri)) throw new UpdateException("Update URL is not allowed.", UpdateErrorCode.Network);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("XTranslate", current.ToString())); // Required by the GitHub API.
        try { return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); }
        catch (HttpRequestException) { throw new UpdateException("Network error.", UpdateErrorCode.Network); }
    }

    private static async Task CopyBoundedAsync(Stream source, Stream target, long limit, IProgress<double>? progress, CancellationToken token)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        try
        {
            while ((read = await source.ReadAsync(buffer, token)) > 0)
            {
                total += read;
                if (total > limit) throw new UpdateException("Response exceeds the expected size.", UpdateErrorCode.Integrity);
                await target.WriteAsync(buffer.AsMemory(0, read), token);
                progress?.Report(limit > 0 ? Math.Min(1, (double)total / limit) : 0);
            }
        }
        catch (IOException) { throw new UpdateException("Network error.", UpdateErrorCode.Network); }
    }

    public static bool IsAllowed(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        AllowedHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase);

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}

/// <summary>
/// Replaces program files in place. Windows allows renaming (not deleting) a running EXE or loaded DLL,
/// so each replaced file is first renamed to *.old, then the new file is moved in. Any failure restores
/// every renamed file. data/ is never read, moved or written. Leftover *.old files are removed on next start.
/// </summary>
public static class UpdateInstaller
{
    public const string OldSuffix = ".old";
    private const long MaxEntryBytes = 200L * 1024 * 1024;
    private const int MaxEntries = 64;

    public static void VerifySha256(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException("SHA-256 does not match.", UpdateErrorCode.Integrity);
    }

    /// <summary>Extracts a flat release ZIP into a new staging folder and checks edition and version.</summary>
    public static string Stage(string zipPath, string installDirectory, UpdateFlavor flavor, UpdateVersion current,
        Func<string, string?> readProductVersion)
    {
        var staging = Path.Combine(installDirectory, ".update-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        try
        {
            using (var archive = OpenZip(zipPath))
            {
                if (archive.Entries.Count is 0 or > MaxEntries) throw Package("Unexpected number of files.");
                foreach (var entry in archive.Entries)
                {
                    var name = entry.FullName;
                    // Release packages are flat. Anything with a path, a data folder or a reserved name is rejected.
                    if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name.Contains(':') || name is "." or ".." ||
                        name.Equals("data", StringComparison.OrdinalIgnoreCase) || name.EndsWith(OldSuffix, StringComparison.OrdinalIgnoreCase) ||
                        name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                        throw Package("Unsafe entry in package.");
                    if (entry.Length > MaxEntryBytes) throw Package("Entry too large.");
                    entry.ExtractToFile(Path.Combine(staging, name), overwrite: false);
                }
            }
            var exe = Path.Combine(staging, "XTranslate.exe");
            if (!File.Exists(exe)) throw Package("Package has no XTranslate.exe.");
            var packageFlavor = File.Exists(Path.Combine(staging, "XTranslate.dll")) ? UpdateFlavor.Lite : UpdateFlavor.Portable;
            if (packageFlavor != flavor) throw Package("Package edition does not match this copy.");
            if (!UpdateVersion.TryParse(readProductVersion(exe), out var version)) throw Package("Package version is unreadable.");
            if (!(version > current)) throw new UpdateException("Package is not newer than this copy.", UpdateErrorCode.NotNewer);
            return staging;
        }
        catch
        {
            TryDelete(staging);
            throw;
        }
    }

    /// <summary>Moves staged files into place with full rollback. Returns the number of files replaced or added.</summary>
    public static int Apply(string staging, string installDirectory)
    {
        var moved = new List<(string Target, string? Old)>();
        try
        {
            foreach (var source in Directory.GetFiles(staging))
            {
                var target = Path.Combine(installDirectory, Path.GetFileName(source));
                string? old = null;
                if (File.Exists(target))
                {
                    old = target + OldSuffix;
                    if (File.Exists(old)) File.Delete(old); // A stale leftover that is no longer in use.
                    File.Move(target, old);
                }
                moved.Add((target, old));
                File.Move(source, target);
            }
            TryDelete(staging);
            return moved.Count;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            for (var index = moved.Count - 1; index >= 0; index--)
            {
                var (target, old) = moved[index];
                try
                {
                    // The new file may already be in place; set it aside, then put the original back.
                    if (File.Exists(target) && (old is null || File.Exists(old))) File.Delete(target);
                    if (old is not null && File.Exists(old)) File.Move(old, target);
                }
                catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException) { /* Leaves *.old for manual restore. */ }
            }
            TryDelete(staging);
            throw new UpdateException("Files could not be replaced; the previous version was restored.", UpdateErrorCode.Apply);
        }
    }

    /// <summary>Removes *.old files and abandoned staging folders from earlier updates. Never touches data/.</summary>
    public static void CleanUp(string installDirectory)
    {
        foreach (var file in Directory.EnumerateFiles(installDirectory, "*" + OldSuffix))
            try { File.Delete(file); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        foreach (var folder in Directory.EnumerateDirectories(installDirectory, ".update-*")) TryDelete(folder);
    }

    private static ZipArchive OpenZip(string path)
    {
        try { return ZipFile.OpenRead(path); }
        catch (InvalidDataException) { throw Package("Not a valid ZIP package."); }
    }

    private static UpdateException Package(string message) => new(message, UpdateErrorCode.Package);

    private static void TryDelete(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
