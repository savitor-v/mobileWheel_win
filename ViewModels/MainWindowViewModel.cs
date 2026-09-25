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

    private static readonly IBrush ConnectedBrush = new SolidColorBrush(Color.Parse("#34D399"));
    private static readonly IBrush IdleBrush = new SolidColorBrush(Color.Parse("#64748B"));
    private static readonly IBrush LevelBrush = new SolidColorBrush(Color.Parse("#34D399"));
    private static readonly IBrush TiltBrush = new SolidColorBrush(Color.Parse("#FBBF24"));

    private readonly TiltBleClient _ble = new();
    private readonly TiltNetClient _net = new();
    private readonly Dictionary<ulong, TiltDevice> _deviceIndex = new();

    private DateTime _lastDataLogAt = DateTime.MinValue;
    private bool _disconnectRequested;
    private bool _disposed;

    /// <summary>当前已连接目标的名字，用于状态栏显示</summary>
    private string? _connectedLabel;

    public MainWindowViewModel()
    {
        _ble.DeviceDiscovered += OnDeviceDiscovered;
        _ble.Log += OnLog;
        _ble.ReadingReceived += OnReadingReceived;
        _ble.ConnectionStateChanged += (_, connected) => OnConnectionStateChanged(connected, isBle: true);

        _net.Log += OnLog;
        _net.ReadingReceived += OnReadingReceived;
        _net.ConnectionStateChanged += (_, connected) => OnConnectionStateChanged(connected, isBle: false);

        AppendLog("就绪：可扫描 BLE 设备连接，也可直接输入手机的局域网地址连接");
    }

    public ObservableCollection<TiltDevice> Devices { get; } = new();

    public ObservableCollection<string> LogEntries { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScanButtonText))]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConnectLanCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectLanCommand))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConnectLanCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectLanCommand))]
    private bool _isBusy;

    /// <summary>局域网连接目标，格式 host:port</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectLanCommand))]
    private string _serverEndpoint = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private TiltDevice? _selectedDevice;

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

    [ObservableProperty]
    private bool _autoReconnect = true;

    public string ScanButtonText => IsScanning ? "停止扫描" : "开始扫描";

    public IBrush StatusBrush => IsConnected ? ConnectedBrush : IdleBrush;

    public IBrush DirectionBrush => Math.Abs(Angle) <= 0.5 ? LevelBrush : TiltBrush;

    public string AngleText => $"{Angle:0.0}°";

    public string ReadingCountText => $"已接收 {ReadingCount} 帧";

    public string LogHeader => $"数据日志（保留最近 {MaxLogLines} 条）";

    [RelayCommand]
    private void ToggleScan()
    {
        if (IsScanning)
        {
            _ble.StopScan();
            IsScanning = false;
            AppendLog("已停止扫描");
        }
        else
        {
            RestartScan();
        }
    }

    [RelayCommand]
    private void ClearLog() => LogEntries.Clear();

    [RelayCommand]
    private void ClearDevices()
    {
        _deviceIndex.Clear();
        Devices.Clear();
    }

    private bool CanConnect() => SelectedDevice is not null && !IsConnected && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task Connect()
    {
        var target = SelectedDevice;
        if (target is null) return;

        IsBusy = true;
        _connectedLabel = target.DisplayName;
        try
        {
            await _ble.ConnectAsync(target);
        }
        catch (Exception ex)
        {
            _connectedLabel = null;
            AppendLog($"连接失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
            IsScanning = _ble.IsScanning;
        }
    }

    private bool CanDisconnect() => IsConnected && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task Disconnect()
    {
        _disconnectRequested = true;
        IsBusy = true;
        try
        {
            await _ble.DisconnectAsync();
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

    private void RestartScan()
    {
        _deviceIndex.Clear();
        Devices.Clear();
        _ble.StartScan();
        IsScanning = true;
    }

    /// <summary>解析 "IP:端口"，格式不对时返回 null</summary>
    private static (string Host, int Port)? ParseEndpoint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var parts = text.Trim().Split(':');
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0])) return null;
        if (!int.TryParse(parts[1], out var port) || port is <= 0 or > 65535) return null;

        return (parts[0].Trim(), port);
    }

    private bool CanConnectLan() => !IsConnected && !IsBusy && ParseEndpoint(ServerEndpoint) is not null;

    [RelayCommand(CanExecute = nameof(CanConnectLan))]
    private async Task ConnectLan()
    {
        var parsed = ParseEndpoint(ServerEndpoint);
        if (parsed is null) return;
        var (host, port) = parsed.Value;

        IsBusy = true;
        _connectedLabel = $"{host}:{port}";
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

    private bool CanDisconnectLan() => _net.IsConnected && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanDisconnectLan))]
    private async Task DisconnectLan()
    {
        _disconnectRequested = true;
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

    private void OnDeviceDiscovered(object? sender, TiltDevice device)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_deviceIndex.TryGetValue(device.BluetoothAddress, out var existing))
            {
                if (!string.IsNullOrWhiteSpace(device.Name))
                {
                    existing.Name = device.Name;
                }

                existing.Rssi = device.Rssi;
                return;
            }

            _deviceIndex[device.BluetoothAddress] = device;
            InsertSorted(device);
        });
    }

    private void OnReadingReceived(object? sender, TiltReading reading)
    {
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

    private void OnConnectionStateChanged(bool connected, bool isBle)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsConnected = connected;
            IsScanning = _ble.IsScanning;

            if (connected)
            {
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
            UpdateStatusText();

            // 只有蓝牙链路才靠重新扫描恢复；局域网断线由用户重新点“连接”
            if (!userInitiated && AutoReconnect && isBle && !_disposed)
            {
                AppendLog("连接意外断开，自动重新扫描…");
                RestartScan();
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

    private void InsertSorted(TiltDevice device)
    {
        var index = 0;
        while (index < Devices.Count && CompareForList(Devices[index], device) <= 0)
        {
            index++;
        }

        Devices.Insert(index, device);
    }

    /// <summary>水平仪设备排前面，其次按信号强度从强到弱</summary>
    private static int CompareForList(TiltDevice left, TiltDevice right)
    {
        if (left.IsTiltDevice != right.IsTiltDevice)
        {
            return left.IsTiltDevice ? -1 : 1;
        }

        return right.Rssi.CompareTo(left.Rssi);
    }

    private static string DescribeRotation(int rotation) => rotation switch
    {
        1 => "90° 横屏正向",
        2 => "180° 倒置",
        3 => "270° 横屏反向",
        _ => "0° 竖屏"
    };

    private void UpdateStatusText() =>
        StatusText = (IsConnected, IsScanning) switch
        {
            (true, _) => $"已连接 · {_connectedLabel ?? "设备"}",
            (false, true) => "扫描中…",
            (false, false) => "未连接"
        };

    partial void OnIsConnectedChanged(bool value) => UpdateStatusText();

    partial void OnIsScanningChanged(bool value) => UpdateStatusText();

    partial void OnSelectedDeviceChanged(TiltDevice? value) => UpdateStatusText();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _ble.DeviceDiscovered -= OnDeviceDiscovered;
        _ble.Log -= OnLog;
        _ble.ReadingReceived -= OnReadingReceived;
        _ble.Dispose();

        _net.Log -= OnLog;
        _net.ReadingReceived -= OnReadingReceived;
        _net.Dispose();
    }
}