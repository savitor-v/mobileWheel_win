using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using MobileWheel.Win.ViewModels;

namespace MobileWheel.Win.Views;

public partial class MainWindow : Window
{
    /// <summary>置顶小窗与工作区边缘的间距（逻辑像素）</summary>
    private const int OverlayMargin = 16;

    private MainWindowViewModel? _viewModel;
    private GamepadOverlay? _overlay;

    public MainWindow()
    {
        InitializeComponent();
        Closed += OnWindowClosed;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = null;
        }

        if (DataContext is MainWindowViewModel viewModel)
        {
            _viewModel = viewModel;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        SyncOverlay();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.GamepadEnabled))
        {
            SyncOverlay();
        }
    }

    /// <summary>勾选「模拟方向盘」时弹出置顶小窗，取消勾选时关闭</summary>
    private void SyncOverlay()
    {
        if (_viewModel is null || !_viewModel.GamepadEnabled)
        {
            CloseOverlay();
            return;
        }

        if (_overlay is not null) return;

        var overlay = new GamepadOverlay { DataContext = _viewModel };
        overlay.Closed += (_, _) => _overlay = null;
        _overlay = overlay;

        // WorkingArea 是逻辑像素，Position 要物理像素，所以按屏幕缩放比换算
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is not null)
        {
            var work = screen.WorkingArea;
            var scaling = screen.Scaling > 0 ? screen.Scaling : 1d;
            var width = (int)Math.Round(overlay.Width * scaling);
            var margin = (int)Math.Round(OverlayMargin * scaling);
            overlay.Position = new PixelPoint(
                work.X + work.Width - width - margin,
                work.Y + margin);
        }

        // 不传 owner，避免被主窗口的层级/最小化影响，保证独立置顶
        overlay.Show();
    }

    private void CloseOverlay()
    {
        var overlay = _overlay;
        _overlay = null;
        overlay?.Close();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = null;
        }

        CloseOverlay();
    }
}