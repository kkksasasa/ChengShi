using System.Runtime.InteropServices;

namespace Chengshi.Engine;

/// <summary>探测交互会话当前是否处于锁屏（Win+L / 切换用户）。</summary>
public interface IWorkstationLockProbe
{
    bool IsLocked(int sessionId);
}

/// <summary>永远报告「没锁」：测试与未注入的宿主用，保证记账行为确定。</summary>
public sealed class NullWorkstationLockProbe : IWorkstationLockProbe
{
    public bool IsLocked(int sessionId) => false;
}

/// <summary>
/// 用 WTS 的 WTSSessionInfoEx 读会话锁定标志。任何失败都按「没锁」处理：
/// 探测只影响「锁屏期间暂停计时」这一件事，探错宁可多计（与老行为一致），绝不能少计。
/// SessionFlags 的语义 Win7 与 Win8+ 相反（老系统上报反值）；澄时只支持 Win10/11，按正确语义读。
/// </summary>
public sealed class WtsWorkstationLockProbe : IWorkstationLockProbe
{
    public static WtsWorkstationLockProbe Instance { get; } = new();

    public bool IsLocked(int sessionId)
    {
        try
        {
            if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, WtsInfoClass.WTSSessionInfoEx, out var buffer, out _))
            {
                return false;
            }

            try
            {
                var info = Marshal.PtrToStructure<WtsInfoEx>(buffer);
                return info.Level == 1 && info.Data.SessionFlags == WtsSessionStateLocked;
            }
            finally
            {
                WTSFreeMemory(buffer);
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    private const int WtsSessionStateLocked = 1;

    private enum WtsInfoClass
    {
        WTSSessionInfoEx = 25,
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr hServer, int sessionId, WtsInfoClass infoClass, out IntPtr ppBuffer, out int pBytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsInfoEx
    {
        public uint Level;
        public WtsInfoExLevel1 Data;
    }

    // WTSINFOEX_LEVEL 是只有一个成员的联合，按第一个成员平铺即可。
    [StructLayout(LayoutKind.Sequential)]
    private struct WtsInfoExLevel1
    {
        public uint SessionId;
        public int SessionState;
        public int SessionFlags;
    }
}
