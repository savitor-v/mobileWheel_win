using CommunityToolkit.Mvvm.ComponentModel;

namespace MobileWheel.Win.Models;

/// <summary>扫描到的 BLE 设备，RSSI 会随扫描结果实时刷新。</summary>
public sealed partial class TiltDevice : ObservableObject
{
    public TiltDevice(ulong bluetoothAddress, string name, short rssi, bool isTiltDevice)
    {
        BluetoothAddress = bluetoothAddress;
        _name = name;
        _rssi = rssi;
        IsTiltDevice = isTiltDevice;
    }

    public ulong BluetoothAddress { get; }

    /// <summary>广播里是否带上了水平仪服务 UUID</summary>
    public bool IsTiltDevice { get; }

    public string AddressText => string.Join(':',
        Enumerable.Range(0, 6).Select(i => ((BluetoothAddress >> (8 * (5 - i))) & 0xFF).ToString("X2")));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RssiText))]
    private short _rssi;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "未知设备" : Name;

    public string RssiText => $"{Rssi} dBm";
}