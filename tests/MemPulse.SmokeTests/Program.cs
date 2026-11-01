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
Console.WriteLine("All smoke tests passed.");
