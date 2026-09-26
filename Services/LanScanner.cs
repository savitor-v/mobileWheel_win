using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using MobileWheel.Win.Models;

namespace MobileWheel.Win.Services;

/// <summary>
/// 局域网设备扫描：枚举本机所在的 /24 网段（逐网卡），并发探测指定端口。
/// 端口能连上只说明"有程序在监听"，再读一小会儿数据，能收到一帧合法的
/// 水平仪 JSON 才算确认是手机端（<see cref="LanDevice.Confirmed"/>）。
/// </summary>
public sealed class LanScanner
{
    /// <summary>单个地址的 TCP 连接超时：家庭 Wi-Fi 里 RTT 很小，400ms 足够</summary>
    private const int ProbeTimeoutMs = 400;

    /// <summary>端口开放后等第一帧数据的时间（手机端接入即补发最近一帧，通常立刻就到）</summary>
    private const int ConfirmTimeoutMs = 600;

    /// <summary>并发探测数：254 个地址两轮就能扫完</summary>
    private const int MaxParallel = 128;

    /// <summary>文本日志，可能在后台线程触发</summary>
    public event EventHandler<string>? Log;

    /// <summary>
    /// 扫描本机各网段的 /24 地址（1..254，跳过本机），返回开放了 port 的主机。
    /// 已确认的排在前面，其余按 IP 升序。
    /// </summary>
    public async Task<IReadOnlyList<LanDevice>> ScanAsync(int port, CancellationToken token)
    {
        var targets = BuildTargets();
        if (targets.Count == 0)
        {
            Log?.Invoke(this, "没有找到可用的局域网网卡，无法扫描");
            return Array.Empty<LanDevice>();
        }

        Log?.Invoke(this, $"本机网段共 {targets.Count} 个地址，开始探测端口 {port}…");

        var found = new List<LanDevice>();
        using var gate = new SemaphoreSlim(MaxParallel);
        var tasks = targets.Select(async ip =>
        {
            await gate.WaitAsync(token);
            try
            {
                if (await ProbeAsync(ip, port, token) is { } device)
                {
                    lock (found) found.Add(device);
                }
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);

        return found
            .OrderByDescending(d => d.Confirmed)
            .ThenBy(d => IpKey(d.Ip))
            .ToList();
    }

    /// <summary>本机所有网段的待探测地址（/24 假设：家用 Wi-Fi 基本都是 255.255.255.0）</summary>
    private static List<string> BuildTargets()
    {
        var targets = new List<string>();
        var seen = new HashSet<string>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            IPInterfaceProperties properties;
            try
            {
                properties = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                var octets = unicast.Address.GetAddressBytes();
                if (octets[0] == 169 && octets[1] == 254) continue; // APIPA，扫了也没用

                var self = unicast.Address.ToString();
                var prefix = $"{octets[0]}.{octets[1]}.{octets[2]}";
                for (var host = 1; host <= 254; host++)
                {
                    var ip = $"{prefix}.{host}";
                    if (ip == self) continue;
                    if (seen.Add(ip)) targets.Add(ip);
                }
            }
        }

        return targets;
    }

    /// <summary>探测一个地址：端口开放则返回设备（是否能收到数据帧决定 Confirmed）；无响应返回 null</summary>
    private static async Task<LanDevice?> ProbeAsync(string ip, int port, CancellationToken token)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Parse(ip), port)
                        .WaitAsync(TimeSpan.FromMilliseconds(ProbeTimeoutMs), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            client.Dispose();
            throw;
        }
        catch
        {
            // 超时 / 拒绝 / 不可达：该地址没有开放端口
            client.Dispose();
            return null;
        }

        var confirmed = false;
        try
        {
            client.NoDelay = true;
            using var stream = client.GetStream();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(ConfirmTimeoutMs);

            var buffer = new byte[1024];
            var read = await stream.ReadAsync(buffer, timeout.Token);
            if (read > 0)
            {
                var payload = Encoding.UTF8.GetString(buffer, 0, read);
                confirmed = payload.Split('\n').Any(line => TiltReading.TryParse(line.Trim(), out _));
            }
        }
        catch
        {
            // 端口开放但没等到（或没等到合法的）数据帧，仍按设备列出
        }
        finally
        {
            client.Dispose();
        }

        return new LanDevice(ip, confirmed);
    }

    /// <summary>把点分 IP 变成可比较的整数，避免字符串排序出现 .10 排在 .2 前面</summary>
    private static uint IpKey(string ip)
    {
        var octets = IPAddress.Parse(ip).GetAddressBytes();
        return ((uint)octets[0] << 24) | ((uint)octets[1] << 16) | ((uint)octets[2] << 8) | octets[3];
    }
}