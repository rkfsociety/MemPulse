using MemPulse.Core;

void Assert(bool value, string message)
{
    if (!value) throw new Exception(message);
    Console.WriteLine("PASS " + message);
}
Assert(Format.Bytes(0) == "0 Б", "zero bytes");
Assert(Format.Bytes(1024).Contains("КБ"), "KiB formatting");
Assert(Format.Bytes(1024 * 1024).Contains("МБ"), "MiB formatting");
Assert(Format.Bytes(-1024) == "0 Б", "negative values clamp");
Assert(new MemoryUsage(100, 25, 0, 0, 0).Used == 75, "physical RAM calculation");
Assert(new MemoryUsage(0, 0, 0, 0, 0).UsedPercent == 0, "zero total guard");
Assert(new MemoryUsage(100, 200, 0, 0, 0).Used == 0, "inconsistent availability guard");
var config = AppSettings.Normalize(new AppSettings { RefreshSeconds = -5, AutoCleanThreshold = 120, AutoCleanCooldownMinutes = 0 });
Assert(config.RefreshSeconds == 1 && config.AutoCleanThreshold == 98 &&
       config.AutoCleanCooldownMinutes == 5, "settings validation");
var clone = config.Copy();
clone.AutoClean = true;
Assert(!config.AutoClean, "settings copy isolation");
Assert(RussianText.Processes(1) == "1 процесс" &&
       RussianText.Processes(2) == "2 процесса" &&
       RussianText.Processes(5) == "5 процессов" &&
       RussianText.Processes(11) == "11 процессов" &&
       RussianText.Processes(21) == "21 процесс" &&
       RussianText.Processes(112) == "112 процессов", "Russian process count declension");
Assert(UpdateService.TryParseReleaseTag("v1.2.3", out var parsed) &&
       parsed == new Version(1, 2, 3), "semantic release versions");
Assert(!UpdateService.TryParseReleaseTag("v1.2.3-beta", out _), "reject prerelease tags");
Assert(!UpdateService.TryParseReleaseTag("v1.02.3", out _), "reject noncanonical version");
Assert(UpdateService.IsExpectedAssetUrl(
    new Uri("https://github.com/rkfsociety/MemPulse/releases/download/v1.2.3/MemPulse-Setup-x64.exe"),
    "v1.2.3"), "allow official installer URL");
Assert(!UpdateService.IsExpectedAssetUrl(
    new Uri("https://github.com.attacker.test/rkfsociety/MemPulse/releases/download/v1.2.3/MemPulse-Setup-x64.exe"),
    "v1.2.3"), "reject impostor host");
var snapshot = new ProcessSnapshotCollection<ProcessUsage>();
int resetEvents = 0;
snapshot.CollectionChanged += (_, args) =>
{
    if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
        resetEvents++;
};
snapshot.ReplaceWith([
    new ProcessUsage(1, "Alpha", 1000, 800),
    new ProcessUsage(2, "Beta", 500, 300),
    new ProcessUsage(3, "Gamma", 200, 100)
]);
Assert(snapshot.Count == 3 && resetEvents == 1, "batch process snapshot raises one reset event");
snapshot.ReplaceWith([new ProcessUsage(4, "Delta", 700, 500)]);
Assert(snapshot.Count == 1 && snapshot[0].Pid == 4 && resetEvents == 2,
    "second snapshot replaces all rows atomically");
snapshot.ReplaceWith([]);
Assert(snapshot.Count == 0 && resetEvents == 3, "empty snapshot clears all processes");

var grouped = ProcessGrouping.Build([
    new ProcessUsage(11, "chrome", 4000, 800),
    new ProcessUsage(12, "Chrome", 6000, 1000),
    new ProcessUsage(23, "explorer", 3000, 900)
]);
Assert(grouped.Count == 2, "process grouping merges app names ignoring case");
Assert(grouped[0].Count == 2 && grouped[0].WorkingBytes == 10000 &&
       grouped[0].PrivateBytes == 1800 && grouped[0].Members.Select(x => x.Pid).SequenceEqual([12, 11]),
    "group totals and original child PIDs are preserved");
Assert(grouped[0].RelativeLoad == 100 && grouped[1].RelativeLoad is > 0 and < 100,
    "relative memory load uses largest aggregated application");
Assert(ProcessGrouping.Build([]).Count == 0, "empty process snapshots are valid");

Console.WriteLine("All smoke tests passed.");
