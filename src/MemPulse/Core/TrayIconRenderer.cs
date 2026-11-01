using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;

namespace MemPulse.Core;

/// <summary>
/// Renders a small, high-contrast RAM usage indicator for the Windows notification area.
/// The caller owns the returned Icon and must dispose it after replacing the tray icon.
/// </summary>
public static class TrayIconRenderer
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Create(int usedPercent)
    {
        int percent = Math.Clamp(usedPercent, 0, 100);
        Color accent = percent >= 90
            ? Color.FromArgb(255, 109, 115)
            : percent >= 80
                ? Color.FromArgb(255, 202, 99)
                : Color.FromArgb(95, 228, 193);

        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            graphics.Clear(Color.Transparent);

            using var background = new SolidBrush(Color.FromArgb(15, 28, 36));
            using var outline = new Pen(accent, 1.6f);
            using var bar = new SolidBrush(accent);
            using var shape = new GraphicsPath();

            const int radius = 7;
            shape.AddArc(1, 1, radius * 2, radius * 2, 180, 90);
            shape.AddArc(31 - radius * 2, 1, radius * 2, radius * 2, 270, 90);
            shape.AddArc(31 - radius * 2, 31 - radius * 2, radius * 2, radius * 2, 0, 90);
            shape.AddArc(1, 31 - radius * 2, radius * 2, radius * 2, 90, 90);
            shape.CloseFigure();

            graphics.FillPath(background, shape);
            graphics.DrawPath(outline, shape);

            string label = percent.ToString(CultureInfo.InvariantCulture) + "%";
            float fontSize = percent == 100 ? 11.2f : percent >= 10 ? 13.0f : 15.0f;
            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.White);
            using var layout = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap
            };
            graphics.DrawString(label, font, textBrush, new RectangleF(1, 3, 30, 24), layout);

            using var track = new SolidBrush(Color.FromArgb(46, 66, 74));
            graphics.FillRectangle(track, 5, 26, 22, 2);
            graphics.FillRectangle(bar, 5, 26, (int)Math.Round(22.0 * percent / 100), 2);
        }

        var handle = bitmap.GetHicon();
        try
        {
            // Icon.FromHandle does not own the native icon handle. Clone before releasing it.
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}
