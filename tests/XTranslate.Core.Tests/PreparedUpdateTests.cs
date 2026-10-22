using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using XTranslate.Core;

internal static class PreparedUpdateTests
{
    private static readonly UpdateVersion From = new(0, 4, 0);
    private static readonly UpdateVersion To = new(0, 4, 1);
    public static IEnumerable<(string Name, Func<Task> Test)> All => new (string, Func<Task>)[]
    {
        ("Prepared update leaves active program/data bytes unchanged and seals exact file identities", Preparation),
        ("Prepared updater copies a separate payload and never moves its own loaded EXE/DLL paths", Apply),
        ("Prepared update rejects modified/missing/added payload bytes before replacing any file", Tampering),
        ("Prepared update rejects malformed manifests, unsafe names, mismatched versions and editions", InvalidPlans),
        ("Prepared update refuses reparse paths and a Lite runtime lost after preparation", LinksAndRuntime),
        ("Prepared update rolls back a partial locked-file failure while retaining the updater's original bytes", Rollback)
    };

    private static Task Preparation()
    {
        using var fixture = new Fixture();
        var before = fixture.ProgramBytes();
        var plan = PreparedUpdate.Read(fixture.Stage, fixture.Root);
        Check(before == fixture.ProgramBytes() && plan.From == From.ToString() && plan.To == To.ToString() &&
            plan.Files.Length == 3 && plan.Files.All(file => file.Sha256.Length == 64), "Preparation modified program/data or lost identities");
        Check(!Directory.EnumerateFiles(fixture.Root, "*.old").Any(), "Preparation replaced active files");
        return Task.CompletedTask;
    }

    private static Task Apply()
    {
        using var fixture = new Fixture();
        var worker = PreparedUpdate.CopyWorker(fixture.Stage, fixture.Root);
        var workerExe = Path.Combine(worker, "XTranslate.exe");
        var stageBytes = fixture.StageBytes();
        using (new FileStream(workerExe, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (new FileStream(Path.Combine(worker, "a.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Check(PreparedUpdate.Apply(fixture.Stage, fixture.Root, To, UpdateFlavor.Portable, _ => From.ToString()) == 3, "Update did not copy all files");
            Check(stageBytes == fixture.StageBytes() && File.ReadAllText(workerExe) == "new exe", "Updater's original load paths moved or changed");
        }
        Check(File.ReadAllText(Path.Combine(fixture.Root, "XTranslate.exe")) == "new exe" &&
            File.ReadAllText(Path.Combine(fixture.Root, "data", "nested", "learning.json")) == "fixed data" &&
            Directory.EnumerateDirectories(fixture.Stage).All(path => Path.GetFileName(path) == ".update-worker"), "Apply lost new files/data or retained a copied payload");
        return Task.CompletedTask;
    }

    private static Task Tampering()
    {
        foreach (var kind in new[] { "edit", "delete", "add", "worker" })
        {
            using var fixture = new Fixture();
            var before = fixture.ProgramBytes();
            if (kind == "edit") File.WriteAllText(Path.Combine(fixture.Stage, "a.dll"), "changed bytes");
            if (kind == "delete") File.Delete(Path.Combine(fixture.Stage, "a.dll"));
            if (kind == "add") File.WriteAllText(Path.Combine(fixture.Stage, "extra.dll"), "unexpected");
            if (kind == "worker")
            {
                var worker = PreparedUpdate.CopyWorker(fixture.Stage, fixture.Root);
                File.WriteAllText(Path.Combine(worker, "a.dll"), "changed worker bytes");
                Reject(() => PreparedUpdate.VerifyWorker(fixture.Stage, fixture.Root), UpdateErrorCode.Integrity);
            }
            else Reject(() => PreparedUpdate.Apply(fixture.Stage, fixture.Root, To, UpdateFlavor.Portable, _ => From.ToString()), UpdateErrorCode.Integrity);
            Check(before == fixture.ProgramBytes() && !Directory.EnumerateFiles(fixture.Root, "*.old").Any(), "Invalid prepared bytes crossed swap boundary");
        }
        return Task.CompletedTask;
    }

    private static Task InvalidPlans()
    {
        foreach (var kind in new[] { "null", "duplicate", "unsafe", "same-version", "wrong-updater", "wrong-installed", "wrong-edition", "directory" })
        {
            using var fixture = new Fixture();
            var before = fixture.ProgramBytes();
            var path = Path.Combine(fixture.Stage, PreparedUpdate.ManifestName);
            var plan = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            if (kind == "null") File.WriteAllText(path, "null");
            else if (kind == "duplicate") File.WriteAllText(path, "{\"From\":\"0.4.0\",\"From\":\"0.4.0\"}");
            else if (kind == "directory") Directory.CreateDirectory(Path.Combine(fixture.Stage, "unexpected"));
            else
            {
                if (kind == "unsafe") plan["Files"]![0]!["Name"] = "../data/secret";
                if (kind == "same-version") plan["To"] = From.ToString();
                File.WriteAllText(path, plan.ToJsonString());
            }
            Reject(() => PreparedUpdate.Apply(fixture.Stage, fixture.Root,
                kind == "wrong-updater" ? From : To,
                kind == "wrong-edition" ? UpdateFlavor.Lite : UpdateFlavor.Portable,
                _ => kind == "wrong-installed" ? "0.3.0" : From.ToString()), UpdateErrorCode.Package);
            Check(before == fixture.ProgramBytes(), "Invalid manifest/updater modified installation");
        }
        using (var fixture = new Fixture())
            Reject(() => PreparedUpdate.Read(fixture.Stage, Path.GetTempPath()), UpdateErrorCode.Package);
        return Task.CompletedTask;
    }

    private static Task LinksAndRuntime()
    {
        using (var fixture = new Fixture())
        {
            var path = Path.Combine(fixture.Stage, PreparedUpdate.ManifestName);
            var plan = PreparedUpdate.Read(fixture.Stage, fixture.Root);
            File.WriteAllText(path, JsonSerializer.Serialize(plan with { Flavor = UpdateFlavor.Lite }));
            Reject(() => PreparedUpdate.Apply(fixture.Stage, fixture.Root, To, UpdateFlavor.Lite, _ => From.ToString(), (_, _) => false), UpdateErrorCode.Package);
        }
        using (var fixture = new Fixture())
        {
            File.WriteAllText(Path.Combine(fixture.Stage, "XTranslate.runtimeconfig.json"), "{\"runtimeOptions\":{\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\"99.0.0\"}}}");
            File.Delete(Path.Combine(fixture.Stage, PreparedUpdate.ManifestName));
            PreparedUpdate.Seal(fixture.Stage, From, To, UpdateFlavor.Lite);
            Reject(() => PreparedUpdate.Apply(fixture.Stage, fixture.Root, To, UpdateFlavor.Lite, _ => From.ToString(), (_, _) => false), UpdateErrorCode.RuntimeMissing);
        }
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
        using (var fixture = new Fixture())
        {
            // Owned directory junction; no privilege elevation, user directory or external path involved.
            var link = Path.Combine(fixture.Stage, ".update-worker");
            var start = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec")!)
            {
                UseShellExecute = false, CreateNoWindow = true,
                Arguments = $"/d /c mklink /J \"{link}\" \"{fixture.Root}\\data\""
            };
            using var process = System.Diagnostics.Process.Start(start)!;
            Check(process.WaitForExit(5000) && process.ExitCode == 0, "Could not create owned link fixture");
            try { Reject(() => PreparedUpdate.Read(fixture.Stage, fixture.Root), UpdateErrorCode.Package); }
            finally { Directory.Delete(link); }
        }
        return Task.CompletedTask;
    }

    private static Task Rollback()
    {
        // This models Windows deny-delete locking. The cross-platform lane still checks all pure boundaries.
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
        using var fixture = new Fixture();
        var before = fixture.ProgramBytes();
        var stageBytes = fixture.StageBytes();
        using (new FileStream(Path.Combine(fixture.Root, "b.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Reject(() => PreparedUpdate.Apply(fixture.Stage, fixture.Root, To, UpdateFlavor.Portable, _ => From.ToString()), UpdateErrorCode.Apply);
        Check(before == fixture.ProgramBytes() && stageBytes == fixture.StageBytes() &&
            !Directory.EnumerateFiles(fixture.Root, "*.old").Any() && !Directory.EnumerateDirectories(fixture.Stage).Any(), "Partial replacement did not restore original bytes/clear only the copied payload");
        return Task.CompletedTask;
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action, UpdateErrorCode code)
    {
        try { action(); }
        catch (UpdateException error) { Check(error.Code == code, $"Expected {code}, got {error.Code}"); return; }
        throw new Exception("Expected prepared update refusal");
    }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "xtranslate-prepared-" + Guid.NewGuid().ToString("N"));
        internal string Stage { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Root, "data", "nested"));
            File.WriteAllText(Path.Combine(Root, "data", "nested", "learning.json"), "fixed data");
            foreach (var (name, content) in new[] { ("XTranslate.exe", "old exe"), ("a.dll", "old a"), ("b.dll", "old b") })
                File.WriteAllText(Path.Combine(Root, name), content);
            var zip = Path.Combine(Root, "candidate.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                foreach (var (name, content) in new[] { ("XTranslate.exe", "new exe"), ("a.dll", "new a"), ("b.dll", "new b") })
                { using var writer = new StreamWriter(archive.CreateEntry(name).Open()); writer.Write(content); }
            Stage = UpdateInstaller.Stage(zip, Root, UpdateFlavor.Portable, From, _ => To.ToString());
            File.Delete(zip);
            PreparedUpdate.Seal(Stage, From, To, UpdateFlavor.Portable);
        }
        internal string ProgramBytes() => JsonSerializer.Serialize(Directory.EnumerateFiles(Root)
            .Append(Path.Combine(Root, "data", "nested", "learning.json")).OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new { name = Path.GetFileName(path), content = Convert.ToBase64String(File.ReadAllBytes(path)) }));
        internal string StageBytes() => JsonSerializer.Serialize(Directory.EnumerateFiles(Stage).OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new { name = Path.GetFileName(path), content = Convert.ToBase64String(File.ReadAllBytes(path)) }));
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
