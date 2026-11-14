using MemPulse.Services;
using SkiaSharp;
using Xunit;

namespace MemPulse.IconTests;

public sealed class IconTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void BrandIcons_AreValidIcoAndPngFiles()
    {
        string icoPath = Path.Combine(AppContext.BaseDirectory, "MemPulse.ico");
        byte[] ico = File.ReadAllBytes(icoPath);
        Assert.True(ico.Length >= 22);
        // ICO header: reserved (0,0), type (1,0), image count >= 1
        Assert.Equal(0, ico[0]);
        Assert.Equal(0, ico[1]);
        Assert.Equal(1, ico[2]);
        Assert.Equal(0, ico[3]);
        Assert.True(BitConverter.ToUInt16(ico, 4) >= 1);

        string pngPath = Path.Combine(AppContext.BaseDirectory, "MemPulse.png");
        byte[] png = File.ReadAllBytes(pngPath);
        Assert.Equal(PngSignature, png[..8]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(74)]
    [InlineData(89)]
    [InlineData(90)]
    [InlineData(100)]
    public void TrayIconRenderer_RendersValidPngAtEdgeCasesAndThresholds(int percent)
    {
        byte[] png = TrayIconRenderer.RenderPng(percent);
        Assert.True(png.Length > 100);
        Assert.Equal(PngSignature, png[..8]);

        using var bitmap = SKBitmap.Decode(png);
        Assert.NotNull(bitmap);
        Assert.Equal(32, bitmap.Width);
        Assert.Equal(32, bitmap.Height);
    }

    [Fact]
    public void TrayIconRenderer_ChangesAppearanceAtCriticalThresholds()
    {
        Assert.NotEqual(TrayIconRenderer.ColorForUsage(20), TrayIconRenderer.ColorForUsage(95));
        byte[] low = TrayIconRenderer.RenderPng(20);
        byte[] high = TrayIconRenderer.RenderPng(95);
        Assert.NotEqual(low, high);
    }
}
