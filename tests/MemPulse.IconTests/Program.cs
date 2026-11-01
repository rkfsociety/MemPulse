using System.Drawing;
using MemPulse.Core;

string path = Path.Combine(AppContext.BaseDirectory, "MemPulse.ico");
using var branded = new Icon(path, new Size(32, 32));
if (branded.Handle == IntPtr.Zero) throw new Exception("Brand icon could not be loaded.");
Console.WriteLine("PASS: MemPulse.ico is a valid Windows icon.");

foreach (int percent in new[] { 0, 4, 74, 89, 90, 100 })
{
    using var icon = TrayIconRenderer.Create(percent);
    if (icon.Handle == IntPtr.Zero)
        throw new Exception("Tray icon creation failed at " + percent + "%.");
    using var bitmap = icon.ToBitmap();
    if (bitmap.Width <= 0 || bitmap.Height <= 0)
        throw new Exception("Empty tray image at " + percent + "%.");
}
Console.WriteLine("PASS: RAM percentage tray icons render at edge cases and thresholds.");

using var lowIcon = TrayIconRenderer.Create(20);
using var highIcon = TrayIconRenderer.Create(95);
using var lowBitmap = lowIcon.ToBitmap();
using var highBitmap = highIcon.ToBitmap();
if (lowBitmap.GetPixel(1, 15).ToArgb() == highBitmap.GetPixel(1, 15).ToArgb())
    throw new Exception("Tray color threshold did not change between low and high RAM usage.");
Console.WriteLine("PASS: Tray usage indicator changes appearance at critical thresholds.");
