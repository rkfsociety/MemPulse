using System.Windows;
using MemPulse.Core;

using Window = System.Windows.Window;
using MessageBox = System.Windows.MessageBox;

namespace MemPulse;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _source;
    public AppSettings Result { get; private set; }

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _source = settings.Copy();
        Result = _source.Copy();
        RefreshText.Text = _source.RefreshSeconds.ToString();
        ThresholdText.Text = _source.AutoCleanThreshold.ToString();
        CooldownText.Text = _source.AutoCleanCooldownMinutes.ToString();
        AutoCleanCheck.IsChecked = _source.AutoClean;
        StartupCheck.IsChecked = _source.StartWithWindows;
        MinimizeCheck.IsChecked = _source.MinimizeToTray;
        CloseCheck.IsChecked = _source.CloseToTray;
        UpdateCheck.IsChecked = _source.CheckForUpdates;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RefreshText.Text, out var refresh) || refresh is < 1 or > 10 ||
            !int.TryParse(ThresholdText.Text, out var threshold) || threshold is < 70 or > 98 ||
            !int.TryParse(CooldownText.Text, out var cooldown) || cooldown is < 5 or > 120)
        {
            MessageBox.Show(this,
                "Интервал обновления: 1–10 сек. Порог: 70–98%. Пауза: 5–120 мин.",
                "Некорректные настройки", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Result = new AppSettings
        {
            RefreshSeconds = refresh,
            AutoCleanThreshold = threshold,
            AutoCleanCooldownMinutes = cooldown,
            AutoClean = AutoCleanCheck.IsChecked == true,
            StartWithWindows = StartupCheck.IsChecked == true,
            MinimizeToTray = MinimizeCheck.IsChecked == true,
            CloseToTray = CloseCheck.IsChecked == true,
            CheckForUpdates = UpdateCheck.IsChecked == true
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
