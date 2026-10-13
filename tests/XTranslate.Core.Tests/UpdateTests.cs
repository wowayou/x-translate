using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XTranslate.Core;

/// <summary>Update check, download and in-place replacement. Mock HTTP and temporary folders only.</summary>
internal static class UpdateTests
{
    private static readonly UpdateVersion Current = new(0, 3, 0);

    public static IEnumerable<(string Name, Func<Task> Test)> All => new (string, Func<Task>)[]
    {
        ("Update versions compare as stable semver and reject pre-releases", Versions),
        ("Release parsing selects this edition's asset and its SHA-256", ReleaseParsing),
        ("Update check reports a private or missing source without retrying", SourceUnavailable),
        ("Download follows only GitHub HTTPS redirects and verifies SHA-256", Download),
        ("Staging rejects unsafe, wrong-edition and non-newer packages", Staging),
        ("Apply replaces in-use files via rename, keeps data, and cleans up later", ApplyAndCleanUp),
        ("Apply restores every original file when a replacement fails", ApplyRollback)
    };

    private static Task Versions()
    {
        Check(UpdateVersion.TryParse("v0.3.10", out var a) && UpdateVersion.TryParse("0.3.9+abc", out var b) && a > b, "Numeric ordering failed");
        foreach (var bad in new[] { "0.3", "0.3.0-beta", "v1.2.3.4", "x.y.z", "", "1.2.-3" })
            Check(!UpdateVersion.TryParse(bad, out _), "Accepted " + bad);
        return Task.CompletedTask;
    }

    private static Task ReleaseParsing()
    {
        var release = UpdateClient.ParseRelease(Release("v0.3.1"), UpdateFlavor.Portable);
        Check(release.Version.ToString() == "0.3.1" && release.AssetName == "XTranslate-0.3.1-win-x64-portable.zip" &&
            release.Sha256 == new string('a', 64) && release.AssetSize == 10, "Wrong asset or digest");
        Check(UpdateClient.ParseRelease(Release("v0.3.1"), UpdateFlavor.Lite).AssetName!.EndsWith("-lite.zip"), "Lite asset not selected");
        // The real latest release before 0.3.0 used another asset name; it must read as "up to date", not as a broken package.
        var older = JsonDocument.Parse("{\"tag_name\":\"v0.2.1\",\"assets\":[{\"name\":\"XTranslate-0.2.1-win-x64-standalone.zip\",\"size\":1}]}").RootElement;
        var latest = UpdateClient.ParseRelease(older, UpdateFlavor.Portable, Current);
        Check(latest.Version.ToString() == "0.2.1" && latest.AssetUrl is null, "An older release without a matching asset was not treated as up to date");
        Check(UpdateClient.ParseRelease(Release("v0.3.1"), UpdateFlavor.Portable, Current).AssetUrl is not null, "A newer release lost its asset");
        var newerWithoutAsset = JsonDocument.Parse("{\"tag_name\":\"v0.4.0\",\"assets\":[]}").RootElement;
        ExpectUpdate(() => UpdateClient.ParseRelease(newerWithoutAsset, UpdateFlavor.Portable, Current), UpdateErrorCode.NoAsset);
        foreach (var (tag, extra) in new[] { ("v0.3.1-rc1", ""), ("v0.3.1", ",\"prerelease\":true"), ("v0.3.1", ",\"draft\":true") })
            ExpectUpdate(() => UpdateClient.ParseRelease(Release(tag, extra), UpdateFlavor.Portable), UpdateErrorCode.NoAsset);
        ExpectUpdate(() => UpdateClient.ParseRelease(Release("v0.3.1", assetUrl: "https://evil.test/x.zip"), UpdateFlavor.Portable), UpdateErrorCode.Package);
        ExpectUpdate(() => UpdateClient.ParseRelease(Release("v0.3.1", assetUrl: "http://github.com/x.zip"), UpdateFlavor.Portable), UpdateErrorCode.Package);
        return Task.CompletedTask;
    }

    private static async Task SourceUnavailable()
    {
        var calls = 0;
        using var client = Client(request => { calls++; Check(request.Headers.UserAgent.ToString().StartsWith("XTranslate/0.3.0"), "Missing user agent"); return new(HttpStatusCode.NotFound); });
        var error = await ExpectUpdateAsync(client.GetLatestAsync(UpdateFlavor.Portable, Current, CancellationToken.None), UpdateErrorCode.SourceUnavailable);
        Check(error.StatusCode == 404 && calls == 1, "Unavailable source retried or lost status");
    }

    private static async Task Download()
    {
        var payload = Encoding.UTF8.GetBytes("package bytes");
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var release = new UpdateRelease(new(0, 3, 1), "v0.3.1", new Uri("https://github.com/r"), new Uri("https://github.com/r/x.zip"), "x.zip", payload.Length, sha);
        var hosts = new List<string>();
        using var client = Client(request =>
        {
            hosts.Add(request.RequestUri!.Host);
            if (request.RequestUri.Host == "github.com")
                return new(HttpStatusCode.Found) { Headers = { Location = new Uri("https://objects.githubusercontent.com/blob") } };
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        });
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "x.zip");
        await client.DownloadAsync(release, Current, path, null, CancellationToken.None);
        Check(File.ReadAllBytes(path).SequenceEqual(payload) && string.Join(",", hosts) == "github.com,objects.githubusercontent.com", "Redirected download failed");

        using var evil = Client(_ => new(HttpStatusCode.Found) { Headers = { Location = new Uri("https://evil.test/blob") } });
        await ExpectUpdateAsync(evil.DownloadAsync(release, Current, path, null, CancellationToken.None), UpdateErrorCode.Network);
        using var tampered = Client(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("package byteZ")) });
        await ExpectUpdateAsync(tampered.DownloadAsync(release, Current, path, null, CancellationToken.None), UpdateErrorCode.Integrity);
        using var oversized = Client(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[payload.Length + 1]) });
        await ExpectUpdateAsync(oversized.DownloadAsync(release, Current, path, null, CancellationToken.None), UpdateErrorCode.Integrity);
        await ExpectUpdateAsync(client.DownloadAsync(release with { Sha256 = null }, Current, path, null, CancellationToken.None), UpdateErrorCode.Integrity);
    }

    private static Task Staging()
    {
        using var temp = new TempFolder();
        string Version(string _) => "0.3.1";
        foreach (var bad in new[] { "../evil.exe", "sub/file.dll", "data", "C:evil", "x.old" })
        {
            var zip = Zip(temp.Path, ("XTranslate.exe", "exe"), (bad, "x"));
            ExpectUpdate(() => UpdateInstaller.Stage(zip, temp.Path, UpdateFlavor.Portable, Current, Version), UpdateErrorCode.Package);
        }
        var lite = Zip(temp.Path, ("XTranslate.exe", "exe"), ("XTranslate.dll", "dll"));
        ExpectUpdate(() => UpdateInstaller.Stage(lite, temp.Path, UpdateFlavor.Portable, Current, Version), UpdateErrorCode.Package);
        var portable = Zip(temp.Path, ("XTranslate.exe", "exe"));
        ExpectUpdate(() => UpdateInstaller.Stage(portable, temp.Path, UpdateFlavor.Portable, Current, _ => "0.3.0"), UpdateErrorCode.NotNewer);
        ExpectUpdate(() => UpdateInstaller.Stage(portable, temp.Path, UpdateFlavor.Portable, Current, _ => null), UpdateErrorCode.Package);
        File.WriteAllText(Path.Combine(temp.Path, "not-a-zip.zip"), "plain text");
        ExpectUpdate(() => UpdateInstaller.Stage(Path.Combine(temp.Path, "not-a-zip.zip"), temp.Path, UpdateFlavor.Portable, Current, Version), UpdateErrorCode.Package);
        Check(!Directory.EnumerateDirectories(temp.Path, ".update-*").Any(), "Rejected packages left staging folders");
        var staged = UpdateInstaller.Stage(portable, temp.Path, UpdateFlavor.Portable, Current, Version);
        Check(File.Exists(Path.Combine(staged, "XTranslate.exe")), "Valid package was not staged");
        return Task.CompletedTask;
    }

    private static Task ApplyAndCleanUp()
    {
        using var temp = new TempFolder();
        File.WriteAllText(Path.Combine(temp.Path, "XTranslate.exe"), "old exe");
        File.WriteAllText(Path.Combine(temp.Path, "native.dll"), "old dll");
        Directory.CreateDirectory(Path.Combine(temp.Path, "data"));
        File.WriteAllText(Path.Combine(temp.Path, "data", "settings.json"), "user data");
        var staged = UpdateInstaller.Stage(Zip(temp.Path, ("XTranslate.exe", "new exe"), ("native.dll", "new dll"), ("added.dll", "added")),
            temp.Path, UpdateFlavor.Portable, Current, _ => "0.3.1");
        // An open, share-delete handle behaves like a running EXE: it can be renamed but not overwritten.
        using (var running = new FileStream(Path.Combine(temp.Path, "XTranslate.exe"), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            Check(UpdateInstaller.Apply(staged, temp.Path) == 3, "Not every file was applied");
            Check(File.ReadAllText(Path.Combine(temp.Path, "XTranslate.exe")) == "new exe" && File.ReadAllText(Path.Combine(temp.Path, "added.dll")) == "added" &&
                File.Exists(Path.Combine(temp.Path, "XTranslate.exe.old")), "In-use file was not replaced through rename");
            UpdateInstaller.CleanUp(temp.Path); // Still in use: must not throw.
        }
        UpdateInstaller.CleanUp(temp.Path);
        Check(!Directory.EnumerateFiles(temp.Path, "*.old").Any() && !Directory.Exists(staged) &&
            File.ReadAllText(Path.Combine(temp.Path, "data", "settings.json")) == "user data", "Cleanup left files or touched data");
        return Task.CompletedTask;
    }

    private static Task ApplyRollback()
    {
        using var temp = new TempFolder();
        File.WriteAllText(Path.Combine(temp.Path, "a.dll"), "old a");
        File.WriteAllText(Path.Combine(temp.Path, "b.dll"), "old b");
        var staged = UpdateInstaller.Stage(Zip(temp.Path, ("XTranslate.exe", "new exe"), ("a.dll", "new a"), ("b.dll", "new b")),
            temp.Path, UpdateFlavor.Portable, Current, _ => "0.3.1");
        // No share-delete: this file cannot be renamed, so the update must roll back everything.
        using (new FileStream(Path.Combine(temp.Path, "b.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
            ExpectUpdate(() => UpdateInstaller.Apply(staged, temp.Path), UpdateErrorCode.Apply);
        Check(File.ReadAllText(Path.Combine(temp.Path, "a.dll")) == "old a" && File.ReadAllText(Path.Combine(temp.Path, "b.dll")) == "old b" &&
            !File.Exists(Path.Combine(temp.Path, "a.dll.old")), "Rollback did not restore the original files");
        return Task.CompletedTask;
    }

    private static JsonElement Release(string tag, string extra = "", string assetUrl = "https://github.com/wowayou/x-translate/releases/download/v0.3.1/x.zip")
    {
        var version = tag.TrimStart('v').Split('-')[0];
        var json = $"{{\"tag_name\":\"{tag}\",\"html_url\":\"https://github.com/wowayou/x-translate/releases/tag/{tag}\"{extra},\"assets\":[" +
            $"{{\"name\":\"XTranslate-{version}-win-x64-portable.zip\",\"size\":10,\"digest\":\"sha256:{new string('A', 64)}\",\"browser_download_url\":\"{assetUrl}\"}}," +
            $"{{\"name\":\"XTranslate-{version}-win-x64-lite.zip\",\"size\":5,\"digest\":\"sha256:{new string('b', 64)}\",\"browser_download_url\":\"{assetUrl}\"}}]}}";
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static string Zip(string folder, params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(content);
        }
        return path;
    }

    private static UpdateClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(new HttpClient(new Handler(respond)));
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private static UpdateException ExpectUpdate(Action action, UpdateErrorCode code)
    {
        try { action(); }
        catch (UpdateException error) { Check(error.Code == code, $"Expected {code}, got {error.Code}: {error.Message}"); return error; }
        throw new Exception("Expected " + code);
    }

    private static async Task<UpdateException> ExpectUpdateAsync(Task task, UpdateErrorCode code)
    {
        try { await task; }
        catch (UpdateException error) { Check(error.Code == code, $"Expected {code}, got {error.Code}: {error.Message}"); return error; }
        throw new Exception("Expected " + code);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xtranslate-update-" + Guid.NewGuid().ToString("N")[..8]);
        public TempFolder() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }
}
