namespace Chengshi.Engine;

/// <summary>守护服务拒绝/失败时抛给界面程序的异常。</summary>
public sealed class RemoteFaultException : Exception
{
    public RemoteFaultException(string message)
        : base(message)
    {
    }
}
