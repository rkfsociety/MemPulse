using System.Drawing;
using MemPulse.Core;
using Xunit;

namespace MemPulse.IconTests;

public sealed class IconTests
{
    [Fact]
    public void BrandIcon_IsValidWindowsIcon()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "MemPulse.ico");
        using var branded = new Icon(path, new Size(32, 32));
        Assert.NotEqual(IntPtr.Zero, branded.Handle);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(74)]
    [InlineData(89)]
    [InlineData(90)]
    [InlineData(100)]
    public void TrayIconRenderer_RendersAtEdgeCasesAndThresholds(int percent)
    {
        using var icon = TrayIconRenderer.Create(percent);
        Assert.NotEqual(IntPtr.Zero, icon.Handle);
        using var bitmap = icon.ToBitmap();
        Assert.True(bitmap.Width > 0 && bitmap.Height > 0);
    }

    [Fact]
    public void TrayIconRenderer_ChangesAppearanceAtCriticalThresholds()
    {
        using var lowIcon = TrayIconRenderer.Create(20);
        using var highIcon = TrayIconRenderer.Create(95);
        using var lowBitmap = lowIcon.ToBitmap();
        using var highBitmap = highIcon.ToBitmap();
        Assert.NotEqual(lowBitmap.GetPixel(1, 15).ToArgb(), highBitmap.GetPixel(1, 15).ToArgb());
    }
}
