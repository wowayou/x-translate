using XTranslate.App.Lexicons;
using static TestSupport;

if (args.Length > 0 && args[0] == "--learning-read-child")
{
    var book = await new XTranslate.App.Learning.LearningStore(args[1]).LoadAsync();
    Check(book.Cards.Single().Content.Prompt == "account for", "Learning restart drifted");
    Console.WriteLine("LEARNING_READ_OK");
    return 0;
}
if (args.Length > 0 && args[0] == "--learning-crash-child")
{
    var store = new XTranslate.App.Learning.LearningStore(args[1], _ => Environment.Exit(19));
    var book = await store.LoadAsync();
    await store.ChangeAsync(book, b => b.Add(LearningStorageTests.Entry("new"), LearningStorageTests.Now));
    return 2;
}
if (args.Length > 0 && args[0] == "--read-child")
{
    var store = new LexiconStore(args[1]);
    using var dictionary = await store.OpenDictionaryAsync(args[2]);
    var page = await dictionary.LookupAsync("thread");
    Check(page.Entries.Single().Entry.Definition == args[3], "Restart read drifted");
    Console.WriteLine("RESTART_READ_OK");
    return 0;
}
if (args.Length > 0 && args[0] is "--crash-before-publish-child" or "--crash-after-publish-child")
{
    var before = args[0] == "--crash-before-publish-child";
    var store = new LexiconStore(args[1], point => { if (before && point == StorageCheckpoint.BlobMoved) Environment.Exit(17); });
    var old = (await store.ListAsync()).Single();
    using var pinned = await store.OpenDictionaryAsync(old.Revision.ResourceId);
    using var preview = await store.PrepareDictionaryAsync(args[2], DictionaryInfo, Columns, replace: old);
    var result = await store.CommitAsync(preview);
    Check(!before && result.CleanupPending, "Interrupted-commit fixture did not defer cleanup");
    Environment.Exit(18);
    return 2;
}
if (args.Length > 0 && args[0] == "--busy-child")
{
    await Error(() => new LexiconStore(args[1]).ListAsync(), LexiconStorageError.Busy);
    Console.WriteLine("BUSY_OK");
    return 0;
}
if (args.Length > 0 && args[0] == "--perf-case-child") return await StoragePerformance.RunCase(int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture));
if (args.Length > 0 && args[0] == "--perf-read-child") return await StoragePerformance.ColdRead(args[1], args[2]);
if (args.Length > 0 && args[0] == "--performance") return await StoragePerformance.Run();

var tests = StorageTests.All.Concat(StorageFailureTests.All).Concat(LearningStorageTests.All).ToArray();
var failures = new List<string>();
foreach (var (name, test) in tests)
{
    try { await test().WaitAsync(TimeSpan.FromSeconds(60)); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures.Add($"FAIL {name}: {error}"); }
}
foreach (var failure in failures) Console.WriteLine(failure);
Console.WriteLine($"{tests.Length - failures.Count}/{tests.Length} storage tests passed");
return failures.Count == 0 ? 0 : 1;
