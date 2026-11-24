using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SwiftBeam.Desktop.Controls;

public sealed class ThroughputSparkline : Control
{
    private const int MaxSamples = 60;
    private readonly double[] _samples = new double[MaxSamples];
    private int _count;
    private int _head;

    private static readonly Pen StrokePen = new(SolidColorBrush.Parse("#34D399"), 1.6)
    {
        LineJoin = PenLineJoin.Round,
        LineCap = PenLineCap.Round
    };

    private static readonly IBrush FillBrush = new SolidColorBrush(Color.FromArgb(46, 16, 185, 129));
    private static readonly Pen BaselinePen = new(SolidColorBrush.Parse("#1E293B"), 1);

    public void PushSample(double bytesPerSec)
    {
        _samples[_head] = Math.Max(0, bytesPerSec);
        _head = (_head + 1) % MaxSamples;
        if (_count < MaxSamples)
            _count++;

        InvalidateVisual();
    }

    public void Reset()
    {
        Array.Clear(_samples);
        _count = 0;
        _head = 0;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var rect = Bounds;
        double w = rect.Width;
        double h = rect.Height;
        if (w <= 4 || h <= 4)
            return;

        // Subtle baseline
        context.DrawLine(BaselinePen, new Point(0, h - 1), new Point(w, h - 1));

        if (_count == 0)
            return;

        double maxVal = 0;
        int startIdx = _count < MaxSamples ? 0 : _head;
        for (int i = 0; i < _count; i++)
        {
            double v = _samples[(startIdx + i) % MaxSamples];
            if (v > maxVal) maxVal = v;
        }

        if (maxVal <= 1)
            return;

        double topPad = 2.0;
        double bottomPad = 1.5;
        double usableH = Math.Max(2.0, h - topPad - bottomPad);
        double stepX = MaxSamples > 1 ? w / (MaxSamples - 1) : w;
        int xOffsetSlots = MaxSamples - _count;

        var fillGeo = new StreamGeometry();
        var lineGeo = new StreamGeometry();

        using (var fg = fillGeo.Open())
        using (var lg = lineGeo.Open())
        {
            double firstX = xOffsetSlots * stepX;
            double firstVal = _samples[startIdx];
            double firstY = topPad + usableH * (1.0 - Math.Clamp(firstVal / maxVal, 0, 1));

            fg.BeginFigure(new Point(firstX, h - bottomPad), isFilled: true);
            fg.LineTo(new Point(firstX, firstY));
            lg.BeginFigure(new Point(firstX, firstY), isFilled: false);

            double lastX = firstX;
            for (int i = 1; i < _count; i++)
            {
                double val = _samples[(startIdx + i) % MaxSamples];
                double x = (xOffsetSlots + i) * stepX;
                double y = topPad + usableH * (1.0 - Math.Clamp(val / maxVal, 0, 1));
                fg.LineTo(new Point(x, y));
                lg.LineTo(new Point(x, y));
                lastX = x;
            }

            fg.LineTo(new Point(lastX, h - bottomPad));
            fg.EndFigure(isClosed: true);
            lg.EndFigure(isClosed: false);
        }

        context.DrawGeometry(FillBrush, null, fillGeo);
        context.DrawGeometry(null, StrokePen, lineGeo);
    }
}
