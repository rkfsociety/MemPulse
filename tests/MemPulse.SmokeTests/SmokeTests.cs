using System.Collections.Specialized;
using MemPulse.Core;
using Xunit;

namespace MemPulse.SmokeTests;

public sealed class SmokeTests
{
    [Fact]
    public void FormatBytes_FormatsExpectedUnitsAndClampsNegatives()
    {
        Assert.Equal("0 Б", Format.Bytes(0));
        Assert.Contains("КБ", Format.Bytes(1024));
        Assert.Contains("МБ", Format.Bytes(1024 * 1024));
        Assert.Equal("0 Б", Format.Bytes(-1024));
    }

    [Fact]
    public void MemoryUsage_CalculatesUsedAndHandlesEdgeCases()
    {
        Assert.Equal(75UL, new MemoryUsage(100, 25, 0, 0, 0).Used);
        Assert.Equal(0, new MemoryUsage(0, 0, 0, 0, 0).UsedPercent);
        Assert.Equal(0UL, new MemoryUsage(100, 200, 0, 0, 0).Used);
    }

    [Fact]
    public void AppSettings_NormalizesBoundsAndCopiesIndependently()
    {
        var config = AppSettings.Normalize(new AppSettings
        {
            RefreshSeconds = -5,
            AutoCleanThreshold = 120,
            AutoCleanCooldownMinutes = 0
        });
        Assert.Equal(1, config.RefreshSeconds);
        Assert.Equal(98, config.AutoCleanThreshold);
        Assert.Equal(5, config.AutoCleanCooldownMinutes);

        var clone = config.Copy();
        clone.AutoClean = true;
        Assert.False(config.AutoClean);
    }

    [Theory]
    [InlineData(1, "1 приложение")]
    [InlineData(2, "2 приложения")]
    [InlineData(5, "5 приложений")]
    [InlineData(11, "11 приложений")]
    [InlineData(21, "21 приложение")]
    [InlineData(112, "112 приложений")]
    public void RussianText_DeclinesApplications(int count, string expected) =>
        Assert.Equal(expected, RussianText.Applications(count));

    [Theory]
    [InlineData(1, "1 процесс")]
    [InlineData(2, "2 процесса")]
    [InlineData(5, "5 процессов")]
    [InlineData(11, "11 процессов")]
    [InlineData(21, "21 процесс")]
    [InlineData(112, "112 процессов")]
    public void RussianText_DeclinesProcesses(int count, string expected) =>
        Assert.Equal(expected, RussianText.Processes(count));

    [Fact]
    public void UpdateService_ValidatesReleaseTagsAndAssetUrls()
    {
        Assert.True(UpdateService.TryParseReleaseTag("v1.2.3", out var parsed));
        Assert.Equal(new Version(1, 2, 3), parsed);
        Assert.False(UpdateService.TryParseReleaseTag("v1.2.3-beta", out _));
        Assert.False(UpdateService.TryParseReleaseTag("v1.02.3", out _));

        Assert.True(UpdateService.IsExpectedAssetUrl(
            new Uri("https://github.com/rkfsociety/MemPulse/releases/download/v1.2.3/MemPulse-Setup-x64.exe"),
            "v1.2.3"));
        Assert.False(UpdateService.IsExpectedAssetUrl(
            new Uri("https://github.com.attacker.test/rkfsociety/MemPulse/releases/download/v1.2.3/MemPulse-Setup-x64.exe"),
            "v1.2.3"));
    }

    [Fact]
    public void UpdateService_ParsesDigestAndSha256SumsFallback()
    {
        string hex = new string('a', 64);
        Assert.True(UpdateService.TryParseSha256Digest("sha256:" + hex, out var digestBytes));
        Assert.Equal(32, digestBytes.Length);
        Assert.False(UpdateService.TryParseSha256Digest("sha256:short", out _));

        string sums = $"""
            {new string('0', 64)}  MemPulse.exe
            {hex}  MemPulse-Setup-x64.exe
            """;
        Assert.True(UpdateService.TryParseSha256Sums(sums, "MemPulse-Setup-x64.exe", out var sumsBytes));
        Assert.Equal(digestBytes, sumsBytes);

        Assert.True(UpdateService.TryParseSha256Sums(hex, "MemPulse-Setup-x64.exe", out var singleBytes));
        Assert.Equal(digestBytes, singleBytes);

        Assert.False(UpdateService.TryParseSha256Sums("invalid", "MemPulse-Setup-x64.exe", out _));
    }

    [Fact]
    public void ProcessSnapshotCollection_RaisesSingleResetPerBatch()
    {
        var snapshot = new ProcessSnapshotCollection<ProcessUsage>();
        int resetEvents = 0;
        snapshot.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset)
                resetEvents++;
        };

        snapshot.ReplaceWith([
            new ProcessUsage(1, "Alpha", 1000, 800),
            new ProcessUsage(2, "Beta", 500, 300),
            new ProcessUsage(3, "Gamma", 200, 100)
        ]);
        Assert.Equal(3, snapshot.Count);
        Assert.Equal(1, resetEvents);

        snapshot.ReplaceWith([new ProcessUsage(4, "Delta", 700, 500)]);
        Assert.Single(snapshot);
        Assert.Equal(4, snapshot[0].Pid);
        Assert.Equal(2, resetEvents);

        snapshot.ReplaceWith([]);
        Assert.Empty(snapshot);
        Assert.Equal(3, resetEvents);
    }

    [Fact]
    public void ProcessGrouping_AggregatesSaturatesAndSorts()
    {
        var grouped = ProcessGrouping.Build([
            new ProcessUsage(11, "chrome", 4000, 800),
            new ProcessUsage(12, "Chrome", 6000, 1000),
            new ProcessUsage(23, "explorer", 3000, 900)
        ]);
        Assert.Equal(2, grouped.Count);
        Assert.Equal(2, grouped[0].Count);
        Assert.Equal(10000, grouped[0].WorkingBytes);
        Assert.Equal(1800, grouped[0].PrivateBytes);
        Assert.Equal([12, 11], grouped[0].Members.Select(x => x.Pid));
        Assert.Equal(100, grouped[0].RelativeLoad);
        Assert.InRange(grouped[1].RelativeLoad, 0.0001, 99.9999);

        Assert.Empty(ProcessGrouping.Build([]));

        var extreme = ProcessGrouping.Build([
            new ProcessUsage(31, "large", long.MaxValue, long.MaxValue),
            new ProcessUsage(32, "LARGE", 10, 20),
            new ProcessUsage(33, "negative", -1, -1)
        ]);
        Assert.Equal(2, extreme.Count);
        Assert.Equal(long.MaxValue, extreme[0].WorkingBytes);
        Assert.Equal(long.MaxValue, extreme[0].PrivateBytes);
        Assert.Equal(100, extreme[0].RelativeLoad);
        Assert.Equal(0, extreme[1].WorkingBytes);
        Assert.Equal(0, extreme[1].PrivateBytes);
        Assert.Equal(0, extreme[1].RelativeLoad);

        var sortable = ProcessGrouping.Build([
            new ProcessUsage(1, "zeta", 1200, 50),
            new ProcessUsage(2, "alpha", 250, 100),
            new ProcessUsage(3, "ALPHA", 250, 100),
            new ProcessUsage(4, "beta", 800, 900)
        ]);
        Assert.Same(sortable, ProcessGrouping.Sort(sortable, ProcessSortOrder.WorkingMemory));
        Assert.Equal("zeta", sortable[0].Name);
        Assert.Equal(["alpha", "beta", "zeta"], ProcessGrouping.Sort(sortable, ProcessSortOrder.Name).Select(g => g.Name));
        Assert.Equal("beta", ProcessGrouping.Sort(sortable, ProcessSortOrder.PrivateMemory)[0].Name);
        Assert.Equal("alpha", ProcessGrouping.Sort(sortable, ProcessSortOrder.ProcessCount)[0].Name);
    }

    [Fact]
    public async Task SettingsStore_PersistsAtomicallyUnderConcurrency()
    {
        string settingsDirectory = Path.Combine(Path.GetTempPath(), "MemPulse-settings-smoke-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(settingsDirectory);
            string settingsPath = Path.Combine(settingsDirectory, "settings.json");
            Directory.CreateDirectory(settingsPath + ".tmp"); // A leftover legacy staging path must not block saving.
            var store = new SettingsStore(settingsPath);
            store.Save(new AppSettings { RefreshSeconds = 4, AutoCleanThreshold = 85 });

            var loaded = store.Load();
            Assert.Equal(4, loaded.RefreshSeconds);
            Assert.Equal(85, loaded.AutoCleanThreshold);

            await Task.WhenAll(
                Task.Run(() => store.Save(new AppSettings { RefreshSeconds = 5 })),
                Task.Run(() => store.Save(new AppSettings { RefreshSeconds = 6 })));
            Assert.InRange(store.Load().RefreshSeconds, 5, 6);

            await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() =>
                new SettingsStore(settingsPath).Save(new AppSettings { RefreshSeconds = i % 10 + 1 }))));
            Assert.InRange(store.Load().RefreshSeconds, 1, 10);
            Assert.Empty(Directory.EnumerateFiles(settingsDirectory, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(settingsDirectory)) Directory.Delete(settingsDirectory, true);
        }
    }

    [Fact]
    public void ProcessGroupingSnapshotComparer_IgnoresSubPercentJitter()
    {
        var groupSnapshot = new ProcessSnapshotCollection<ProcessGroupUsage>(ProcessGrouping.SnapshotComparer);
        int groupResets = 0;
        groupSnapshot.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset)
                groupResets++;
        };

        groupSnapshot.ReplaceWith([
            new ProcessGroupUsage("Alpha", [new ProcessUsage(1, "Alpha", 1000, 500)], 1000, 500, null, 100.0),
            new ProcessGroupUsage("Beta", [new ProcessUsage(2, "Beta", 500, 200)], 500, 200, null, 50.0)
        ]);
        groupSnapshot.ReplaceWith([
            new ProcessGroupUsage("Alpha", [new ProcessUsage(1, "Alpha", 1000, 500)], 1000, 500, null, 100.0),
            new ProcessGroupUsage("Beta", [new ProcessUsage(2, "Beta", 500, 200)], 500, 200, null, 50.2)
        ]);
        Assert.Equal(1, groupResets);

        groupSnapshot.ReplaceWith([
            new ProcessGroupUsage("Alpha", [new ProcessUsage(1, "Alpha", 1000, 500)], 1000, 500, null, 100.0),
            new ProcessGroupUsage("Beta", [new ProcessUsage(2, "Beta", 550, 200)], 550, 200, null, 55.0)
        ]);
        Assert.Equal(2, groupResets);
    }

    [Fact]
    public void AutoCleanupPolicy_RespectsThresholdAndCooldown()
    {
        var now = DateTime.UtcNow;
        Assert.True(AutoCleanupPolicy.ShouldClean(91, 90, 15, now, DateTime.MinValue));
        Assert.False(AutoCleanupPolicy.ShouldClean(89, 90, 15, now, DateTime.MinValue));
        Assert.False(AutoCleanupPolicy.ShouldClean(91, 90, 15, now, now.AddMinutes(-5)));
        Assert.True(AutoCleanupPolicy.ShouldClean(91, 90, 15, now, now.AddMinutes(-15)));
        Assert.False(AutoCleanupPolicy.ShouldClean(double.NaN, 90, 15, now, DateTime.MinValue));
    }
}
