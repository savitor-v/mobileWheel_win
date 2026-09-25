using MobileWheel.Win.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace MobileWheel.Win.Services;

/// <summary>
/// BLE 中心设备（GATT Client）：扫描 → 连接安卓端 → 订阅 Notify → 解析 JSON 行。
/// 协议与安卓端 TiltBleServer 完全对应。
/// </summary>
public sealed class TiltBleClient : IDisposable
{
    /// <summary>对应 TiltBleServer.SERVICE_UUID</summary>
    public static readonly Guid ServiceUuid = new("0000a100-0000-1000-8000-00805f9b34fb");

    /// <summary>对应 TiltBleServer.TILT_UUID（Notify + Read）</summary>
    public static readonly Guid TiltCharacteristicUuid = new("0000a101-0000-1000-8000-00805f9b34fb");

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>服务发现失败后的重试间隔：刚连上的链路偶尔还没准备好</summary>
    private static readonly TimeSpan DiscoveryRetryDelay = TimeSpan.FromMilliseconds(1200);

    private const int DiscoveryAttempts = 3;

    private readonly TiltFrameParser _parser = new();
    private readonly HashSet<ulong> _knownAddresses = new();

    private BluetoothLEAdvertisementWatcher? _watcher;
    private BluetoothLEDevice? _device;
    private GattCharacteristic? _characteristic;
    private bool _disposed;

    /// <summary>发现（或刷新）了一个广播设备，可能在后台线程触发</summary>
    public event EventHandler<TiltDevice>? DeviceDiscovered;

    /// <summary>文本日志，可能在后台线程触发</summary>
    public event EventHandler<string>? Log;

    /// <summary>收到一帧完整的倾斜角数据，可能在后台线程触发</summary>
    public event EventHandler<TiltReading>? ReadingReceived;

    /// <summary>连接状态变化（true=已连接并订阅成功）</summary>
    public event EventHandler<bool>? ConnectionStateChanged;

    public bool IsScanning => _watcher?.Status == BluetoothLEAdvertisementWatcherStatus.Started;

    public bool IsConnected => _characteristic is not null;

    public void StartScan()
    {
        if (_disposed) return;
        StopScan();

        _knownAddresses.Clear();
        _watcher = new BluetoothLEAdvertisementWatcher
        {
            // 安卓端把设备名放在扫描响应里，必须用主动扫描才能拿到名字
            ScanningMode = BluetoothLEScanningMode.Active
        };
        _watcher.Received += OnAdvertisementReceived;
        _watcher.Stopped += OnWatcherStopped;
        _watcher.Start();

        Log?.Invoke(this, "开始扫描 BLE 设备…");
    }

    public void StopScan()
    {
        if (_watcher is null) return;

        _watcher.Received -= OnAdvertisementReceived;
        _watcher.Stopped -= OnWatcherStopped;
        try
        {
            if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
            {
                _watcher.Stop();
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"停止扫描失败: {ex.Message}");
        }

        _watcher = null;
    }

    /// <summary>连接目标设备并订阅通知，失败时抛出异常供界面提示。</summary>
    public async Task ConnectAsync(TiltDevice target)
    {
        if (_disposed) return;

        StopScan();
        Log?.Invoke(this, $"正在连接 {target.DisplayName} ({target.AddressText})…");

        try
        {
            await ConnectCoreAsync(target).WaitAsync(ConnectTimeout);
        }
        catch (TimeoutException)
        {
            ResetConnection();
            throw new TimeoutException($"连接超时：{target.AddressText}");
        }
        catch
        {
            ResetConnection();
            throw;
        }
    }

    private async Task ConnectCoreAsync(TiltDevice target)
    {
        // 清理上一次的残留状态，但不对外发“已断开”，否则界面会在连接过程中触发自动重连扫描
        ResetConnection(notifyStateChange: false);

        var device = await OpenDeviceAsync(target);
        _device = device;
        device.ConnectionStatusChanged += OnConnectionStatusChanged;

        var services = await FindServicesAsync(device);
        var characteristic = await services.Services[0]
            .GetCharacteristicsForUuidAsync(TiltCharacteristicUuid, BluetoothCacheMode.Uncached);
        if (characteristic.Status != GattCommunicationStatus.Success || characteristic.Characteristics.Count == 0)
        {
            throw new InvalidOperationException($"未找到倾斜角特征值 ({characteristic.Status})。");
        }

        var tiltCharacteristic = characteristic.Characteristics[0];
        tiltCharacteristic.ValueChanged += OnValueChanged;
        // 先记下连接状态再订阅：安卓端订阅成功会立刻补发一帧，避免这一帧被当作未连接而丢掉
        _characteristic = tiltCharacteristic;
        _parser.Reset();

        var cccdStatus = await tiltCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (cccdStatus != GattCommunicationStatus.Success)
        {
            TryDetach(tiltCharacteristic);
            _characteristic = null;
            throw new InvalidOperationException($"订阅通知失败 ({cccdStatus})。");
        }

        Log?.Invoke(this, $"已连接并订阅通知：{target.DisplayName}");
        ConnectionStateChanged?.Invoke(this, true);
    }

    /// <summary>
    /// 按广播里的地址类型打开设备。安卓端用随机地址（可解析私密地址）广播，
    /// 若按公有地址去连会直接连不上；两种都试一遍再放弃。
    /// </summary>
    private async Task<BluetoothLEDevice> OpenDeviceAsync(TiltDevice target)
    {
        var candidateTypes = target.AddressType switch
        {
            BluetoothAddressType.Public => new[] { BluetoothAddressType.Public, BluetoothAddressType.Random },
            BluetoothAddressType.Random => new[] { BluetoothAddressType.Random, BluetoothAddressType.Public },
            _ => new[] { BluetoothAddressType.Public, BluetoothAddressType.Random }
        };

        BluetoothLEDevice? device = null;
        foreach (var addressType in candidateTypes)
        {
            device = await BluetoothLEDevice.FromBluetoothAddressAsync(target.BluetoothAddress, addressType);
            if (device is not null)
            {
                if (addressType != target.AddressType)
                {
                    Log?.Invoke(this, $"按 {target.AddressType} 地址类型未找到设备，改用 {addressType} 成功");
                }
                return device;
            }

            Log?.Invoke(this, $"按 {addressType} 地址类型未找到设备");
        }

        throw new InvalidOperationException("系统未找到该蓝牙设备，请确认蓝牙已打开且设备在范围内。");
    }

    /// <summary>
    /// 查询水平仪服务。刚建立链路时首次发现偶发 Unreachable，重试并退回缓存模式。
    /// </summary>
    private async Task<GattDeviceServicesResult> FindServicesAsync(BluetoothLEDevice device)
    {
        GattDeviceServicesResult? result = null;
        for (var attempt = 1; attempt <= DiscoveryAttempts; attempt++)
        {
            var cacheMode = attempt == 1 ? BluetoothCacheMode.Uncached : BluetoothCacheMode.Cached;
            result = await device.GetGattServicesForUuidAsync(ServiceUuid, cacheMode);
            if (result.Status == GattCommunicationStatus.Success && result.Services.Count > 0)
            {
                return result;
            }

            if (attempt < DiscoveryAttempts)
            {
                Log?.Invoke(this, $"第 {attempt} 次服务发现失败 ({result.Status})，{DiscoveryRetryDelay.TotalMilliseconds:0} ms 后重试…");
                await Task.Delay(DiscoveryRetryDelay);
            }
        }

        throw new InvalidOperationException(
            $"未找到水平仪服务 ({result?.Status})，请确认安卓端已开启蓝牙广播，且已点“开始监测”。");
    }

    /// <summary>断开连接（用户主动调用）</summary>
    public async Task DisconnectAsync()
    {
        StopScan();

        var characteristic = _characteristic;
        _characteristic = null;

        if (characteristic is not null)
        {
            try
            {
                await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
            }
            catch (Exception ex)
            {
                Log?.Invoke(this, $"取消订阅失败（可忽略）: {ex.Message}");
            }

            TryDetach(characteristic);
        }

        ResetConnection();
        Log?.Invoke(this, "已断开连接");
    }

    private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var advertisement = args.Advertisement;
        var isTiltDevice = advertisement.ServiceUuids.Contains(ServiceUuid);
        var isNew = _knownAddresses.Add(args.BluetoothAddress);
        if (!isNew && isTiltDevice is false)
        {
            return; // 非目标设备只在首次出现时上报一次，避免刷屏
        }

        // BluetoothAddressType 属性要求 19041+，更旧的系统上只能按公有地址试
        var addressType = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
            ? args.BluetoothAddressType
            : BluetoothAddressType.Public;

        var device = new TiltDevice(
            args.BluetoothAddress,
            addressType,
            advertisement.LocalName ?? string.Empty,
            args.RawSignalStrengthInDBm,
            isTiltDevice);

        DeviceDiscovered?.Invoke(this, device);
    }

    private void OnWatcherStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        if (args.Error != BluetoothError.Success)
        {
            Log?.Invoke(this, $"扫描已停止：{args.Error}（请确认系统蓝牙已打开）");
        }
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected) return;
        if (_characteristic is null) return; // 尚未订阅成功，忽略本次状态抖动

        Log?.Invoke(this, "设备侧已断开连接");
        ResetConnection();
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var buffer = args.CharacteristicValue;
        var bytes = new byte[(int)buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer))
        {
            reader.ReadBytes(bytes);
        }

        foreach (var frame in _parser.Append(bytes))
        {
            if (TiltReading.TryParse(frame, out var reading))
            {
                ReadingReceived?.Invoke(this, reading);
            }
            else
            {
                Log?.Invoke(this, $"无法解析的数据帧: {frame}");
            }
        }
    }

    /// <summary>清空连接相关状态（幂等）；notifyStateChange=false 时不对外发“已断开”。</summary>
    private void ResetConnection(bool notifyStateChange = true)
    {
        var characteristic = _characteristic;
        _characteristic = null;
        if (characteristic is not null)
        {
            TryDetach(characteristic);
        }

        var device = _device;
        _device = null;
        if (device is not null)
        {
            try
            {
                device.ConnectionStatusChanged -= OnConnectionStatusChanged;
                device.Dispose();
            }
            catch (Exception ex)
            {
                Log?.Invoke(this, $"释放蓝牙设备失败（可忽略）: {ex.Message}");
            }
        }

        _parser.Reset();
        if (notifyStateChange)
        {
            ConnectionStateChanged?.Invoke(this, false);
        }
    }

    private void TryDetach(GattCharacteristic characteristic)
    {
        try
        {
            characteristic.ValueChanged -= OnValueChanged;
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"移除通知回调失败（可忽略）: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopScan();
        ResetConnection();
    }
}