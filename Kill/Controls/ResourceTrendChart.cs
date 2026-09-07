using System.Windows;
using System.Windows.Media;

namespace Kill.Controls;

public sealed class ResourceTrendChart : FrameworkElement
{
    public static readonly DependencyProperty PrimaryStrokeProperty = DependencyProperty.Register(
        nameof(PrimaryStroke), typeof(Brush), typeof(ResourceTrendChart),
        new FrameworkPropertyMetadata(CreateFrozenBrush(0x0F, 0x8B, 0x8D),
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SecondaryStrokeProperty = DependencyProperty.Register(
        nameof(SecondaryStroke), typeof(Brush), typeof(ResourceTrendChart),
        new FrameworkPropertyMetadata(CreateFrozenBrush(0xD9, 0x77, 0x06),
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridStrokeProperty = DependencyProperty.Register(
        nameof(GridStroke), typeof(Brush), typeof(ResourceTrendChart),
        new FrameworkPropertyMetadata(CreateFrozenBrush(0xD8, 0xDE, 0xE4),
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(ResourceTrendChart),
        new FrameworkPropertyMetadata(1.75, FrameworkPropertyMetadataOptions.AffectsRender),
        value => value is double thickness && double.IsFinite(thickness) && thickness >= 0);

    private IReadOnlyList<double?> _primary = Array.Empty<double?>();
    private IReadOnlyList<double?>? _secondary;
    private double? _fixedMaximum;

    public ResourceTrendChart()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    public Brush PrimaryStroke
    {
        get => (Brush)GetValue(PrimaryStrokeProperty);
        set => SetValue(PrimaryStrokeProperty, value);
    }

    public Brush SecondaryStroke
    {
        get => (Brush)GetValue(SecondaryStrokeProperty);
        set => SetValue(SecondaryStrokeProperty, value);
    }

    public Brush GridStroke
    {
        get => (Brush)GetValue(GridStrokeProperty);
        set => SetValue(GridStrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public void SetSeries(IEnumerable<double?> primary, IEnumerable<double?>? secondary = null,
        double? fixedMaximum = null)
    {
        ArgumentNullException.ThrowIfNull(primary);

        _primary = primary.ToArray();
        _secondary = secondary?.ToArray();
        _fixedMaximum = fixedMaximum is > 0 && double.IsFinite(fixedMaximum.Value)
            ? fixedMaximum
            : null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var width = RenderSize.Width;
        var height = RenderSize.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 1 || height <= 1)
            return;

        var inset = Math.Max(0.5, StrokeThickness / 2);
        var bounds = new Rect(inset, inset, Math.Max(0, width - inset * 2),
            Math.Max(0, height - inset * 2));
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        DrawGrid(drawingContext, bounds);

        var maximum = _fixedMaximum ?? FindAutomaticMaximum();
        DrawSeries(drawingContext, bounds, _primary, PrimaryStroke, maximum);
        if (_secondary is not null)
            DrawSeries(drawingContext, bounds, _secondary, SecondaryStroke, maximum);
    }

    private void DrawGrid(DrawingContext drawingContext, Rect bounds)
    {
        if (GridStroke is null) return;

        var pen = new Pen(GridStroke, 1);
        for (var row = 0; row <= 4; row++)
        {
            var y = bounds.Top + bounds.Height * row / 4;
            drawingContext.DrawLine(pen, new Point(bounds.Left, y), new Point(bounds.Right, y));
        }

        for (var column = 0; column <= 6; column++)
        {
            var x = bounds.Left + bounds.Width * column / 6;
            drawingContext.DrawLine(pen, new Point(x, bounds.Top), new Point(x, bounds.Bottom));
        }
    }

    private void DrawSeries(DrawingContext drawingContext, Rect bounds,
        IReadOnlyList<double?> values, Brush stroke, double maximum)
    {
        if (stroke is null || values.Count == 0) return;

        var pen = new Pen(stroke, StrokeThickness);
        var segment = new List<Point>();
        for (var index = 0; index < values.Count; index++)
        {
            var value = Normalize(values[index]);
            if (value is null)
            {
                DrawSegment(drawingContext, segment, pen);
                segment.Clear();
                continue;
            }

            var x = values.Count == 1
                ? bounds.Left + bounds.Width / 2
                : bounds.Left + bounds.Width * index / (values.Count - 1);
            var y = bounds.Bottom - bounds.Height * Math.Min(value.Value, maximum) / maximum;
            segment.Add(new Point(x, y));
        }

        DrawSegment(drawingContext, segment, pen);
    }

    private double FindAutomaticMaximum()
    {
        var maximum = 0d;
        FindMaximum(_primary, ref maximum);
        if (_secondary is not null) FindMaximum(_secondary, ref maximum);
        return maximum > 0 ? maximum * 1.05 : 1;
    }

    private static void FindMaximum(IReadOnlyList<double?> values, ref double maximum)
    {
        foreach (var rawValue in values)
        {
            var value = Normalize(rawValue);
            if (value is not null) maximum = Math.Max(maximum, value.Value);
        }
    }

    private static void DrawSegment(DrawingContext drawingContext, IReadOnlyList<Point> points, Pen pen)
    {
        if (points.Count == 1)
        {
            var radius = Math.Max(2, pen.Thickness);
            drawingContext.DrawEllipse(pen.Brush, null, points[0], radius, radius);
            return;
        }

        for (var index = 1; index < points.Count; index++)
            drawingContext.DrawLine(pen, points[index - 1], points[index]);
    }

    private static double? Normalize(double? value)
    {
        if (value is null || !double.IsFinite(value.Value)) return null;
        return Math.Max(0, value.Value);
    }

    private static SolidColorBrush CreateFrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
