using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Chengshi.Engine;

/// <summary>
/// 交互会话判定：守护服务以 SYSTEM 运行，能看到所有会话的进程，
/// 但只应拦截当前控制台会话（孩子正在用的那个），绝不碰服务会话和其他用户。
/// </summary>
public static class ActiveSession
{
    private static bool _directUnavailable;

    public static int ConsoleSessionId
    {
        get
        {
            // 个别精简版 / 老版 Windows 的 kernel32 没有这个导出（EntryPointNotFoundException），
            // 直接把每秒的守护 Tick 打断：倒计时冻结、界面反复弹错。
            // 降级链：kernel32 导出 → wtsapi32 枚举会话 → 当前进程会话。
            if (!_directUnavailable)
            {
                try
                {
                    return (int)WtsGetActiveConsoleSessionId();
                }
                catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
                {
                    _directUnavailable = true;
                }
            }

            var enumerated = TryEnumerateActiveSession();
            return enumerated ?? Process.GetCurrentProcess().SessionId;
        }
    }

    public static bool IsInteractive(int sessionId)
    {
        var active = ConsoleSessionId;
        return active != 0 && sessionId == active;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WtsGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessions(IntPtr hServer, int reserved, int version, out IntPtr ppSessionInfo, out int count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    private enum WtsConnectState
    {
        WtsActive,
        WtsConnected,
        WtsConnectQuery,
        WtsShadow,
        WtsDisconnected,
        WtsIdle,
        WtsListen,
        WtsReset,
        WtsDown,
        WtsInit,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public int SessionId;
        public IntPtr pWinStationName;
        public WtsConnectState State;
    }

    /// <summary>枚举会话找 State=Active 的那一个；枚举不可用时返回 null。</summary>
    private static int? TryEnumerateActiveSession()
    {
        try
        {
            if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var buffer, out var count))
            {
                return null;
            }

            try
            {
                var size = Marshal.SizeOf<WtsSessionInfo>();
                for (var i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<WtsSessionInfo>(buffer + i * size);
                    if (info.State == WtsConnectState.WtsActive)
                    {
                        return info.SessionId;
                    }
                }

                return null;
            }
            finally
            {
                WTSFreeMemory(buffer);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }
}
