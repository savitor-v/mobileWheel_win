using Avalonia.Controls;
using Avalonia.Input;

namespace MobileWheel.Win.Views;

/// <summary>
/// 「模拟方向盘」勾选后弹出的置顶小窗：实时显示方向盘模拟量与 LT / RT 扳机模拟量。
/// DataContext 由 MainWindow 直接指向同一个 MainWindowViewModel，数据无需额外搬运。
///
/// 窗口是无边框的（<c>SystemDecorations="None"</c>），没有标题栏和按钮，
/// 所以自己接管拖动：在窗口任意位置按下左键都能拖走。
/// </summary>
public partial class GamepadOverlay : Window
{
    public GamepadOverlay()
    {
        InitializeComponent();
        PointerPressed += OnWindowPointerPressed;
    }

    /// <summary>整个窗口都是拖动区：按下左键即进入系统拖动/移动窗口流程</summary>
    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}