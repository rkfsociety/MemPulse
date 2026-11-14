using MemPulse.Core;
using MemPulse.Core.Platform.Linux;
using Xunit;

namespace MemPulse.LinuxTests;

public sealed class LinuxTests
{
    [Fact]
    public void ParseMemInfo_ParsesTotalCacheCommitAndFallback()
    {
        const string meminfo = """
MemTotal:        8000000 kB
MemFree:         1000000 kB
MemAvailable:    2500000 kB
Buffers:          100000 kB
Cached:          1200000 kB
SReclaimable:     300000 kB
Committed_AS:    4000000 kB
CommitLimit:     9500000 kB
""";
        var ram = LinuxProcfs.ParseMemInfo(meminfo);
        Assert.Equal(8000000UL * 1024, ram.Total);
        Assert.Equal(2500000UL * 1024, ram.Available);
        Assert.Equal(1500000UL * 1024, ram.Cache);
        Assert.Equal(4000000UL * 1024, ram.Committed);
        Assert.Equal(9500000UL * 1024, ram.CommitLimit);
        Assert.Equal(5500000UL * 1024, ram.Used);
        Assert.True(Math.Abs(ram.UsedPercent - 68.75) < .001);

        var old = LinuxProcfs.ParseMemInfo("MemTotal: 100 kB\nMemFree: 10 kB\nBuffers: 15 kB\nCached: 20 kB");
        Assert.Equal(45UL * 1024, old.Available);

        Assert.Equal(100UL * 1024, LinuxProcfs.ParseMemInfo("MemTotal: 100 kB\nMemAvailable: 999 kB").Available);
        Assert.Throws<InvalidDataException>(() => LinuxProcfs.ParseMemInfo("Cached: 1000 kB"));

        var tabMem = LinuxProcfs.ParseMemInfo("MemTotal:\t100\tkB\nMemAvailable:\t40\tkB");
        Assert.Equal(100UL * 1024, tabMem.Total);
        Assert.Equal(40UL * 1024, tabMem.Available);
    }

    [Fact]
    public void ParseProcessStatus_And_Grouping_WorkCorrectly()
    {
        var p = LinuxProcfs.ParseProcessStatus(123, "Name:\tmy process\nVmRSS:\t1234 kB\nVmData:\t4567 kB");
        Assert.Equal(123, p.Pid);
        Assert.Equal("my process", p.Name);
        Assert.Equal(1234L * 1024, p.WorkingBytes);
        Assert.Equal(4567L * 1024, p.PrivateBytes);

        var groups = ProcessGrouping.Build([p, p with { Pid = 234 }]);
        Assert.Single(groups);
        Assert.Equal(2, groups[0].Count);
        Assert.Same(groups, ProcessGrouping.Filter(groups, ""));
        Assert.Single(ProcessGrouping.Filter(groups, " MY PROCESS "));
        Assert.Single(ProcessGrouping.Filter(groups, "234"));
        Assert.Empty(ProcessGrouping.Filter(groups, "999"));

        var many = ProcessGrouping.Build(Enumerable.Range(1, 250)
            .Select(i => new ProcessUsage(i, $"app{i:D3}", i * 1024L, 0)));
        Assert.Equal(250, many.Count);
        Assert.Equal(250, ProcessGrouping.Filter(many, "app").Count);

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

        var tabProcess = LinuxProcfs.ParseProcessStatus(42, "Name:\ttabbed\nVmRSS:\t8\tkB\nVmData:\t4\tkB");
        Assert.Equal(8192, tabProcess.WorkingBytes);
        Assert.Equal(4096, tabProcess.PrivateBytes);
    }

    [Fact]
    public void TryParseStartTicks_HandlesSpacesBracketsAndTabs()
    {
        string stat = "111 (my proc (test)) S " + string.Join(' ', Enumerable.Repeat("0", 18)) + " 123456 0 0";
        Assert.True(LinuxProcfs.TryParseStartTicks(stat, out var ticks));
        Assert.Equal(123456UL, ticks);

        Assert.False(LinuxProcfs.TryParseStartTicks("broken", out _));

        Assert.True(LinuxProcfs.TryParseStartTicks(stat.Replace(" S ", "\tS\t"), out var tabTicks));
        Assert.Equal(123456UL, tabTicks);
    }

    [Fact]
    public void AutoCleanupPolicy_ValidatesThresholdCooldownAndNaN()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(AutoCleanupPolicy.ShouldClean(91, 90, now, DateTimeOffset.MinValue));
        Assert.False(AutoCleanupPolicy.ShouldClean(89, 90, now, DateTimeOffset.MinValue));
        Assert.False(AutoCleanupPolicy.ShouldClean(91, 90, now, now.AddMinutes(5)));
        Assert.False(AutoCleanupPolicy.ShouldClean(double.NaN, 90, now, DateTimeOffset.MinValue));
    }

    [Fact]
    public void ParseEligibleMappings_FiltersAndAlignsToPageSize()
    {
        const string maps = """
100000-120000 rw-p 00000000 00:00 0
120000-140000 r--p 00000000 00:00 0
140000-160000 rw-s 00000000 00:00 0
160000-180000 rw-p 00000000 00:00 0 [stack]
180000-1a0000 rw-p 00000000 00:00 0 [heap]
1a0000-1c0000 rw-p 00000000 08:01 333 /usr/lib/libfoo.so
""";
        var candidates = LinuxProcessMemory.ParseEligibleMappings(maps, 128 * 1024);
        Assert.Single(candidates);
        Assert.Equal(0x100000UL, candidates[0].Address);
        Assert.Equal(128UL * 1024, candidates[0].Length);
        Assert.Empty(LinuxProcessMemory.ParseEligibleMappings(maps, 0));

        const string guardedMaps = """
200000-220000 rw-p 00000000 08:01 333
220000-240000 rw-p 00000000 00:00 0 [stack]
240000-260000 rw-p 00000000 00:00 0 [anon:managed heap]
260000-280000 rw-p 00000000 00:00 0
""";
        var guarded = LinuxProcessMemory.ParseEligibleMappings(guardedMaps, 256 * 1024, 65536);
        Assert.Equal(2, guarded.Count);
        Assert.Equal(0x240000UL, guarded[0].Address);
        Assert.Equal(0x260000UL, guarded[1].Address);
        Assert.All(guarded, m => Assert.Equal(128UL * 1024, m.Length));

        var limited = LinuxProcessMemory.ParseEligibleMappings(guardedMaps, 100000, 65536);
        Assert.Single(limited);
        Assert.Equal(65536UL, limited[0].Length);

        Assert.Throws<ArgumentOutOfRangeException>(() => LinuxProcessMemory.ParseEligibleMappings(guardedMaps, 4096, 3000));
    }

    [Fact]
    public async Task LinuxSettingsStore_SavesAtomicallyWithoutRaces()
    {
        string settingsDirectory = Path.Combine(Path.GetTempPath(), "MemPulse-linux-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            string settingsPath = Path.Combine(settingsDirectory, "linux-settings.json");
            var store = new SettingsStore(settingsPath);
            await Task.WhenAll(Enumerable.Range(0, 24).Select(i => Task.Run(() =>
                new SettingsStore(settingsPath).Save(new AppSettings { RefreshSeconds = i % 10 + 1 }))));

            Assert.InRange(store.Load().RefreshSeconds, 1, 10);
            Assert.Empty(Directory.EnumerateFiles(settingsDirectory, "*.tmp"));

            if (OperatingSystem.IsLinux())
            {
                var mode = File.GetUnixFileMode(settingsPath);
                Assert.Equal(UnixFileMode.None, mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                                                       UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute));
            }
        }
        finally
        {
            if (Directory.Exists(settingsDirectory)) Directory.Delete(settingsDirectory, true);
        }
    }

    [Fact]
    public void FakeProcfs_ExpandsTruncatedProcessNamesFromCmdline()
    {
        string fakeProcRoot = Path.Combine(Path.GetTempPath(), "mempulse-proc-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(fakeProcRoot, "101"));
            File.WriteAllText(Path.Combine(fakeProcRoot, "101", "status"), "Name:\tbash\nState:\tS (sleeping)\nVmRSS:\t2048 kB\nVmData:\t512 kB\n");
            File.WriteAllText(Path.Combine(fakeProcRoot, "101", "cmdline"), "/bin/other-should-not-override\0");

            Directory.CreateDirectory(Path.Combine(fakeProcRoot, "102"));
            File.WriteAllText(Path.Combine(fakeProcRoot, "102", "status"), "Name:\tWebKitWebProces\nState:\tS (sleeping)\nVmRSS:\t4096 kB\nVmData:\t1024 kB\n");
            File.WriteAllText(Path.Combine(fakeProcRoot, "102", "cmdline"), "/usr/lib/webkit/WebKitWebProcess\0--type=gpu\0");

            var procfs = new LinuxProcfs(fakeProcRoot);
            var procs = procfs.ReadProcesses();
            Assert.Equal(2, procs.Count);
            Assert.Contains(procs, x => x.Pid == 102 && x.Name == "WebKitWebProcess");
            Assert.Contains(procs, x => x.Pid == 101 && x.Name == "bash");
        }
        finally
        {
            if (Directory.Exists(fakeProcRoot)) Directory.Delete(fakeProcRoot, true);
        }
    }

    [Fact]
    public void LiveLinuxProcfs_ReadsMemoryProcessesAndStartTicks()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var live = new LinuxProcfs();
        var snapshot = live.ReadMemory();
        Assert.True(snapshot.Total > 0);
        Assert.True(snapshot.Available <= snapshot.Total);
        Assert.NotEmpty(live.ReadProcesses());
        Assert.True(LinuxProcfs.TryReadStartTicks(Environment.ProcessId, out var liveStart));
        Assert.True(liveStart > 0);
    }

    [Fact]
    public void LinuxProcfs_ParsesSwapAndZram()
    {
        const string meminfo = """
        MemTotal:       16384000 kB
        MemAvailable:    8192000 kB
        Cached:          1024000 kB
        SwapTotal:       4194304 kB
        SwapFree:        3145728 kB
        """;
        var memory = LinuxProcfs.ParseMemInfo(meminfo);
        Assert.Equal(4194304UL * 1024, memory.SwapTotal);
        Assert.Equal(1048576UL * 1024, memory.SwapUsed);

        const string swaps = """
        Filename				Type		Size		Used		Priority
        /dev/zram0                              partition	4194300		524288		100
        /swapfile                               file		2097148		0		-2
        """;
        var (zramTotal, zramUsed) = LinuxProcfs.ParseProcSwapsZram(swaps);
        Assert.Equal(4194300UL * 1024, zramTotal);
        Assert.Equal(524288UL * 1024, zramUsed);
    }
}
