using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SoundCatcher.Controls;

public sealed class WaveformView : FrameworkElement
{
    private static readonly Brush BackgroundBrush = Frozen(Color.FromRgb(0x10, 0x12, 0x18));
    private static readonly Pen BorderPen = FrozenPen(Color.FromRgb(0x26, 0x2A, 0x36), 1);
    private static readonly Brush WaveBrush = Frozen(Color.FromRgb(0x4F, 0x56, 0x8C));
    private static readonly Brush SelectedWaveBrush = Frozen(Color.FromRgb(0x81, 0x8C, 0xF8));
    private static readonly Brush DimBrush = Frozen(Color.FromArgb(0xA8, 0x05, 0x06, 0x0A));
    private static readonly Pen HandlePen = FrozenPen(Color.FromRgb(0xF4, 0x72, 0xB6), 2);
    private static readonly Brush HandleBrush = Frozen(Color.FromRgb(0xF4, 0x72, 0xB6));
    private static readonly Pen PlayheadPen = FrozenPen(Color.FromRgb(0x34, 0xD3, 0x99), 1.5);

    private const double Pad = 8;
    private const double HandleHit = 10;

    private double[] _peaks = Array.Empty<double>();
    private double _duration = 1;
    private double _start;
    private double _end = 1;
    private double _playhead = -1;
    private int _drag = -1;

    public event EventHandler? SelectionChanged;

    public double[] Peaks
    {
        get => _peaks;
        set
        {
            _peaks = value ?? Array.Empty<double>();
            InvalidateVisual();
        }
    }

    public double Duration
    {
        get => _duration;
        set
        {
            _duration = Math.Max(0.05, value);
            InvalidateVisual();
        }
    }

    public double StartSeconds
    {
        get => _start;
        set
        {
            _start = Math.Clamp(value, 0, Math.Max(0, _end - 0.05));
            InvalidateVisual();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public double EndSeconds
    {
        get => _end;
        set
        {
            _end = Math.Clamp(value, _start + 0.05, _duration);
            InvalidateVisual();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public double PlayheadSeconds
    {
        get => _playhead;
        set
        {
            _playhead = value;
            InvalidateVisual();
        }
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(Frozen(color), thickness);
        pen.Freeze();
        return pen;
    }

    private double XOf(double seconds)
    {
        var usable = Math.Max(1, ActualWidth - Pad * 2);
        return Pad + Math.Clamp(seconds / _duration, 0, 1) * usable;
    }

    private double SecondsOf(double x)
    {
        var usable = Math.Max(1, ActualWidth - Pad * 2);
        return Math.Clamp((x - Pad) / usable, 0, 1) * _duration;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var bounds = new Rect(0.5, 0.5, Math.Max(1, width - 1), Math.Max(1, height - 1));
        dc.DrawRoundedRectangle(BackgroundBrush, BorderPen, bounds, 8, 8);

        var mid = height / 2;
        var amplitude = height / 2 - 12;
        var startX = XOf(_start);
        var endX = XOf(_end);

        if (_peaks.Length > 0)
        {
            var usable = Math.Max(1, width - Pad * 2);
            var step = usable / _peaks.Length;
            var barWidth = Math.Max(1, step - 0.35);

            for (var i = 0; i < _peaks.Length; i++)
            {
                var x = Pad + i * step;
                var value = Math.Clamp(_peaks[i], 0, 1);
                var barHeight = Math.Max(0.8, value * amplitude);
                var inside = x >= startX && x <= endX;
                var rect = new Rect(x, mid - barHeight, barWidth, barHeight * 2);
                dc.DrawRectangle(inside ? SelectedWaveBrush : WaveBrush, null, rect);
            }
        }

        // Area di luar pilihan digelapkan.
        if (startX > Pad)
        {
            dc.DrawRectangle(DimBrush, null, new Rect(Pad, Pad, startX - Pad, height - Pad * 2));
        }
        if (endX < width - Pad)
        {
            dc.DrawRectangle(DimBrush, null, new Rect(endX, Pad, width - Pad - endX, height - Pad * 2));
        }

        dc.DrawLine(HandlePen, new Point(startX, Pad), new Point(startX, height - Pad));
        dc.DrawLine(HandlePen, new Point(endX, Pad), new Point(endX, height - Pad));

        var capWidth = 7.0;
        var capHeight = 22.0;
        dc.DrawRoundedRectangle(HandleBrush, null, new Rect(startX - capWidth / 2, (height - capHeight) / 2, capWidth, capHeight), 3, 3);
        dc.DrawRoundedRectangle(HandleBrush, null, new Rect(endX - capWidth / 2, (height - capHeight) / 2, capWidth, capHeight), 3, 3);

        if (_playhead >= 0)
        {
            var x = XOf(_playhead);
            dc.DrawLine(PlayheadPen, new Point(x, Pad), new Point(x, height - Pad));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var x = e.GetPosition(this).X;
        var startX = XOf(_start);
        var endX = XOf(_end);

        if (Math.Abs(x - startX) <= HandleHit || x < startX)
        {
            _drag = 0;
        }
        else if (Math.Abs(x - endX) <= HandleHit || x > endX)
        {
            _drag = 1;
        }
        else
        {
            _drag = -1;
            return;
        }

        CaptureMouse();
        UpdateDrag(x);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var x = e.GetPosition(this).X;

        if (_drag < 0 || e.LeftButton != MouseButtonState.Pressed)
        {
            Cursor = Math.Abs(x - XOf(_start)) <= HandleHit || Math.Abs(x - XOf(_end)) <= HandleHit
                ? Cursors.SizeWE
                : Cursors.Arrow;
            return;
        }

        UpdateDrag(x);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag >= 0)
        {
            _drag = -1;
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void UpdateDrag(double x)
    {
        var seconds = SecondsOf(x);
        if (_drag == 0) StartSeconds = Math.Min(seconds, _end - 0.1);
        else if (_drag == 1) EndSeconds = Math.Max(seconds, _start + 0.1);
    }
}
