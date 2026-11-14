using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Xml.Linq;
using MemPulse.Core;
using MemPulse.Core.Helper;
using MemPulse.Core.Platform;
using MemPulse.Helper;
using MemPulse.PulseModule;
using PCPulse.Contracts;
using Xunit;

namespace MemPulse.SmokeTests;

public sealed class ModuleAndSecurityTests
{
    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "MemPulse.sln")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("Не найден корень репозитория MemPulse.");
    }

    [Fact]
    public void ModuleManifest_IsValid_AndMatchesBuildVersionAndDescriptor()
    {
        string root = FindRepoRoot();
        string manifestPath = Path.Combine(root, "src", "MemPulse.PulseModule", "module.json");
        string propsPath = Path.Combine(root, "src", "Directory.Build.props");

        Assert.True(File.Exists(manifestPath), "module.json отсутствует");
        var propsVersion = XDocument.Load(propsPath).Descendants("Version").First().Value.Trim();

        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var obj = doc.RootElement;

        Assert.Equal("mempulse", obj.GetProperty("id").GetString());
        Assert.Equal("MemPulse", obj.GetProperty("name").GetString());
        Assert.False(string.IsNullOrWhiteSpace(obj.GetProperty("description").GetString()));
        Assert.Equal(propsVersion, obj.GetProperty("version").GetString());
        Assert.Equal(PulseContractVersion.Current, obj.GetProperty("contractVersion").GetString());
        Assert.Equal("MemPulse.PulseModule.dll", obj.GetProperty("entryAssembly").GetString());
        Assert.Equal("MemPulse.PulseModule.MemPulseModule", obj.GetProperty("entryType").GetString());

        var platforms = obj.GetProperty("platforms").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("windows", platforms);
        Assert.Contains("linux", platforms);

        var helpers = obj.GetProperty("helperExecutable");
        Assert.Equal("MemPulse.Helper.exe", helpers.GetProperty("windows").GetString());
        Assert.Equal("MemPulse.Helper", helpers.GetProperty("linux").GetString());

        var module = new MemPulseModule();
        Assert.Equal("mempulse", module.Descriptor.Id);
        Assert.Equal(propsVersion, module.Descriptor.ModuleVersion.ToString(3));
        Assert.Equal(PulsePlatform.All, module.Descriptor.SupportedPlatforms);
        Assert.True(module.Descriptor.RequiresElevationForSomeActions);
    }

    [Fact]
    public void CoreAndModule_DoNotReferenceWpfWinFormsOrChildWindows()
    {
        var forbiddenAssemblies = new[]
        {
            "PresentationFramework",
            "PresentationCore",
            "WindowsBase",
            "System.Windows.Forms",
            "System.Drawing.Common"
        };

        var coreAsm = typeof(AppSettings).Assembly;
        var moduleAsm = typeof(MemPulseModule).Assembly;

        foreach (var asm in new[] { coreAsm, moduleAsm })
        {
            var refs = asm.GetReferencedAssemblies().Select(r => r.Name).ToArray();
            foreach (var forbidden in forbiddenAssemblies)
                Assert.DoesNotContain(forbidden, refs);
        }

        // Verify MemPulse.PulseModule.dll never instantiates or references Avalonia.Controls.Window
        using var stream = File.OpenRead(moduleAsm.Location);
        using var peReader = new PEReader(stream);
        var metadataReader = peReader.GetMetadataReader();
        foreach (var handle in metadataReader.TypeReferences)
        {
            var typeRef = metadataReader.GetTypeReference(handle);
            string ns = metadataReader.GetString(typeRef.Namespace);
            string name = metadataReader.GetString(typeRef.Name);
            Assert.False(
                ns == "Avalonia.Controls" && name == "Window",
                "Модуль MemPulse.PulseModule не имеет права создавать дочерние Window.");
        }
    }

    [Fact]
    public void PlatformServices_ProvideWorkingTelemetryAndPaths()
    {
        var monitor = PlatformServices.CreateMemoryMonitor();
        var mem = monitor.ReadMemory();
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            Assert.True(mem.Total > 0);
            Assert.True(mem.Available <= mem.Total);
        }

        Assert.True(Path.IsPathFullyQualified(SettingsStore.DefaultWindowsPath));
        Assert.EndsWith(Path.Combine("MemPulse", "settings.json"), SettingsStore.DefaultWindowsPath);
        Assert.True(Path.IsPathFullyQualified(SettingsStore.DefaultLinuxPath));
        Assert.EndsWith(Path.Combine("MemPulse", "linux-settings.json"), SettingsStore.DefaultLinuxPath);
    }

    [Fact]
    public async Task PulseModule_Lifecycle_InitializeSummaryAndDisposeWork()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "mempulse-modtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var ctx = new FakeHostContext(tempDir);
            var module = new MemPulseModule();

            Assert.Throws<InvalidOperationException>(() => module.CreateView());
            Assert.Null(await module.GetSummaryAsync());

            await module.InitializeAsync(ctx);
            var summary = await module.GetSummaryAsync();
            Assert.NotNull(summary);
            Assert.Contains("ОЗУ:", summary.Headline);
            Assert.True(ctx.RefreshRequestedCount > 0);

            await module.DisposeAsync();
            await module.DisposeAsync();
            Assert.Null(await module.GetSummaryAsync());
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void HelperCli_RejectsTraversalOutOfRootInvalidNamesAndBadFlags()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "mempulse-helpertest-" + Guid.NewGuid().ToString("N"));
        string tmpSub = Path.Combine(tempDir, "tmp");
        Directory.CreateDirectory(tmpSub);
        try
        {
            string id = Guid.NewGuid().ToString("N");
            string validReport = Path.Combine(tmpSub, $"report-{id}.json");
            string validExclude = Path.Combine(tmpSub, $"mempulse-exclude-{id}.json");
            string validMarker = Path.Combine(tmpSub, $"mempulse-auto-{id}.marker");

            var parsedClean = HelperCli.Parse([
                "--clean-cache", "--standby",
                "--data-dir", tempDir,
                "--report", validReport,
                "--exclude-file", validExclude
            ]);
            Assert.Equal(HelperCommandMode.CleanCache, parsedClean.Mode);
            Assert.True(parsedClean.Standby);

            var parsedAuto = HelperCli.Parse([
                "--auto-clean",
                "--threshold", "85",
                "--cooldown", "15",
                "--parent-pid", "123",
                "--parent-ticks", "4567",
                "--data-dir", tempDir,
                "--marker", validMarker,
                "--report", validReport
            ]);
            Assert.Equal(HelperCommandMode.AutoClean, parsedAuto.Mode);
            Assert.Equal(85, parsedAuto.Threshold);

            // Relative data-dir rejected
            Assert.ThrowsAny<Exception>(() => HelperCli.Parse([
                "--clean-cache", "--standby", "--data-dir", "relative/dir", "--report", validReport
            ]));

            // Traversal '..' rejected
            Assert.ThrowsAny<Exception>(() => HelperCli.Parse([
                "--clean-cache", "--standby", "--data-dir", tempDir, "--report", Path.Combine(tempDir, "..", $"report-{id}.json")
            ]));

            // Out-of-root report rejected
            Assert.ThrowsAny<Exception>(() => HelperCli.Parse([
                "--clean-cache", "--standby", "--data-dir", tempDir, "--report", Path.Combine(Path.GetTempPath(), $"report-{id}.json")
            ]));

            // Arbitrary filename rejected for exclude/marker/report
            Assert.ThrowsAny<Exception>(() => HelperCli.Parse([
                "--clean-cache", "--workingsets", "--data-dir", tempDir, "--report", validReport,
                "--exclude-file", Path.Combine(tmpSub, "not-an-exclude-name.txt")
            ]));

            Assert.ThrowsAny<Exception>(() => HelperCli.Parse([
                "--auto-clean", "--threshold", "85", "--cooldown", "15", "--parent-pid", "1", "--parent-ticks", "2",
                "--data-dir", tempDir, "--marker", Path.Combine(tmpSub, "evil.sh"), "--report", validReport
            ]));

            // Windows Alternate Data Stream syntax rejected
            if (OperatingSystem.IsWindows())
            {
                Assert.ThrowsAny<Exception>(() => HelperCli.Parse([
                    "--clean-cache", "--standby", "--data-dir", tempDir, "--report", validReport + ":hidden"
                ]));
            }

            // Invalid bounds / unknown flags rejected
            Assert.ThrowsAny<Exception>(() => HelperCli.Parse([
                "--trim-process", "0", "--data-dir", tempDir, "--report", validReport
            ]));
            Assert.ThrowsAny<Exception>(() => HelperCli.Parse([
                "--clean-cache", "--standby", "--unknown-flag", "--data-dir", tempDir, "--report", validReport
            ]));
            Assert.ThrowsAny<Exception>(() => HelperCli.Parse([
                "--auto-clean", "--threshold", "50", "--cooldown", "15", "--parent-pid", "1", "--parent-ticks", "2",
                "--data-dir", tempDir, "--marker", validMarker, "--report", validReport
            ]));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void HelperPathGuard_RejectsSymlinkSubdirectoryWhenSupported()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "mempulse-sym-" + Guid.NewGuid().ToString("N"));
        string targetDir = Path.Combine(Path.GetTempPath(), "mempulse-sym-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(targetDir);
        string linkDir = Path.Combine(tempDir, "linked");

        try
        {
            try
            {
                Directory.CreateSymbolicLink(linkDir, targetDir);
            }
            catch (IOException)
            {
                // On unprivileged Windows without Developer Mode, CreateSymbolicLink may throw PrivilegeNotHeld;
                // junction/reparse check on the attribute logic still applies cleanly elsewhere.
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            string reportPath = Path.Combine(linkDir, $"report-{Guid.NewGuid():N}.json");
            Assert.Throws<UnauthorizedAccessException>(() =>
                HelperPathGuard.ResolveSafeReportPath(tempDir, reportPath));
        }
        finally
        {
            if (Directory.Exists(linkDir)) Directory.Delete(linkDir);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
        }
    }

    private sealed class FakeHostContext(string dataDir) : IPulseHostContext
    {
        public string ModuleId => "mempulse";
        public string ModuleDataDirectory => dataDir;
        public string ModuleInstallDirectory => dataDir;
        public Version HostVersion => new(1, 0, 0);
        public string ContractVersion => PulseContractVersion.Current;
        public IPulseDialogService Dialogs { get; } = new FakeDialogService();
        public IPulseElevationService Elevation { get; } = new FakeElevationService();
        public IPulseLogger Logger { get; } = new FakeLogger();
        public IPulseSettingsStore Settings { get; } = new FakeSettingsStore();
        public int RefreshRequestedCount { get; private set; }
        public void RequestSummaryRefresh() => RefreshRequestedCount++;
    }

    private sealed class FakeDialogService : IPulseDialogService
    {
        public Task ShowInfoAsync(string title, string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ShowErrorAsync(string title, string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> ConfirmAsync(string title, string message, string confirmText = "Продолжить", string cancelText = "Отмена", bool isDestructive = false, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class FakeElevationService : IPulseElevationService
    {
        public bool IsElevated => false;
        public Task<int> RunElevatedHelperAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class FakeLogger : IPulseLogger
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class FakeSettingsStore : IPulseSettingsStore
    {
        private readonly Dictionary<string, object> _store = new();
        public Task<T?> LoadAsync<T>(string key, CancellationToken cancellationToken = default) where T : class =>
            Task.FromResult(_store.TryGetValue(key, out var val) ? val as T : null);
        public Task SaveAsync<T>(string key, T value, CancellationToken cancellationToken = default) where T : class
        {
            _store[key] = value;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            _store.Remove(key);
            return Task.CompletedTask;
        }
    }
}
