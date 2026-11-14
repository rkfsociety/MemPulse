using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MemPulse.PulseModule.UI;

internal sealed class RamDonutGauge : Control
{
    private double _percent;
    private string _subtitle = "—";

    public RamDonutGauge()
    {
        Width = 148;
        Height = 148;
    }

    public void Update(double percent, string subtitle)
    {
        _percent = Math.Clamp(percent, 0, 100);
        _subtitle = subtitle;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 24)
            return;

        double stroke = 12;
        double radius = (size - stroke) / 2.0 - 2;
        var center = new Point(Bounds.Width / 2.0, Bounds.Height / 2.0);

        var trackPen = new Pen(ModuleTheme.TrackBrush, stroke, lineCap: PenLineCap.Round);
        context.DrawEllipse(null, trackPen, center, radius, radius);

        double sweepAngle = _percent / 100.0 * 359.5;
        if (sweepAngle > 0.5)
        {
            var color = ModuleTheme.SeverityColor(_percent);
            var activePen = new Pen(new SolidColorBrush(color), stroke, lineCap: PenLineCap.Round);
            var geometry = BuildArcGeometry(center, radius, -90, sweepAngle);
            context.DrawGeometry(null, activePen, geometry);
        }

        string pctText = $"{Math.Round(_percent):F0}%";
        var pctFormatted = new FormattedText(
            pctText,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold),
            26,
            ModuleTheme.TextPrimary);

        context.DrawText(
            pctFormatted,
            new Point(center.X - pctFormatted.Width / 2.0, center.Y - pctFormatted.Height / 2.0 - 8));

        var subFormatted = new FormattedText(
            _subtitle,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Medium),
            11,
            ModuleTheme.TextMuted);

        context.DrawText(
            subFormatted,
            new Point(center.X - subFormatted.Width / 2.0, center.Y + 14));
    }

    private static StreamGeometry BuildArcGeometry(Point center, double radius, double startDeg, double sweepDeg)
    {
        double startRad = startDeg * Math.PI / 180.0;
        double endRad = (startDeg + sweepDeg) * Math.PI / 180.0;

        var startPoint = new Point(
            center.X + radius * Math.Cos(startRad),
            center.Y + radius * Math.Sin(startRad));
        var endPoint = new Point(
            center.X + radius * Math.Cos(endRad),
            center.Y + radius * Math.Sin(endRad));

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(startPoint, false);
            ctx.ArcTo(
                endPoint,
                new Size(radius, radius),
                0,
                sweepDeg > 180,
                SweepDirection.Clockwise);
            ctx.EndFigure(false);
        }
        return geometry;
    }
}
