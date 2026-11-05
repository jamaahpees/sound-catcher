using System.Windows;
using System.Windows.Media;

namespace SoundCatcher.Controls;

public sealed class LevelMeter : FrameworkElement
{
    private static readonly Brush IdleBrush = CreateBrush(Color.FromRgb(0x22, 0x25, 0x32));
    private static readonly Brush LowBrush = CreateBrush(Color.FromRgb(0x81, 0x8C, 0xF8));
    private static readonly Brush MidBrush = CreateBrush(Color.FromRgb(0xC0, 0x84, 0xE8));
    private static readonly Brush HighBrush = CreateBrush(Color.FromRgb(0xF4, 0x3F, 0x5E));

    private const int Bars = 44;

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level),
        typeof(double),
        typeof(LevelMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    private static Brush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        const double gap = 2.5;
        var barWidth = Math.Max(1.5, (width - gap * (Bars - 1)) / Bars);
        var level = Math.Clamp(Level, 0, 1);

        for (var i = 0; i < Bars; i++)
        {
            var threshold = (i + 1) / (double)Bars;
            var active = level >= threshold - 0.001;

            Brush brush;
            if (!active)
            {
                brush = IdleBrush;
            }
            else if (threshold > 0.85)
            {
                brush = HighBrush;
            }
            else if (threshold > 0.62)
            {
                brush = MidBrush;
            }
            else
            {
                brush = LowBrush;
            }

            var barHeight = height * (0.34 + 0.66 * threshold);
            var x = i * (barWidth + gap);
            var y = (height - barHeight) / 2;
            dc.DrawRoundedRectangle(brush, null, new Rect(x, y, barWidth, barHeight), 1.5, 1.5);
        }
    }
}
