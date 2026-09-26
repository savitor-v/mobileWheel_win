using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Exceptions;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace MobileWheel.Win.Services;

/// <summary>
/// 把手机横向倾角映射成虚拟 Xbox 360 手柄的左摇杆 X 轴，让游戏把左摇杆当成方向盘：
/// 手机向左倾斜（angle &gt; 0）= 方向盘左打 = 摇杆 X 取负值。
/// 可用角度 0°~60°：0° 居中，越接近 ±60° 舵量越大，到 ±60° 满舵（超出按满舵截断）。
/// 手机端左右两侧的竖向滑动条分别映射到 LT / RT 扳机（0..255），
/// 顶部两个按键分别映射到 Xbox 360 手柄的 X / A 按键。
/// 依赖已安装的 ViGEmBus 驱动，用户态调用不需要管理员权限。
/// </summary>
public sealed class TiltGamepad : IDisposable
{
    /// <summary>满舵角度：手机倾斜 ±60° 对应摇杆最左 / 最右</summary>
    public const double FullLockAngle = 60d;

    /// <summary>超过该时间没有新数据就自动回中，避免停止监测后方向盘卡在满舵</summary>
    private static readonly TimeSpan DataTimeout = TimeSpan.FromMilliseconds(500);

    private readonly object _sync = new();
    private readonly System.Threading.Timer _watchdog;

    private ViGEmClient? _client;
    private IXbox360Controller? _pad;
    private short? _lastAxis;
    private byte? _lastLeftTrigger;
    private byte? _lastRightTrigger;
    private bool? _lastButtonX;
    private bool? _lastButtonA;
    private DateTime _lastDataAt;
    private bool _hasData;
    private bool _disposed;

    public TiltGamepad()
    {
        _watchdog = new System.Threading.Timer(_ => CheckDataTimeout(), null, DataTimeout, DataTimeout);
    }

    /// <summary>文本日志，可能在本类内部线程或调用线程触发</summary>
    public event EventHandler<string>? Log;

    /// <summary>虚拟手柄是否已接入系统</summary>
    public bool IsConnected
    {
        get
        {
            lock (_sync) return _pad is not null;
        }
    }

    /// <summary>创建并接入虚拟 Xbox 360 手柄；失败时记日志并保持未接入状态。</summary>
    public void Connect()
    {
        lock (_sync)
        {
            if (_disposed || _pad is not null) return;

            try
            {
                _client = new ViGEmClient();
                _pad = _client.CreateXbox360Controller();
                _pad.AutoSubmitReport = true;
                _pad.Connect();

                // ViGEmBus 新建虚拟手柄的摇杆/扳机初始值并不都是 0（实测左摇杆 X 读到 -3356），
                // 而 ViGEm.NET 不会重复下发与上次完全相同的报告，
                // 所以先各写一个非 0 值再写回 0，保证手柄一插上就真正居中、扳机松开。
                _pad.SetAxisValue(Xbox360Axis.LeftThumbX, 1);
                _pad.SetSliderValue(Xbox360Slider.LeftTrigger, 1);
                _pad.SetSliderValue(Xbox360Slider.RightTrigger, 1);
                _pad.SetAxisValue(Xbox360Axis.LeftThumbX, 0);
                _pad.SetSliderValue(Xbox360Slider.LeftTrigger, 0);
                _pad.SetSliderValue(Xbox360Slider.RightTrigger, 0);
                _pad.SetButtonState(Xbox360Button.X, false);
                _pad.SetButtonState(Xbox360Button.A, false);

                _lastAxis = 0;
                _lastLeftTrigger = 0;
                _lastRightTrigger = 0;
                _lastButtonX = false;
                _lastButtonA = false;
                _hasData = false;

                Log?.Invoke(this, $"虚拟 Xbox 360 手柄已接入：左摇杆 X 轴 = 手机倾角（0°~{FullLockAngle:0}° 线性映射，超出按满舵截断），LT/RT = 左右滑动条，X/A = 顶部两个按键");
            }
            catch (VigemBusNotFoundException ex)
            {
                Log?.Invoke(this, $"ViGEmBus 驱动不可用，无法创建虚拟手柄：{ex.Message}");
                ReleaseInternal();
            }
            catch (Exception ex)
            {
                Log?.Invoke(this, $"创建虚拟手柄失败：{ex.GetType().Name} {ex.Message}");
                ReleaseInternal();
            }
        }
    }

    /// <summary>
    /// 把倾角、两个扳机与 X / A 按键写进虚拟手柄；未接入时静默忽略
    /// （数据线程直接调用，不加 UI 派发）
    /// </summary>
    public void Update(double angle, int leftTrigger, int rightTrigger, bool buttonX, bool buttonA) =>
        Write(ToAxis(angle), ToTrigger(leftTrigger), ToTrigger(rightTrigger), buttonX, buttonA);

    /// <summary>方向盘回中、扳机松开、按键全部释放（断开连接、停止监测时调用）</summary>
    public void Reset() => Write(0, 0, 0, false, false);

    /// <summary>方向盘回中并拔出虚拟手柄</summary>
    public void Disconnect()
    {
        lock (_sync)
        {
            ReleaseInternal();
        }
    }

    /// <summary>
    /// 倾角 → 摇杆 X 轴比例（-1..1）。
    /// 可用角度 0°~<see cref="FullLockAngle"/>：0° 为 0（居中），到 ±60° 线性升到满舵；
    /// 向左倾斜（angle &gt; 0）返回负值，对应方向盘左打。
    /// </summary>
    public static double ToRatio(double angle)
    {
        var magnitude = Math.Min(Math.Abs(angle), FullLockAngle);
        var ratio = magnitude / FullLockAngle;
        return angle < 0 ? ratio : -ratio;
    }

    /// <summary>倾角 → 摇杆 X 轴值；向左倾斜取负（左打方向）</summary>
    public static short ToAxis(double angle) =>
        (short)Math.Round(ToRatio(angle) * short.MaxValue);

    /// <summary>扳机值 → 单字节，超出 0..255 的部分夹断</summary>
    private static byte ToTrigger(int value) => (byte)Math.Clamp(value, 0, 255);

    /// <summary>写入轴、扳机与按键；值没变就不重复下发，省去无效的 USB 报告</summary>
    private void Write(short axis, byte leftTrigger, byte rightTrigger, bool buttonX, bool buttonA)
    {
        lock (_sync)
        {
            var pad = _pad;
            if (pad is null) return;

            // 数据是否变化都要刷新时间戳，避免数据平稳时被看门狗误判为超时
            _hasData = true;
            _lastDataAt = DateTime.UtcNow;

            try
            {
                if (_lastAxis != axis)
                {
                    pad.SetAxisValue(Xbox360Axis.LeftThumbX, axis);
                    _lastAxis = axis;
                }

                if (_lastLeftTrigger != leftTrigger)
                {
                    pad.SetSliderValue(Xbox360Slider.LeftTrigger, leftTrigger);
                    _lastLeftTrigger = leftTrigger;
                }

                if (_lastRightTrigger != rightTrigger)
                {
                    pad.SetSliderValue(Xbox360Slider.RightTrigger, rightTrigger);
                    _lastRightTrigger = rightTrigger;
                }

                if (_lastButtonX != buttonX)
                {
                    pad.SetButtonState(Xbox360Button.X, buttonX);
                    _lastButtonX = buttonX;
                }

                if (_lastButtonA != buttonA)
                {
                    pad.SetButtonState(Xbox360Button.A, buttonA);
                    _lastButtonA = buttonA;
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke(this, $"写入虚拟手柄失败：{ex.GetType().Name} {ex.Message}");
            }
        }
    }

    private void CheckDataTimeout()
    {
        lock (_sync)
        {
            if (_disposed || _pad is null || !_hasData) return;
            if (DateTime.UtcNow - _lastDataAt <= DataTimeout) return;

            _hasData = false;
            // 数据断了就把方向盘回中、扳机松开、按键释放，避免游戏里卡住满舵或一直踩油门
            Write(0, 0, 0, false, false);
        }
    }

    /// <summary>拔出虚拟手柄并释放客户端（调用方需已持锁）</summary>
    private void ReleaseInternal()
    {
        var pad = _pad;
        _pad = null;
        _lastAxis = null;
        _lastLeftTrigger = null;
        _lastRightTrigger = null;
        _lastButtonX = null;
        _lastButtonA = null;
        _hasData = false;

        if (pad is not null)
        {
            try
            {
                pad.Disconnect();
            }
            catch (Exception ex)
            {
                Log?.Invoke(this, $"拔出虚拟手柄失败（可忽略）：{ex.Message}");
            }
        }

        var client = _client;
        _client = null;
        client?.Dispose();

        if (pad is not null)
        {
            Log?.Invoke(this, "虚拟手柄已拔出");
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseInternal();
        }

        _watchdog.Dispose();
    }
}