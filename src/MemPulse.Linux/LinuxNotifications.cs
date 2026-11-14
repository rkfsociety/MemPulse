using System.ComponentModel;
using System.Diagnostics;

namespace MemPulse.Linux;

/// <summary>Best-effort desktop notifications on Linux via notify-send.</summary>
public static class LinuxNotifications
{
    public static void TrySend(string title, string body, string urgency = "normal")
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(title))
            return;

        _ = Task.Run(() =>
        {
            try
            {
                var start = new ProcessStartInfo("notify-send")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                start.ArgumentList.Add("--app-name=MemPulse");
                start.ArgumentList.Add("--urgency=" + urgency);
                string iconPath = Path.Combine(AppContext.BaseDirectory, "MemPulse.png");
                if (File.Exists(iconPath))
                {
                    start.ArgumentList.Add("--icon=" + iconPath);
                }
                start.ArgumentList.Add(title);
                start.ArgumentList.Add(body ?? string.Empty);

                using var proc = Process.Start(start);
                proc?.WaitForExit(3000);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                // notify-send is optional on minimal Linux environments.
            }
        });
    }
}
