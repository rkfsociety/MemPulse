using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PCPulse.Contracts;

namespace MemPulse.Services;

internal sealed class StandaloneDialogService : IPulseDialogService
{
    private readonly Panel _overlayHost;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public StandaloneDialogService(Panel overlayHost)
    {
        _overlayHost = overlayHost;
        _overlayHost.IsVisible = false;
    }

    public Task ShowInfoAsync(string title, string message, CancellationToken cancellationToken = default) =>
        ShowDialogCoreAsync(title, message, "Понятно", null, isDestructive: false, cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);

    public Task ShowErrorAsync(string title, string message, CancellationToken cancellationToken = default) =>
        ShowDialogCoreAsync(title, message, "Закрыть", null, isDestructive: true, cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);

    public Task<bool> ConfirmAsync(
        string title,
        string message,
        string confirmText = "Продолжить",
        string cancelText = "Отмена",
        bool isDestructive = false,
        CancellationToken cancellationToken = default) =>
        ShowDialogCoreAsync(title, message, confirmText, cancelText, isDestructive, cancellationToken);

    private async Task<bool> ShowDialogCoreAsync(
        string title,
        string message,
        string confirmText,
        string? cancelText,
        bool isDestructive,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var confirmBtn = new Button
                {
                    Content = confirmText,
                    Background = isDestructive ? Brush.Parse("#3B1720") : Brush.Parse("#38BDF8"),
                    Foreground = isDestructive ? Brush.Parse("#F87171") : Brush.Parse("#08111B"),
                    BorderBrush = isDestructive ? Brush.Parse("#7F1D1D") : Brush.Parse("#38BDF8"),
                    BorderThickness = new Thickness(1),
                    FontWeight = FontWeight.SemiBold,
                    Padding = new Thickness(16, 8),
                    CornerRadius = new CornerRadius(8)
                };
                confirmBtn.Click += (_, _) => tcs.TrySetResult(true);

                var buttons = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10
                };

                if (!string.IsNullOrEmpty(cancelText))
                {
                    var cancelBtn = new Button
                    {
                        Content = cancelText,
                        Background = Brush.Parse("#1B2736"),
                        Foreground = Brush.Parse("#F1F5F9"),
                        BorderBrush = Brush.Parse("#253549"),
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(14, 8),
                        CornerRadius = new CornerRadius(8)
                    };
                    cancelBtn.Click += (_, _) => tcs.TrySetResult(false);
                    buttons.Children.Add(cancelBtn);
                }

                buttons.Children.Add(confirmBtn);

                var card = new Border
                {
                    MaxWidth = 460,
                    Background = Brush.Parse("#151F2C"),
                    BorderBrush = isDestructive ? Brush.Parse("#7F1D1D") : Brush.Parse("#253549"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(14),
                    Padding = new Thickness(20),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new StackPanel
                    {
                        Spacing = 12,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = title,
                                FontSize = 16,
                                FontWeight = FontWeight.Bold,
                                Foreground = Brush.Parse("#F1F5F9"),
                                TextWrapping = TextWrapping.Wrap
                            },
                            new TextBlock
                            {
                                Text = message,
                                FontSize = 13.5,
                                Foreground = Brush.Parse("#CBD5E1"),
                                TextWrapping = TextWrapping.Wrap
                            },
                            buttons
                        }
                    }
                };

                var backdrop = new Border
                {
                    Background = Brush.Parse("#B0050A10"),
                    Child = card
                };

                _overlayHost.Children.Clear();
                _overlayHost.Children.Add(backdrop);
                _overlayHost.IsVisible = true;
            });

            try
            {
                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _overlayHost.Children.Clear();
                    _overlayHost.IsVisible = false;
                });
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
