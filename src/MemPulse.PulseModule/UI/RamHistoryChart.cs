using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MemPulse.PulseModule.UI;

internal sealed class RamHistoryChart : Control
{
    private double[] _samples = [];
    private double _currentPercent;

    public RamHistoryChart()
    {
        MinHeight = 148;
    }

    public void Update(IReadOnlyList<double> samples, double currentPercent)
    {
        _samples = [.. samples];
        _currentPercent = Math.Clamp(currentPercent, 0, 100);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width < 40 || height < 40)
            return;

        const double leftPad = 36;
        const double rightPad = 8;
        const double topPad = 10;
        const double bottomPad = 18;

        double plotWidth = Math.Max(10, width - leftPad - rightPad);
        double plotHeight = Math.Max(10, height - topPad - bottomPad);

        var gridPen = new Pen(ModuleTheme.BorderSubtleBrush, 1);
        var labelTypeface = new Typeface(FontFamily.Default);

        foreach (int level in new[] { 0, 50, 100 })
        {
            double y = topPad + plotHeight * (1.0 - level / 100.0);
            context.DrawLine(gridPen, new Point(leftPad, y), new Point(leftPad + plotWidth, y));

            var label = new FormattedText(
                $"{level}%",
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                labelTypeface,
                10.5,
                ModuleTheme.TextMuted);
            context.DrawText(label, new Point(0, Math.Clamp(y - label.Height / 2.0, 0, height - label.Height)));
        }

        if (_samples.Length == 0)
            return;

        var accent = ModuleTheme.SeverityColor(_currentPercent);
        var linePen = new Pen(new SolidColorBrush(accent), 2.2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var fillBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            [
                new GradientStop(Color.FromArgb(95, accent.R, accent.G, accent.B), 0),
                new GradientStop(Color.FromArgb(8, accent.R, accent.G, accent.B), 1)
            ]
        };

        var points = new Point[_samples.Length];
        if (_samples.Length == 1)
        {
            double y = topPad + plotHeight * (1.0 - Math.Clamp(_samples[0], 0, 100) / 100.0);
            points[0] = new Point(leftPad + plotWidth, y);
            context.DrawEllipse(new SolidColorBrush(accent), null, points[0], 4, 4);
            return;
        }

        for (int i = 0; i < _samples.Length; i++)
        {
            double x = leftPad + plotWidth * i / (_samples.Length - 1);
            double y = topPad + plotHeight * (1.0 - Math.Clamp(_samples[i], 0, 100) / 100.0);
            points[i] = new Point(x, y);
        }

        var areaGeometry = new StreamGeometry();
        using (var ctx = areaGeometry.Open())
        {
            double baseY = topPad + plotHeight;
            ctx.BeginFigure(new Point(points[0].X, baseY), true);
            for (int i = 0; i < points.Length; i++)
                ctx.LineTo(points[i]);
            ctx.LineTo(new Point(points[^1].X, baseY));
            ctx.EndFigure(true);
        }
        context.DrawGeometry(fillBrush, null, areaGeometry);

        var lineGeometry = new StreamGeometry();
        using (var ctx = lineGeometry.Open())
        {
            ctx.BeginFigure(points[0], false);
            for (int i = 1; i < points.Length; i++)
                ctx.LineTo(points[i]);
            ctx.EndFigure(false);
        }
        context.DrawGeometry(null, linePen, lineGeometry);

        var lastPoint = points[^1];
        context.DrawEllipse(new SolidColorBrush(accent), new Pen(ModuleTheme.CardBackground, 2), lastPoint, 4.5, 4.5);
    }
}
