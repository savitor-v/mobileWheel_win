using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MobileWheel.Win.Models;
using MobileWheel.Win.Services;

namespace MobileWheel.Win.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private const int MaxLogLines = 200;

    /// <summary>20Hz 的数据不适合全部写进日志，按 200ms 限流</summary>
    private static readonly TimeSpan DataLogInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>断线后自动重连的间隔</summary>
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(3);

    private static readonly IBrush ConnectedBrush = new SolidColorBrush(Color.Parse("#34D399"));
    private static readonly IBrush IdleBrush = new SolidColorBrush(Color.Parse("#64748B"));
    private static readonly IBrush LevelBrush = new SolidColorBrush(Color.Parse("#34D399"));
    private static readonly IBrush TiltBrush = new SolidColorBrush(Color.Parse("#FBBF24"));
    private static readonly IBrush ButtonIdleBrush = new SolidColorBrush(Color.Parse("#475569"));
    private static readonly IBrush ButtonXActiveBrush = new SolidColorBrush(Color.Parse("#38BDF8"));
    private static readonly IBrush ButtonAActiveBrush = new SolidColorBrush(Color.Parse("#4ADE80"));

    private readonly TiltNetClient _net = new();
    private readonly TiltGamepad _gamepad = new();
    private readonly LanScanner _scanner = new();

    private DateTime _lastDataLogAt = DateTime.MinValue;
    private bool _disconnectRequested;
    private bool _disposed;

    /// <summary>当前已连接目标的名字，用于状态栏显示</summary>
    private string? _connectedLabel;

    /// <summary>最近一次使用的连接目标，断线后用于后台自动重连</summary>
    private (string Host, int Port)? _lastEndpoint;

    /// <summary>后台自动重连循环的取消源</summary>
    private CancellationTokenSource? _reconnectCts;

    /// <summary>局域网扫描的取消源</summary>
    private CancellationTokenSource? _scanCts;

    public MainWindowViewModel()
    {
        _net.Log += OnLog;
        _net.ReadingReceived += OnReadingReceived;
        _net.ConnectionStateChanged += (_, connected) => OnConnectionStateChanged(connected);
        _gamepad.Log += OnLog;
        _scanner.Log += OnLog;

        AppendLog("就绪：手机上点“开始监测”与“开启局域网”，再用左侧「扫描设备」或直接填手机 IP");

        _ = RefreshViGEmBusStatusAsync();
    }

    public ObservableCollection<string> LogEntries { get; } = new();

    /// <summary>扫描到的局域网设备（已确认的排在前面）</summary>
    public ObservableCollection<LanDevice> ScanDevices { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConnectSelectedDeviceCommand))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConnectSelectedDeviceCommand))]
    private bool _isBusy;

    /// <summary>手机 IP。端口固定用 <see cref="TiltNetClient.DefaultPort"/>，不用（也不允许）填</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string _serverIp = string.Empty;

    /// <summary>正在扫描局域网</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScanSummary))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConnectSelectedDeviceCommand))]
    private bool _isScanning;

    /// <summary>扫描结果里被选中的设备（选中即回填 IP，可直接连接）</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectSelectedDeviceCommand))]
    private LanDevice? _selectedDevice;

    [ObservableProperty]
    private string _statusText = "未连接";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AngleText))]
    [NotifyPropertyChangedFor(nameof(DirectionBrush))]
    [NotifyPropertyChangedFor(nameof(SteeringText))]
    [NotifyPropertyChangedFor(nameof(SteeringPointerAngle))]
    private double _angle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DirectionBrush))]
    private string _direction = "等待数据";

    [ObservableProperty]
    private string _rotationText = "—";

    [ObservableProperty]
    private string _updatedAtText = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReadingCountText))]
    private int _readingCount;

    /// <summary>意外断线后在后台自动重连</summary>
    [ObservableProperty]
    private bool _autoReconnect = true;

    /// <summary>把手机倾角映射成虚拟 Xbox 360 手柄的左摇杆 X 轴，供游戏当方向盘用</summary>
    [ObservableProperty]
    private bool _gamepadEnabled;

    [ObservableProperty]
    private string _gamepadStatus = "虚拟方向盘：未启用";

    /// <summary>内置安装包对应的 ViGEmBus 驱动状态文案</summary>
    [ObservableProperty]
    private string _viGEmBusStatus = "虚拟手柄驱动：检测中…";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallDriverCommand))]
    private bool _isInstallingDriver;

    /// <summary>手机端左滑动条传来的扳机值（0..255）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LeftTriggerText))]
    [NotifyPropertyChangedFor(nameof(LeftTriggerBarHeight))]
    private int _leftTrigger;

    /// <summary>手机端右滑动条传来的扳机值（0..255）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RightTriggerText))]
    [NotifyPropertyChangedFor(nameof(RightTriggerBarHeight))]
    private int _rightTrigger;

    /// <summary>手机端顶部「X」按键是否按下</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonXBrush))]
    private bool _buttonXPressed;

    /// <summary>手机端顶部「A」按键是否按下</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonABrush))]
    private bool _buttonAPressed;

    public IBrush StatusBrush => IsConnected ? ConnectedBrush : IdleBrush;

    /// <summary>X / A 按键指示灯：按下时点亮</summary>
    public IBrush ButtonXBrush => ButtonXPressed ? ButtonXActiveBrush : ButtonIdleBrush;

    public IBrush ButtonABrush => ButtonAPressed ? ButtonAActiveBrush : ButtonIdleBrush;

    public IBrush DirectionBrush => Math.Abs(Angle) <= 0.5 ? LevelBrush : TiltBrush;

    public string AngleText => $"{Angle:0.0}°";

    /// <summary>倾角映射到摇杆 X 轴的比例（-1..1，负值 = 左打方向）</summary>
    public double SteeringRatio => TiltGamepad.ToRatio(Angle);

    /// <summary>方向盘指示箭头角度：0 居中，向左打方向为正值（箭头向左偏），满舵 ±60°</summary>
    public double SteeringPointerAngle => -SteeringRatio * TiltGamepad.FullLockAngle;

    public string SteeringText => TiltGamepad.ToAxis(Angle) == 0
        ? "居中"
        : $"{(SteeringRatio < 0 ? "左" : "右")} {Math.Abs(SteeringRatio) * 100:0}%";

    public string LeftTriggerText => $"{LeftTrigger} / 255";

    public string RightTriggerText => $"{RightTrigger} / 255";

    /// <summary>进度条用：0..255 的整型转成 double</summary>
    public double LeftTriggerValue => LeftTrigger;

    public double RightTriggerValue => RightTrigger;

    /// <summary>小窗竖条高度基准（逻辑像素），轨道与填充都以此为准</summary>
    public const double TriggerBarHeight = 86;

    public double LeftTriggerBarHeight => LeftTrigger / 255d * TriggerBarHeight;

    public double RightTriggerBarHeight => RightTrigger / 255d * TriggerBarHeight;

    public string ReadingCountText => $"已接收 {ReadingCount} 帧";

    /// <summary>扫描区的一行状态文本</summary>
    public string ScanSummary => IsScanning
        ? "正在扫描…"
        : ScanDevices.Count > 0
            ? $"发现 {ScanDevices.Count} 台设备"
            : "尚未扫描";

    public string LogHeader => $"数据日志（保留最近 {MaxLogLines} 条）";

    [RelayCommand]
    private void ClearLog() => LogEntries.Clear();

    /// <summary>
    /// 从输入框里取出主机地址：端口固定用 <see cref="TiltNetClient.DefaultPort"/>，
    /// 所以用户只填 IP；即使填了 "IP:端口" 也把端口部分丢掉。空字符串返回 null。
    /// </summary>
    private static string? NormalizeHost(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var host = text.Trim();
        var colon = host.IndexOf(':');
        if (colon >= 0) host = host[..colon].Trim();

        return host.Length == 0 ? null : host;
    }

    private bool CanConnect() => !IsConnected && !IsBusy && NormalizeHost(ServerIp) is not null;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task Connect()
    {
        var host = NormalizeHost(ServerIp);
        if (host is null) return;
        var port = TiltNetClient.DefaultPort;

        StopReconnectLoop();
        IsBusy = true;
        _connectedLabel = $"{host}:{port}";
        _lastEndpoint = (host, port);
        try
        {
            await _net.ConnectAsync(host, port);
        }
        catch (Exception ex)
        {
            _connectedLabel = null;
            AppendLog($"连接失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanScan() => !IsScanning;

    /// <summary>扫描本机网段里开放 11223 端口的主机，结果填进设备列表</summary>
    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task Scan()
    {
        IsScanning = true;
        ScanDevices.Clear();
        SelectedDevice = null;

        var cts = new CancellationTokenSource();
        _scanCts = cts;
        try
        {
            var devices = await _scanner.ScanAsync(TiltNetClient.DefaultPort, cts.Token);
            foreach (var device in devices) ScanDevices.Add(device);

            AppendLog(devices.Count == 0
                ? "未发现设备：请确认手机已点「开启局域网」，且电脑与手机在同一个 Wi-Fi"
                : $"扫描完成：发现 {devices.Count} 台设备（已确认 {devices.Count(d => d.Confirmed)} 台）");

            if (devices.Count > 0) SelectedDevice = devices[0];
        }
        catch (OperationCanceledException)
        {
            AppendLog("扫描已取消");
        }
        catch (Exception ex)
        {
            AppendLog($"扫描失败：{ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_scanCts, cts)) _scanCts = null;
            cts.Dispose();
            IsScanning = false;
            OnPropertyChanged(nameof(ScanSummary));
        }
    }

    /// <summary>选中扫描结果时把 IP 回填到输入框，方便直接查看/微调</summary>
    partial void OnSelectedDeviceChanged(LanDevice? value)
    {
        if (value is not null) ServerIp = value.Ip;
    }

    private bool CanConnectSelectedDevice() =>
        SelectedDevice is not null && !IsConnected && !IsBusy && !IsScanning;

    /// <summary>直接连接列表里选中的设备（端口自动用 11223）</summary>
    [RelayCommand(CanExecute = nameof(CanConnectSelectedDevice))]
    private async Task ConnectSelectedDevice()
    {
        if (SelectedDevice is not { } device) return;

        ServerIp = device.Ip;
        await Connect();
    }

    private bool CanDisconnect() => _net.IsConnected && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task Disconnect()
    {
        _disconnectRequested = true;
        StopReconnectLoop();
        IsBusy = true;
        try
        {
            await _net.DisconnectAsync();
        }
        catch (Exception ex)
        {
            AppendLog($"断开时出错：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnAutoReconnectChanged(bool value)
    {
        if (!value) StopReconnectLoop();
    }

    partial void OnGamepadEnabledChanged(bool value)
    {
        if (value) _gamepad.Connect();
        else _gamepad.Disconnect();

        UpdateGamepadStatus();
    }

    private void UpdateGamepadStatus() => GamepadStatus = GamepadEnabled
        ? _gamepad.IsConnected
            ? $"虚拟方向盘：已接入 · 左摇杆 X 轴（倾角 0°~{TiltGamepad.FullLockAngle:0}° 线性映射，超出按满舵截断）· LT/RT 对应左右滑动条 · X/A 对应顶部两个按键"
            : "虚拟方向盘：接入失败，见日志"
        : "虚拟方向盘：未启用";

    /// <summary>启动时后台探一次驱动状态，只显示不安装（装驱动要用户主动点按钮）</summary>
    private async Task RefreshViGEmBusStatusAsync()
    {
        var available = await Task.Run(ViGEmBusInstaller.IsDriverAvailable);
        Dispatcher.UIThread.Post(() => ViGEmBusStatus = available
            ? "虚拟手柄驱动：已安装"
            : "虚拟手柄驱动：未安装（可点上方按钮一键安装）");
    }

    private bool CanInstallDriver() => !IsInstallingDriver;

    /// <summary>一键安装内置的 ViGEmBus 驱动：释放随程序打包的官方安装包并提权静默安装</summary>
    [RelayCommand(CanExecute = nameof(CanInstallDriver))]
    private async Task InstallDriver()
    {
        IsInstallingDriver = true;
        try
        {
            if (await Task.Run(ViGEmBusInstaller.IsDriverAvailable))
            {
                ViGEmBusStatus = "虚拟手柄驱动：已安装";
                AppendLog("ViGEmBus 驱动已就绪，无需安装");
                return;
            }

            ViGEmBusStatus = "虚拟手柄驱动：正在安装…";
            AppendLog("开始安装内置的 ViGEmBus 驱动（会弹出 UAC 提权确认）…");

            var exitCode = await ViGEmBusInstaller.InstallAsync();

            if (await Task.Run(ViGEmBusInstaller.IsDriverAvailable))
            {
                ViGEmBusStatus = "虚拟手柄驱动：已安装";
                AppendLog("ViGEmBus 驱动安装完成，可以勾选「模拟方向盘」了");
                return;
            }

            ViGEmBusStatus = "虚拟手柄驱动：仍未生效，见日志";
            // 0 与 3010（要求重启）都是安装成功，但设备节点要等系统重新加载
            AppendLog(exitCode is 0 or 3010
                ? "安装程序已正常退出，驱动可能要在重启电脑后才生效"
                : $"安装程序退出码 {exitCode}，驱动未生效");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ViGEmBusInstaller.ErrorCancelled)
        {
            ViGEmBusStatus = "虚拟手柄驱动：未安装（已取消提权）";
            AppendLog("已取消 UAC 提权，驱动未安装");
        }
        catch (Exception ex)
        {
            ViGEmBusStatus = "虚拟手柄驱动：安装失败，见日志";
            AppendLog($"安装 ViGEmBus 驱动失败：{ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            IsInstallingDriver = false;
        }
    }

    /// <summary>
    /// 后台重连循环：意外断线后按固定间隔重试，直到连上、用户主动连接/断开或关闭自动重连。
    /// </summary>
    private void StartReconnectLoop()
    {
        StopReconnectLoop();

        if (_lastEndpoint is not { } endpoint) return;

        var cts = new CancellationTokenSource();
        _reconnectCts = cts;

        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested && !_disposed)
            {
                try
                {
                    await Task.Delay(ReconnectInterval, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (cts.IsCancellationRequested || _disposed || _net.IsConnected || IsBusy) continue;

                try
                {
                    await _net.ConnectAsync(endpoint.Host, endpoint.Port);
                    OnLog(this, $"已重新连接 {endpoint.Host}:{endpoint.Port}");
                    return;
                }
                catch (Exception ex)
                {
                    OnLog(this, $"自动重连失败：{ex.Message}");
                }
            }
        });
    }

    private void StopReconnectLoop()
    {
        var cts = _reconnectCts;
        _reconnectCts = null;
        if (cts is null) return;

        cts.Cancel();
        cts.Dispose();
    }

    private void OnReadingReceived(object? sender, TiltReading reading)
    {
        // 在数据线程直接喂给虚拟手柄，省掉一次 UI 线程派发带来的延迟
        if (GamepadEnabled)
        {
            _gamepad.Update(reading.Angle, reading.LeftTrigger, reading.RightTrigger,
                reading.ButtonX, reading.ButtonA);
        }

        Dispatcher.UIThread.Post(() =>
        {
            Angle = reading.Angle;
            Direction = string.IsNullOrWhiteSpace(reading.Direction) ? "未知" : reading.Direction;
            LeftTrigger = reading.LeftTrigger;
            RightTrigger = reading.RightTrigger;
            RotationText = DescribeRotation(reading.Rotation);
            UpdatedAtText = reading.Timestamp > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(reading.Timestamp).LocalDateTime.ToString("HH:mm:ss.fff")
                : DateTime.Now.ToString("HH:mm:ss.fff");
            ReadingCount++;

            // 按键只在状态翻转时写日志，避免 60Hz 刷屏
            if (reading.ButtonX != ButtonXPressed) AppendLog(reading.ButtonX ? "X 键 按下" : "X 键 松开");
            if (reading.ButtonA != ButtonAPressed) AppendLog(reading.ButtonA ? "A 键 按下" : "A 键 松开");
            ButtonXPressed = reading.ButtonX;
            ButtonAPressed = reading.ButtonA;

            var now = DateTime.Now;
            if (now - _lastDataLogAt < DataLogInterval) return;
            _lastDataLogAt = now;
            AppendLog($"角度 {reading.Angle,5:0.0}° · {Direction} · 屏幕 {RotationText} · ts={reading.Timestamp}");
        });
    }

    private void OnConnectionStateChanged(bool connected)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsConnected = connected;

            if (connected)
            {
                _connectedLabel ??= _lastEndpoint is { } endpoint
                    ? $"{endpoint.Host}:{endpoint.Port}"
                    : null;
                AppendLog("连接成功，开始接收倾斜角数据");
                UpdateStatusText();
                return;
            }

            var userInitiated = _disconnectRequested;
            _disconnectRequested = false;
            _connectedLabel = null;

            Angle = 0;
            Direction = "等待数据";
            LeftTrigger = 0;
            RightTrigger = 0;
            ButtonXPressed = false;
            ButtonAPressed = false;
            RotationText = "—";
            UpdatedAtText = "—";
            // 没数据了先把方向盘回中，避免游戏里保持满舵
            _gamepad.Reset();
            UpdateStatusText();

            if (!userInitiated && AutoReconnect && _lastEndpoint is not null && !_disposed)
            {
                AppendLog("连接意外断开，将在后台自动重连…");
                StartReconnectLoop();
            }
        });
    }

    private void OnLog(object? sender, string message) =>
        Dispatcher.UIThread.Post(() => AppendLog(message));

    private void AppendLog(string message)
    {
        LogEntries.Insert(0, $"{DateTime.Now:HH:mm:ss.fff}  {message}");
        while (LogEntries.Count > MaxLogLines)
        {
            LogEntries.RemoveAt(LogEntries.Count - 1);
        }
    }

    private static string DescribeRotation(int rotation) => rotation switch
    {
        1 => "90° 横屏正向",
        2 => "180° 倒置",
        3 => "270° 横屏反向",
        _ => "0° 竖屏"
    };

    private void UpdateStatusText() =>
        StatusText = IsConnected ? $"已连接 · {_connectedLabel ?? "手机"}" : "未连接";

    partial void OnIsConnectedChanged(bool value) => UpdateStatusText();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopReconnectLoop();

        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = null;
        _scanner.Log -= OnLog;

        _net.Log -= OnLog;
        _net.ReadingReceived -= OnReadingReceived;
        _net.Dispose();

        _gamepad.Log -= OnLog;
        _gamepad.Dispose();
    }
}