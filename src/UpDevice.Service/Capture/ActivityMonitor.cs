namespace UpDevice.Service.Capture;

/// <summary>
/// 活动状态判定：以「最后一次键鼠输入」与「最后一次控制器输入」中较新的时间为准。
/// </summary>
public sealed class ActivityMonitor
{
    private long _lastControllerTick;

    public void NotifyControllerActivity()
    {
        Volatile.Write(ref _lastControllerTick, Environment.TickCount64);
    }

    /// <summary>距离最后一次控制器输入过去了多少秒；从未检测到则返回 <see cref="double.MaxValue"/>。</summary>
    public double SecondsSinceControllerInput()
    {
        var lastController = Volatile.Read(ref _lastControllerTick);
        return lastController == 0 ? double.MaxValue : (Environment.TickCount64 - lastController) / 1000.0;
    }

    /// <summary>当前空闲秒数。</summary>
    public double IdleSeconds(bool includeController)
    {
        var keyboardMouseIdle = Native.NativeMethods.GetIdleMilliseconds() / 1000.0;
        if (!includeController)
        {
            return keyboardMouseIdle;
        }

        var lastController = Volatile.Read(ref _lastControllerTick);
        if (lastController == 0)
        {
            return keyboardMouseIdle;
        }

        var controllerIdle = (Environment.TickCount64 - lastController) / 1000.0;
        return Math.Min(keyboardMouseIdle, controllerIdle);
    }

    /// <summary>timeoutSeconds 为 0 表示禁用待机判定。</summary>
    public bool IsIdle(int timeoutSeconds, bool includeController)
    {
        if (timeoutSeconds <= 0)
        {
            return false;
        }

        return IdleSeconds(includeController) >= timeoutSeconds;
    }
}
