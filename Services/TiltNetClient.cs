using System.Net.Sockets;
using MobileWheel.Win.Models;

namespace MobileWheel.Win.Services;

/// <summary>
/// 倾斜角局域网客户端（TCP）：连接安卓端监听的端口，按行读取 JSON 数据。
/// </summary>
public sealed class TiltNetClient : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly TiltFrameParser _parser = new();

    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _readLoop;
    private bool _disposed;

    /// <summary>文本日志，可能在后台线程触发</summary>
    public event EventHandler<string>? Log;

    /// <summary>收到一帧完整的倾斜角数据，可能在后台线程触发</summary>
    public event EventHandler<TiltReading>? ReadingReceived;

    /// <summary>连接状态变化（true=已连上服务端）</summary>
    public event EventHandler<bool>? ConnectionStateChanged;

    public bool IsConnected => _client?.Connected == true;

    /// <summary>连接服务端，失败时抛出异常供界面提示。</summary>
    public async Task ConnectAsync(string host, int port)
    {
        if (_disposed) return;

        await DisconnectAsync(notifyStateChange: false);
        Log?.Invoke(this, $"正在连接 {host}:{port}…");

        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port).WaitAsync(ConnectTimeout);
        }
        catch (TimeoutException)
        {
            client.Dispose();
            throw new TimeoutException($"连接超时：{host}:{port}（请确认手机已开启局域网，且与电脑在同一网络）");
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw new InvalidOperationException($"{ex.Message}（{host}:{port}）");
        }

        _client = client;
        _stream = client.GetStream();
        _parser.Reset();
        _cts = new CancellationTokenSource();

        // 开启 keep-alive 与低延迟，长时间挂机时能更快察觉对端掉线
        client.NoDelay = true;
        try
        {
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        }
        catch (SocketException ex)
        {
            Log?.Invoke(this, $"设置 keep-alive 失败（可忽略）: {ex.Message}");
        }

        Log?.Invoke(this, $"已连接：{host}:{port}");
        ConnectionStateChanged?.Invoke(this, true);

        var stream = _stream;
        var token = _cts.Token;
        _readLoop = Task.Run(() => ReadLoopAsync(stream, token));
    }

    /// <summary>断开连接（用户主动调用）</summary>
    public async Task DisconnectAsync() => await DisconnectAsync(notifyStateChange: true);

    private async Task DisconnectAsync(bool notifyStateChange)
    {
        var cts = _cts;
        var readLoop = _readLoop;
        _cts = null;
        _readLoop = null;

        cts?.Cancel();
        if (readLoop is not null)
        {
            try
            {
                await readLoop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                Log?.Invoke(this, $"等待读取线程退出失败（可忽略）: {ex.Message}");
            }
        }

        cts?.Dispose();
        ResetConnection(notifyStateChange);
        if (notifyStateChange)
        {
            Log?.Invoke(this, "已断开连接");
        }
    }

    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[4096];
        try
        {
            while (!token.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, token);
                if (read <= 0) break; // 对端关闭连接

                foreach (var frame in _parser.Append(buffer.AsSpan(0, read)))
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
        }
        catch (OperationCanceledException)
        {
            // 用户主动断开，无需提示
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                Log?.Invoke(this, $"读取数据失败：{ex.Message}");
            }
        }
        finally
        {
            // 主动断开时由 DisconnectAsync 统一收尾，避免重复上报断开状态
            var userInitiated = token.IsCancellationRequested;
            if (!userInitiated)
            {
                Log?.Invoke(this, "设备侧已断开连接");
            }

            ResetConnection(notifyStateChange: !userInitiated);
        }
    }

    /// <summary>清空连接相关状态（幂等）；notifyStateChange=false 时不对外发“已断开”。</summary>
    private void ResetConnection(bool notifyStateChange = true)
    {
        var stream = _stream;
        _stream = null;
        stream?.Dispose();

        var client = _client;
        _client = null;
        client?.Dispose();

        _parser.Reset();
        if (notifyStateChange)
        {
            ConnectionStateChanged?.Invoke(this, false);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _readLoop = null;
        ResetConnection(notifyStateChange: false);
    }
}