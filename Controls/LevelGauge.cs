using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MobileWheel.Win.Controls;

/// <summary>
/// 水平仪：背景是固定的水平地平线（下方填充地面），前景是指针，
/// 指针按收到的倾角反向旋转，与手机端「指针固定、地平线旋转」呈现的相对关系一致。
/// 倾角为正表示手机屏幕左侧偏低（向左倾斜）。
/// </summary>
public class LevelGauge : Control
{
    /// <summary>横向倾角（度），正值为左侧偏低（向左倾斜）</summary>
    public static readonly StyledProperty<double> AngleProperty =
        AvaloniaProperty.Register<LevelGauge, double>(nameof(Angle));

    /// <summary>判定为“水平”的角度阈值</summary>
    public static readonly StyledProperty<double> ToleranceProperty =
        AvaloniaProperty.Register<LevelGauge, double>(nameof(Tolerance), 0.5d);

    /// <summary>指针最大偏转角度，超出的部分按此截断</summary>
    private const double MaxPointerAngle = 90d;

    /// <summary>圆角半径</summary>
    private const double CornerRadius = 18d;

    private static readonly IBrush WindowBrush = new SolidColorBrush(Color.Parse("#263238"));
    private static readonly IBrush GroundLevelBrush = new SolidColorBrush(Color.Parse("#2E7D32"));
    private static readonly IBrush GroundTiltBrush = new SolidColorBrush(Color.Parse("#33691E"));
    private static readonly IPen FramePen = new Pen(new SolidColorBrush(Color.Parse("#475569")), 2);
    private static readonly IPen HorizonPen = new Pen(new SolidColorBrush(Color.Parse("#ECEFF1")), 2);
    private static readonly IBrush LevelBrush = new SolidColorBrush(Color.Parse("#66BB6A"));
    private static readonly IBrush TiltBrush = new SolidColorBrush(Color.Parse("#FFA726"));

    static LevelGauge()
    {
        AffectsRender<LevelGauge>(AngleProperty, ToleranceProperty);
    }

    public double Angle
    {
        get => GetValue(AngleProperty);
        set => SetValue(AngleProperty, value);
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

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var half = size / 2 - 2;
        var window = new Rect(center.X - half, center.Y - half, half * 2, half * 2);
        var isLevel = Math.Abs(Angle) <= Tolerance;
        var accent = isLevel ? LevelBrush : TiltBrush;
        var radius = Math.Min(CornerRadius, half);

        // 背景：底盘 + 地面 + 固定的水平地平线
        using (context.PushClip(new RoundedRect(window, radius)))
        {
            context.FillRectangle(WindowBrush, window);
            context.FillRectangle(
                isLevel ? GroundLevelBrush : GroundTiltBrush,
                new Rect(window.X, center.Y, window.Width, window.Bottom - center.Y));
            context.DrawLine(HorizonPen, new Point(window.X, center.Y), new Point(window.Right, center.Y));
        }

        context.DrawRectangle(null, FramePen, new RoundedRect(window, radius));

        // 前景：指针（含中心点）
        var pointerTip = new Point(center.X, center.Y - half * 0.78);
        var pointerBaseY = center.Y + half * 0.12;
        var halfBase = half * 0.16;

        var pointer = new StreamGeometry();
        using (var geometry = pointer.Open())
        {
            geometry.BeginFigure(pointerTip, true);
            geometry.LineTo(new Point(center.X - halfBase, pointerBaseY));
            geometry.LineTo(new Point(center.X + halfBase, pointerBaseY));
            geometry.EndFigure(true);
        }

        // 手机端指针固定在屏幕上、地平线旋转 angle；等价于地平线固定时指针反向旋转
        var radians = -Math.Clamp(Angle, -MaxPointerAngle, MaxPointerAngle) * Math.PI / 180.0;
        using (context.PushTransform(Matrix.CreateRotation(radians, center)))
        {
            context.DrawGeometry(accent, new Pen(accent, 1), pointer);
            context.DrawEllipse(accent, null, center, 5, 5);
        }
    }
}