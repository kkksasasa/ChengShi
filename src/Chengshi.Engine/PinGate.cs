using System.Diagnostics;

namespace Chengshi.Engine;

/// <summary>
/// 家长密码验证的失败锁定：连错 5 次后开始指数退避（1 分钟起步，封顶 15 分钟），
/// 验证成功即清零。管道是本机所有进程都能连的，没有这层闸门，
/// 一个 4 位数字密码十几分钟就能被普通进程在线穷举完。
/// </summary>
public sealed class PinGate
{
    private readonly Func<double> _elapsedSeconds;
    private readonly object _gate = new();
    private int _consecutiveFailures;
    private double _lockedUntilSeconds = double.NegativeInfinity;

    public PinGate(Func<double> elapsedSeconds) => _elapsedSeconds = elapsedSeconds;

    /// <summary>当前是否在锁定期内；返回剩余等待秒数（向上取整）。</summary>
    public bool IsLocked(out int retryInSeconds)
    {
        lock (_gate)
        {
            var remaining = _lockedUntilSeconds - _elapsedSeconds();
            if (remaining > 0)
            {
                retryInSeconds = (int)Math.Ceiling(remaining);
                return true;
            }

            retryInSeconds = 0;
            return false;
        }
    }

    /// <summary>验证成功：失败计数清零、解除锁定。</summary>
    public void OnSuccess()
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;
            _lockedUntilSeconds = double.NegativeInfinity;
        }
    }

    /// <summary>验证失败：可能触发或延长锁定。返回本次失败后的锁定剩余秒数（0 表示未锁定）。</summary>
    public int OnFailure()
    {
        lock (_gate)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures < Threshold)
            {
                return 0;
            }

            var backoff = Math.Min(
                TimeSpan.FromSeconds(60 * Math.Pow(2, _consecutiveFailures - Threshold)).TotalSeconds,
                MaxLockSeconds);
            _lockedUntilSeconds = _elapsedSeconds() + backoff;
            return (int)Math.Ceiling(backoff);
        }
    }

    public const int Threshold = 5;
    public const double MaxLockSeconds = 15 * 60;
}
