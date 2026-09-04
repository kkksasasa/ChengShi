using Chengshi.Core;

namespace Chengshi.Engine;

public interface IProcessEnforcer
{
    event Action<ProcessIdentity>? Blocked;

    /// <summary>按书桌规则检查并拦截；返回是否真的拦了。</summary>
    bool TryEnforce(ProcessIdentity process, Desk desk);

    /// <summary>把已经在运行的进程全部过一遍规则；返回拦截数量。</summary>
    int SweepRunning(Desk desk);
}
