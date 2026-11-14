using System.Reflection;
using Avalonia.Controls;
using MemPulse.PulseModule.UI;
using PCPulse.Contracts;

namespace MemPulse.PulseModule;

public sealed class MemPulseModule : IPulseModule
{
    private MemPulseController? _controller;
    private MemPulseRootView? _cachedView;

    public PulseModuleDescriptor Descriptor { get; } = new(
        Id: "mempulse",
        DisplayName: "MemPulse",
        Description: "Мониторинг и очистка оперативной памяти, кэша и процессов",
        ModuleVersion: ResolveModuleVersion(),
        ContractVersion: PulseContractVersion.Current,
        SupportedPlatforms: PulsePlatform.All,
        RequiresElevationForSomeActions: true);

    public async Task InitializeAsync(IPulseHostContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_controller is not null)
            return;

        var controller = new MemPulseController(context);
        await controller.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _controller = controller;
    }

    public Control CreateView()
    {
        if (_controller is null)
            throw new InvalidOperationException("Модуль MemPulse не инициализирован. Вызовите InitializeAsync перед CreateView.");

        if (_cachedView is null)
        {
            _cachedView = new MemPulseRootView(_controller);
            _controller.StartPollingIfUiAvailable();
        }
        else
        {
            _controller.StartPollingIfUiAvailable();
        }

        return _cachedView;
    }

    public Task<PulseStatusSummary?> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        if (_controller is null)
            return Task.FromResult<PulseStatusSummary?>(null);

        return Task.FromResult<PulseStatusSummary?>(_controller.BuildSummary());
    }

    public async ValueTask DisposeAsync()
    {
        _cachedView?.DetachForDispose();
        _cachedView = null;

        if (_controller is not null)
        {
            await _controller.DisposeAsync().ConfigureAwait(false);
            _controller = null;
        }
    }

    private static Version ResolveModuleVersion()
    {
        var asmVersion = typeof(MemPulseModule).Assembly.GetName().Version;
        if (asmVersion is null)
            return new Version(0, 2, 42);
        return new Version(asmVersion.Major, asmVersion.Minor, Math.Max(0, asmVersion.Build));
    }
}
