using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using CursorTranslator.Models;
using Control = Avalonia.Controls.Control;
using Point = Avalonia.Point;

namespace CursorTranslator.Services;

public sealed class DailyUsageChart : Control
{
    private static readonly IBrush GridBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#EEE7E0"));
    private static readonly IBrush AxisBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#81766D"));
    private static readonly Avalonia.Media.Color UnitsColor = Avalonia.Media.Color.Parse("#B65D3B");
    private static readonly Avalonia.Media.Color RequestsColor = Avalonia.Media.Color.Parse("#5489A6");
    private static readonly IBrush UnitsBrush = new SolidColorBrush(UnitsColor);
    private static readonly IBrush RequestsBrush = new SolidColorBrush(RequestsColor);
    private static readonly Typeface ChartTypeface = new("Inter", Avalonia.Media.FontStyle.Normal, FontWeight.Normal, FontStretch.Normal);

    private IReadOnlyList<DailyTranslationStatistic> _statistics = [];
    private HoveredPoint? _hoveredPoint;

    public IReadOnlyList<DailyTranslationStatistic> Statistics
    {
        get => _statistics;
        set
        {
            _statistics = value ?? [];
            _hoveredPoint = null;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (!TryGetChartLayout(out var layout)) return;

        var width = Bounds.Width;
        var height = Bounds.Height;
        var data = layout.Data;
        var plot = layout.Plot;
        var regularPen = new Avalonia.Media.Pen(GridBrush, 1);

        DrawLabel(context, "翻译字数", new Point(4, 4), UnitsBrush, 11);
        var requestHeader = CreateText("请求次数", 11, RequestsBrush);
        context.DrawText(requestHeader, new Point(width - requestHeader.Width - 4, 4));

        for (var tick = 0; tick <= 4; tick++)
        {
            var proportion = tick / 4d;
            var y = plot.Bottom - plot.Height * proportion;
            context.DrawLine(regularPen, new Point(plot.Left, y), new Point(plot.Right, y));
            DrawRightAlignedLabel(context, FormatScale(layout.MaxUnits * proportion), plot.Left - 8, y - 7, AxisBrush, 10);
            DrawLabel(context, FormatScale(layout.MaxRequests * proportion), new Point(plot.Right + 8, y - 7), AxisBrush, 10);
        }

        if (data.Length > 0)
        {
            DrawSeries(context, data.Select(item => item.TranslationUnits).ToArray(), layout.MaxUnits, plot, UnitsBrush);
            DrawSeries(context, data.Select(item => item.TranslationRequests).ToArray(), layout.MaxRequests, plot, RequestsBrush);
            DrawHoveredPoint(context, layout);
            DrawDateLabels(context, data, plot, height);
            DrawHoverTooltip(context, layout, width, height);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var next = FindHoveredPoint(e.GetPosition(this));
        if (_hoveredPoint == next) return;

        _hoveredPoint = next;
        Cursor = next is null ? null : new Avalonia.Input.Cursor(StandardCursorType.Hand);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoveredPoint is null) return;

        _hoveredPoint = null;
        Cursor = null;
        InvalidateVisual();
    }

    private HoveredPoint? FindHoveredPoint(Point pointer)
    {
        if (!TryGetChartLayout(out var layout) || layout.Data.Length == 0
            || !layout.Plot.Contains(pointer))
            return null;

        const double hitRadius = 11;
        var maxDistanceSquared = hitRadius * hitRadius;
        HoveredPoint? closest = null;
        for (var index = 0; index < layout.Data.Length; index++)
        {
            var unitsPoint = GetDataPoint(index, layout.Data.Length,
                layout.Data[index].TranslationUnits, layout.MaxUnits, layout.Plot);
            var unitsDistance = DistanceSquared(pointer, unitsPoint);
            if (unitsDistance <= maxDistanceSquared)
            {
                maxDistanceSquared = unitsDistance;
                closest = new HoveredPoint(index, IsUnits: true, unitsPoint);
            }

            var requestsPoint = GetDataPoint(index, layout.Data.Length,
                layout.Data[index].TranslationRequests, layout.MaxRequests, layout.Plot);
            var requestsDistance = DistanceSquared(pointer, requestsPoint);
            if (requestsDistance <= maxDistanceSquared)
            {
                maxDistanceSquared = requestsDistance;
                closest = new HoveredPoint(index, IsUnits: false, requestsPoint);
            }
        }

        return closest;
    }

    private bool TryGetChartLayout(out ChartLayout layout)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        var plot = new Rect(58, 28, width - 116, height - 78);
        if (width <= 150 || height <= 120 || plot.Width <= 0 || plot.Height <= 0)
        {
            layout = default;
            return false;
        }

        var data = _statistics.OrderBy(item => item.Date).ToArray();
        var maxUnits = GetScaleMaximum(data.Select(item => item.TranslationUnits).DefaultIfEmpty().Max());
        var maxRequests = GetScaleMaximum(data.Select(item => item.TranslationRequests).DefaultIfEmpty().Max());
        layout = new ChartLayout(data, plot, maxUnits, maxRequests);
        return true;
    }

    private void DrawHoveredPoint(DrawingContext context, ChartLayout layout)
    {
        if (_hoveredPoint is not { } hovered || hovered.Index >= layout.Data.Length) return;
        var brush = hovered.IsUnits ? UnitsBrush : RequestsBrush;
        context.DrawEllipse(brush, new Avalonia.Media.Pen(Avalonia.Media.Brushes.White, 2),
            hovered.Position, 5.8, 5.8);
    }

    private void DrawHoverTooltip(DrawingContext context, ChartLayout layout, double width, double height)
    {
        if (_hoveredPoint is not { } hovered || hovered.Index >= layout.Data.Length) return;

        const double tooltipWidth = 196;
        const double tooltipHeight = 72;
        const double gap = 12;
        var x = hovered.Position.X + gap;
        if (x + tooltipWidth > width - 5)
            x = hovered.Position.X - tooltipWidth - gap;

        var y = hovered.Position.Y - tooltipHeight - gap;
        if (y < 5) y = hovered.Position.Y + gap;
        x = Math.Clamp(x, 5, Math.Max(5, width - tooltipWidth - 5));
        y = Math.Clamp(y, 5, Math.Max(5, height - tooltipHeight - 5));

        var box = new Rect(x, y, tooltipWidth, tooltipHeight);
        var background = new SolidColorBrush(Avalonia.Media.Color.Parse("#FFFEFC"));
        var border = new Avalonia.Media.Pen(new SolidColorBrush(Avalonia.Media.Color.Parse("#DCD2C8")), 1);
        context.DrawRectangle(background, border, box, 9, 9);

        var statistic = layout.Data[hovered.Index];
        DrawLabel(context, statistic.Date.ToString("yyyy/M/d", CultureInfo.InvariantCulture),
            new Point(box.X + 11, box.Y + 8), AxisBrush, 11);
        DrawTooltipRow(context, "翻译字数", statistic.TranslationUnits.ToString("N0", CultureInfo.CurrentCulture),
            "字", UnitsBrush, box.X + 11, box.Y + 29);
        DrawTooltipRow(context, "请求次数", statistic.TranslationRequests.ToString("N0", CultureInfo.CurrentCulture),
            "次", RequestsBrush, box.X + 11, box.Y + 49);
    }

    private static void DrawTooltipRow(
        DrawingContext context,
        string label,
        string value,
        string unit,
        IBrush brush,
        double x,
        double y)
    {
        context.DrawEllipse(brush, null, new Point(x + 4, y + 7), 3.2, 3.2);
        var text = CreateText($"{label}  {value} {unit}", 11, AxisBrush);
        context.DrawText(text, new Point(x + 12, y));
    }

    private static Point GetDataPoint(int index, int count, long value, double maximum, Rect plot)
    {
        var x = count == 1
            ? plot.Left + plot.Width / 2
            : plot.Left + plot.Width * index / (count - 1d);
        var y = plot.Bottom - plot.Height * Math.Clamp(value / maximum, 0, 1);
        return new Point(x, y);
    }

    private static double DistanceSquared(Point first, Point second)
    {
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        return dx * dx + dy * dy;
    }

    private static void DrawSeries(
        DrawingContext context,
        IReadOnlyList<long> values,
        double maximum,
        Rect plot,
        IBrush brush)
    {
        if (values.Count == 0) return;
        var points = new Point[values.Count];
        for (var index = 0; index < values.Count; index++)
            points[index] = GetDataPoint(index, values.Count, values[index], maximum, plot);

        if (points.Length > 1)
        {
            var geometry = new StreamGeometry();
            using (var geometryContext = geometry.Open())
            {
                geometryContext.BeginFigure(points[0], isFilled: false);
                foreach (var point in points.Skip(1))
                    geometryContext.LineTo(point);
                geometryContext.EndFigure(isClosed: false);
            }
            context.DrawGeometry(null, new Avalonia.Media.Pen(brush, 2.5), geometry);
        }

        foreach (var point in points)
            context.DrawEllipse(brush, new Avalonia.Media.Pen(Avalonia.Media.Brushes.White, 1.5), point, 3.4, 3.4);
    }

    private static void DrawDateLabels(
        DrawingContext context,
        IReadOnlyList<DailyTranslationStatistic> data,
        Rect plot,
        double height)
    {
        var labelCount = Math.Min(6, data.Count);
        for (var label = 0; label < labelCount; label++)
        {
            var index = labelCount == 1
                ? 0
                : (int)Math.Round(label * (data.Count - 1d) / (labelCount - 1));
            var pointX = data.Count == 1
                ? plot.Left + plot.Width / 2
                : plot.Left + plot.Width * index / (data.Count - 1d);
            var text = CreateText(data[index].Date.ToString("M/d", CultureInfo.InvariantCulture), 10, AxisBrush);
            var x = Math.Clamp(pointX - text.Width / 2, plot.Left, plot.Right - text.Width);
            context.DrawText(text, new Point(x, height - 33));
        }
    }

    private static double GetScaleMaximum(long maximum)
    {
        if (maximum <= 0) return 1;
        var raw = maximum / 4d;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var normalized = raw / magnitude;
        var step = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return step * magnitude * 4;
    }

    private static string FormatScale(double value)
        => value >= 1_000_000
            ? (value / 1_000_000).ToString("0.#", CultureInfo.InvariantCulture) + "m"
            : value >= 10_000
                ? (value / 1_000).ToString("0.#", CultureInfo.InvariantCulture) + "k"
                : Math.Round(value).ToString("0", CultureInfo.InvariantCulture);

    private static void DrawLabel(DrawingContext context, string text, Point point, IBrush brush, double size)
        => context.DrawText(CreateText(text, size, brush), point);

    private static void DrawRightAlignedLabel(
        DrawingContext context,
        string text,
        double right,
        double top,
        IBrush brush,
        double size)
    {
        var formatted = CreateText(text, size, brush);
        context.DrawText(formatted, new Point(right - formatted.Width, top));
    }

    private static FormattedText CreateText(string text, double size, IBrush brush)
        => new(text, CultureInfo.CurrentCulture, Avalonia.Media.FlowDirection.LeftToRight, ChartTypeface, size, brush);

    private readonly record struct ChartLayout(
        DailyTranslationStatistic[] Data,
        Rect Plot,
        double MaxUnits,
        double MaxRequests);

    private readonly record struct HoveredPoint(int Index, bool IsUnits, Point Position);
}
