using System.Collections.ObjectModel;
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

    private readonly TiltNetClient _net = new();
    private readonly TiltGamepad _gamepad = new();

    private DateTime _lastDataLogAt = DateTime.MinValue;
    private bool _disconnectRequested;
    private bool _disposed;

    /// <summary>当前已连接目标的名字，用于状态栏显示</summary>
    private string? _connectedLabel;

    /// <summary>最近一次使用的连接目标，断线后用于后台自动重连</summary>
    private (string Host, int Port)? _lastEndpoint;

    /// <summary>后台自动重连循环的取消源</summary>
    private CancellationTokenSource? _reconnectCts;

    public MainWindowViewModel()
    {
        _net.Log += OnLog;
        _net.ReadingReceived += OnReadingReceived;
        _net.ConnectionStateChanged += (_, connected) => OnConnectionStateChanged(connected);
        _gamepad.Log += OnLog;

        AppendLog("就绪：手机上点“开始监测”与“开启局域网”，再把手机上显示的 IP:端口填到左侧");
    }

    public ObservableCollection<string> LogEntries { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    private bool _isBusy;

    /// <summary>局域网连接目标，格式 host:port</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string _serverEndpoint = string.Empty;

    [ObservableProperty]
    private string _statusText = "未连接";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AngleText))]
    [NotifyPropertyChangedFor(nameof(DirectionBrush))]
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

    public IBrush StatusBrush => IsConnected ? ConnectedBrush : IdleBrush;

    public IBrush DirectionBrush => Math.Abs(Angle) <= 0.5 ? LevelBrush : TiltBrush;

    public string AngleText => $"{Angle:0.0}°";

    public string ReadingCountText => $"已接收 {ReadingCount} 帧";

    public string LogHeader => $"数据日志（保留最近 {MaxLogLines} 条）";

    [RelayCommand]
    private void ClearLog() => LogEntries.Clear();

    /// <summary>解析 "IP:端口"，格式不对时返回 null</summary>
    private static (string Host, int Port)? ParseEndpoint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var parts = text.Trim().Split(':');
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0])) return null;
        if (!int.TryParse(parts[1], out var port) || port is <= 0 or > 65535) return null;

        return (parts[0].Trim(), port);
    }

    private bool CanConnect() => !IsConnected && !IsBusy && ParseEndpoint(ServerEndpoint) is not null;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task Connect()
    {
        var parsed = ParseEndpoint(ServerEndpoint);
        if (parsed is null) return;
        var (host, port) = parsed.Value;

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
            ? $"虚拟方向盘：已接入 · 左摇杆 X 轴（±{TiltGamepad.FullLockAngle:0}° 满舵）"
            : "虚拟方向盘：接入失败，见日志"
        : "虚拟方向盘：未启用";

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
        if (GamepadEnabled) _gamepad.Update(reading.Angle);

        Dispatcher.UIThread.Post(() =>
        {
            Angle = reading.Angle;
            Direction = string.IsNullOrWhiteSpace(reading.Direction) ? "未知" : reading.Direction;
            RotationText = DescribeRotation(reading.Rotation);
            UpdatedAtText = reading.Timestamp > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(reading.Timestamp).LocalDateTime.ToString("HH:mm:ss.fff")
                : DateTime.Now.ToString("HH:mm:ss.fff");
            ReadingCount++;

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

        _net.Log -= OnLog;
        _net.ReadingReceived -= OnReadingReceived;
        _net.Dispose();

        _gamepad.Log -= OnLog;
        _gamepad.Dispose();
    }
}