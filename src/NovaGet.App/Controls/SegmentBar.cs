using System.Windows;
using System.Windows.Media;
using NovaGet.Core.Engine;

namespace NovaGet.App.Controls;

/// <summary>
/// The progress dialog's segment map: one bar for the whole file, each segment's received part filled,
/// and a marker at every active connection's current position.
/// </summary>
public sealed class SegmentBar : FrameworkElement
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IReadOnlyList<SegmentProgress>), typeof(SegmentBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TotalSizeProperty = DependencyProperty.Register(
        nameof(TotalSize), typeof(long), typeof(SegmentBar),
        new FrameworkPropertyMetadata(-1L, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush s_track = Frozen(new SolidColorBrush(Color.FromRgb(0xE6, 0xE9, 0xEE)));
    private static readonly Brush s_filled = Frozen(new SolidColorBrush(Color.FromRgb(0x2F, 0x80, 0xED)));
    private static readonly Brush s_activeFilled = Frozen(new SolidColorBrush(Color.FromRgb(0x27, 0xAE, 0x60)));
    private static readonly Brush s_cursor = Frozen(new SolidColorBrush(Color.FromRgb(0x1B, 0x5E, 0x20)));
    private static readonly Pen s_border = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xAF)), 1));
    private static readonly Pen s_divider = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)), 1));

    public SegmentBar()
    {
        SnapsToDevicePixels = true;
        MinHeight = 14;
    }

    public IReadOnlyList<SegmentProgress>? Segments
    {
        get => (IReadOnlyList<SegmentProgress>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public long TotalSize
    {
        get => (long)GetValue(TotalSizeProperty);
        set => SetValue(TotalSizeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width, Math.Max(MinHeight, double.IsInfinity(availableSize.Height) ? 14 : Math.Min(availableSize.Height, 18)));

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 2 || height <= 2)
        {
            return;
        }

        var bounds = new Rect(0.5, 0.5, width - 1, height - 1);
        drawingContext.DrawRectangle(s_track, null, bounds);
        var size = TotalSize;
        var segments = Segments;
        if (size > 0 && segments is { Count: > 0 })
        {
            var scale = bounds.Width / size;
            foreach (var segment in segments)
            {
                var start = bounds.Left + (segment.Start * scale);
                var received = bounds.Left + (Math.Min(segment.Received, segment.End + 1) * scale);
                if (received > start)
                {
                    drawingContext.DrawRectangle(segment.Active ? s_activeFilled : s_filled, null, new Rect(start, bounds.Top, received - start, bounds.Height));
                }

                if (segment.Start > 0)
                {
                    drawingContext.DrawLine(s_divider, new Point(start, bounds.Top), new Point(start, bounds.Bottom));
                }

                if (segment.Active && segment.Received <= segment.End)
                {
                    drawingContext.DrawRectangle(s_cursor, null, new Rect(Math.Max(bounds.Left, received - 1), bounds.Top, 2, bounds.Height));
                }
            }
        }

        drawingContext.DrawRectangle(null, s_border, bounds);
    }

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
