using System.Diagnostics;
using System.Text.Json;
using XTranslate.Core;

// An isolated updater parent, not the product UI. It holds its own PID alive at exact failure boundaries,
// avoiding production test switches/delays or racing a normal user's exit. No network/settings/clipboard.
if (args.Length != 5 || args[0] != "--prepared-parent" || !int.TryParse(args[3], out var exitCode)) return 2;
var candidate = Path.GetFullPath(args[1]);
var report = Path.GetFullPath(args[2]);
var readyFile = Path.GetFullPath(args[4]);
var install = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
var stage = UpdateInstaller.Stage(candidate, install, UpdateFlavor.Lite, new(0, 4, 0),
    path => System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductVersion, (_, _) => true);
PreparedUpdate.Seal(stage, new(0, 4, 0), new(0, 4, 1), UpdateFlavor.Lite);
var workerDirectory = PreparedUpdate.CopyWorker(stage, install);
var eventName = "Local\\XTranslate.UpdateReady." + Guid.NewGuid().ToString("N");
using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
using var apply = new EventWaitHandle(false, EventResetMode.ManualReset, eventName + ".Apply");
using var current = Process.GetCurrentProcess();
using var worker = Process.Start(new ProcessStartInfo(Path.Combine(workerDirectory, "XTranslate.exe"))
{
    UseShellExecute = false, WorkingDirectory = workerDirectory,
    ArgumentList = { "--finish-update", Environment.ProcessId.ToString(), current.StartTime.ToUniversalTime().Ticks.ToString(),
        report, "exit", eventName }
}) ?? throw new IOException("Owned worker did not start");
if (!ready.WaitOne(TimeSpan.FromSeconds(5))) return 2;
apply.Set();
File.WriteAllText(readyFile, JsonSerializer.Serialize(new { parentProcessId = Environment.ProcessId, updaterProcessId = worker.Id, stage }));
// The runner either kills this exact parent after the handshake, or observes a nonzero/normal exit.
if (exitCode == -1) Thread.Sleep(TimeSpan.FromSeconds(30));
return exitCode;
