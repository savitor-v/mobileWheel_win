namespace MobileWheel.Win.Models;

/// <summary>
/// 局域网扫描到的一台设备。
/// <paramref name="Confirmed"/> = 连上端口后确实收到了合法的水平仪数据帧；
/// 只有端口开放（可能是别的程序占用了同一端口）时为 false。
/// </summary>
public sealed record LanDevice(string Ip, bool Confirmed)
{
    public string DisplayText => Confirmed
        ? $"{Ip} · 已确认（水平仪）"
        : $"{Ip} · 端口开放";
}