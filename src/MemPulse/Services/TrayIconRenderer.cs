using SkiaSharp;

namespace MemPulse.Services;

public static class TrayIconRenderer
{
    public static SKColor ColorForUsage(int percent, bool isCleaning = false)
    {
        if (isCleaning)
            return SKColor.Parse("#38BDF8");

        return percent switch
        {
            >= 88 => SKColor.Parse("#EF4444"),
            >= 72 => SKColor.Parse("#F59E0B"),
            _ => SKColor.Parse("#22C55E")
        };
    }

    public static byte[] RenderPng(int percent, bool isCleaning = false, int size = 32)
    {
        int clampedPercent = Math.Clamp(percent, 0, 100);
        int pixelSize = Math.Clamp(size, 16, 128);

        var info = new SKImageInfo(pixelSize, pixelSize, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var accent = ColorForUsage(clampedPercent, isCleaning);
        float pad = pixelSize * 0.06f;
        var rect = new SKRect(pad, pad, pixelSize - pad, pixelSize - pad);
        float radius = pixelSize * 0.24f;

        using (var bgPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = SKColor.Parse("#0F172A")
        })
        {
            canvas.DrawRoundRect(rect, radius, radius, bgPaint);
        }

        using (var borderPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1.5f, pixelSize * 0.075f),
            Color = accent
        })
        {
            canvas.DrawRoundRect(rect, radius, radius, borderPaint);
        }

        float barHeight = Math.Max(2.5f, pixelSize * 0.14f);
        float barMargin = pixelSize * 0.18f;
        float barY = pixelSize - pad - barMargin * 0.45f;
        float fullBarWidth = Math.Max(4f, pixelSize - 2 * barMargin);
        float fillWidth = fullBarWidth * (clampedPercent / 100f);

        using (var trackPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = SKColor.Parse("#1E293B")
        })
        {
            canvas.DrawRoundRect(new SKRect(barMargin, barY - barHeight, barMargin + fullBarWidth, barY), 2f, 2f, trackPaint);
        }

        if (fillWidth > 0.5f)
        {
            using var fillPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                Color = accent
            };
            canvas.DrawRoundRect(new SKRect(barMargin, barY - barHeight, barMargin + fillWidth, barY), 2f, 2f, fillPaint);
        }

        string label = clampedPercent >= 100 ? "99" : clampedPercent.ToString();
        using (var typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default)
        using (var font = new SKFont(typeface, pixelSize * 0.44f))
        using (var textPaint = new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.White
        })
        {
            float textWidth = font.MeasureText(label);
            float x = (pixelSize - textWidth) / 2f;
            float y = pixelSize * 0.56f;
            canvas.DrawText(label, x, y, SKTextAlign.Left, font, textPaint);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
