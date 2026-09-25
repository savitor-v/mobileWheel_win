using CommunityToolkit.Mvvm.ComponentModel;
using Windows.Devices.Bluetooth;

namespace MobileWheel.Win.Models;

/// <summary>扫描到的 BLE 设备，RSSI 会随扫描结果实时刷新。</summary>
public sealed partial class TiltDevice : ObservableObject
{
    public TiltDevice(
        ulong bluetoothAddress,
        BluetoothAddressType addressType,
        string name,
        short rssi,
        bool isTiltDevice)
    {
        BluetoothAddress = bluetoothAddress;
        AddressType = addressType;
        _name = name;
        _rssi = rssi;
        IsTiltDevice = isTiltDevice;
    }

    public ulong BluetoothAddress { get; }

    /// <summary>
    /// 广播里的地址类型。安卓端用可解析私密地址（随机地址）广播，
    /// 连接时必须带上这个类型，否则会按公有地址去找而连不上。
    /// </summary>
    public BluetoothAddressType AddressType { get; }

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

    /// <summary>手机端把设备名放在扫描响应里，部分情况下拿不到名字，故给个可识别的兜底名</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? (IsTiltDevice ? "水平仪（安卓端）" : "未知设备")
        : Name;

    public string RssiText => $"{Rssi} dBm";
}