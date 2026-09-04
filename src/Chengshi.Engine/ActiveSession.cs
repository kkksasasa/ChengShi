using System.Runtime.InteropServices;

namespace Chengshi.Engine;

/// <summary>
/// 交互会话判定：守护服务以 SYSTEM 运行，能看到所有会话的进程，
/// 但只应拦截当前控制台会话（孩子正在用的那个），绝不碰服务会话和其他用户。
/// </summary>
public static class ActiveSession
{
    public static int ConsoleSessionId => (int)WtsGetActiveConsoleSessionId();

    public static bool IsInteractive(int sessionId)
    {
        var active = ConsoleSessionId;
        return active != 0 && sessionId == active;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WtsGetActiveConsoleSessionId();
}
