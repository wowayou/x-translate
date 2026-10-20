using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using XTranslate.App.Lexicons;
using XTranslate.Core.Lexicons;

internal static class TestSupport
{
    internal static readonly DictionaryInfo DictionaryInfo = new("Fixed dictionary", "en", "zh-CN");
    internal static readonly DictionaryColumnMap Columns = new("word", "definition", "translation", "phonetic", "pos", "examples", "forms");
    internal const string Header = "word,definition,translation,phonetic,pos,examples,forms\n";
    internal static readonly LexiconProvenance Provenance = new("User fixture", "https://example.test/source", "", "", "Original attribution", "edition-1");
    internal static GlossaryDocument Glossary(string translation = "线程") => new(XTranslate.Core.Lexicons.Glossary.Create(
        new("Software", "Development", "en", "zh-CN"), new[] { new GlossaryTerm("thread", translation, "Reference"), new("API", "", "Keep", true) }), Provenance);

    internal static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    internal static async Task<LexiconStorageException> Error(Func<Task> action, params LexiconStorageError[] codes)
    {
        var error = await Throws<LexiconStorageException>(action);
        Check(codes.Contains(error.Code), $"Unexpected storage error {error.Code}");
        Check(error.InnerException is null && !error.ToString().Contains("private_marker"), "Storage exception leaked input/path");
        return error;
    }
    internal static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    internal static JsonNode Catalog(Sandbox box) => JsonNode.Parse(File.ReadAllText(box.CatalogPath))!;
    internal static async Task WriteCatalog(Sandbox box, JsonNode node) => await File.WriteAllTextAsync(box.CatalogPath, node.ToJsonString());

    internal static async Task<(int ExitCode, string Output)> Child(params string[] args)
    {
        var executable = Environment.ProcessPath!;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await output + await error);
    }
}

internal sealed class Sandbox : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "xtranslate-lexicon-private_marker-" + Guid.NewGuid().ToString("N"));
    internal string Data => Path.Combine(Root, "data");
    internal string CatalogPath => Path.Combine(Data, "lexicons", "catalog.json");
    internal LexiconStore Store => new(Data);
    internal Sandbox() => Directory.CreateDirectory(Root);
    internal string Blob(LexiconResource resource) => Path.Combine(Data, "lexicons", resource.Kind == LexiconKind.Dictionary ? "dictionaries" : "glossaries",
        resource.StorageKey + (resource.Kind == LexiconKind.Dictionary ? ".db" : ".json"));
    internal async Task<string> Csv(string body)
    {
        var path = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".csv");
        await File.WriteAllTextAsync(path, TestSupport.Header + body);
        return path;
    }
    internal async Task<LexiconResource> Dictionary(string meaning = "old")
    {
        var store = Store;
        using var preview = await store.PrepareDictionaryAsync(await Csv($"thread,{meaning},线程,θ,n,example,plural\n"), TestSupport.DictionaryInfo,
            TestSupport.Columns, TestSupport.Provenance);
        return (await store.CommitAsync(preview)).Resource;
    }
    internal async Task<LexiconResource> Glossary()
    {
        var store = Store;
        using var preview = await store.PrepareGlossaryAsync(TestSupport.Glossary());
        return (await store.CommitAsync(preview)).Resource;
    }
    internal bool NoStages() => !Directory.Exists(Path.Combine(Data, "lexicons", ".staging")) ||
        !Directory.EnumerateFileSystemEntries(Path.Combine(Data, "lexicons", ".staging")).Any();
    public void Dispose() => DeleteOwnedTree(Root);
    private static void DeleteOwnedTree(string directory)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if ((attributes & FileAttributes.ReparsePoint) != 0) Directory.Delete(path);
                else DeleteOwnedTree(path);
            }
            else
            {
                if ((attributes & FileAttributes.ReparsePoint) == 0) File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        Directory.Delete(directory);
    }
}
