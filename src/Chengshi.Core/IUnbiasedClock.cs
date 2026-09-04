namespace Chengshi.Core;

/// <summary>
/// 不受系统墙钟拨动影响的单调时钟。钉住场次必须用这个，不能用 DateTime.Now。
/// </summary>
public interface IUnbiasedClock
{
    TimeSpan Elapsed { get; }
}
