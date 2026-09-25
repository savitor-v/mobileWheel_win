using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MobileWheel.Win.Controls;

/// <summary>
/// 水平仪刻度盘：外圈 + 中心基准线 + 每 5° 刻度 + 容差提示线 + 随倾角左右移动的气泡。
/// </summary>
public class LevelGauge : Control
{
    public static readonly StyledProperty<double> AngleProperty =
        AvaloniaProperty.Register<LevelGauge, double>(nameof(Angle));

    /// <summary>气泡到达量程边缘对应的角度</summary>
    public static readonly StyledProperty<double> MaxAngleProperty =
        AvaloniaProperty.Register<LevelGauge, double>(nameof(MaxAngle), 30d);

    /// <summary>判定为“水平”的角度阈值</summary>
    public static readonly StyledProperty<double> ToleranceProperty =
        AvaloniaProperty.Register<LevelGauge, double>(nameof(Tolerance), 0.5d);

    private static readonly IBrush FrameBrush = new SolidColorBrush(Color.Parse("#475569"));
    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.Parse("#334155"));
    private static readonly IBrush TickBrush = new SolidColorBrush(Color.Parse("#94A3B8"));
    private static readonly IBrush LevelBrush = new SolidColorBrush(Color.Parse("#34D399"));
    private static readonly IBrush TiltBrush = new SolidColorBrush(Color.Parse("#FBBF24"));

    static LevelGauge()
    {
        AffectsRender<LevelGauge>(AngleProperty, MaxAngleProperty, ToleranceProperty);
    }

    public double Angle
    {
        get => GetValue(AngleProperty);
        set => SetValue(AngleProperty, value);
    }

    public double MaxAngle
    {
        get => GetValue(MaxAngleProperty);
        set => SetValue(MaxAngleProperty, value);
    }

    public double Tolerance
    {
        get => GetValue(ToleranceProperty);
        set => SetValue(ToleranceProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size < 60) return;

        var maxAngle = MaxAngle <= 0 ? 30d : MaxAngle;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = size / 2 - 4;
        var halfAxis = radius - 28;
        var isLevel = Math.Abs(Angle) <= Tolerance;
        var accent = isLevel ? LevelBrush : TiltBrush;

        context.DrawEllipse(null, new Pen(FrameBrush, 2), center, radius, radius);

        context.DrawLine(
            new Pen(TrackBrush, 2),
            new Point(center.X - halfAxis, center.Y),
            new Point(center.X + halfAxis, center.Y));

        var tickPen = new Pen(TickBrush, 1);
        for (var degrees = -maxAngle; degrees <= maxAngle + 0.001; degrees += 5)
        {
            var x = center.X + degrees / maxAngle * halfAxis;
            var height = Math.Abs(degrees % 10) < 0.001 ? 11d : 6d;
            context.DrawLine(tickPen, new Point(x, center.Y - height), new Point(x, center.Y + height));
        }

        var toleranceOffset = Tolerance / maxAngle * halfAxis;
        var tolerancePen = new Pen(LevelBrush, 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
        context.DrawLine(tolerancePen,
            new Point(center.X - toleranceOffset, center.Y - 20),
            new Point(center.X - toleranceOffset, center.Y + 20));
        context.DrawLine(tolerancePen,
            new Point(center.X + toleranceOffset, center.Y - 20),
            new Point(center.X + toleranceOffset, center.Y + 20));

        var clamped = Math.Clamp(Angle, -maxAngle, maxAngle);
        var bubbleX = center.X + clamped / maxAngle * halfAxis;
        context.DrawEllipse(accent, new Pen(Brushes.White, 2), new Point(bubbleX, center.Y), 15, 15);
    }
}