using System;
using System.Runtime.InteropServices;

namespace Chengshi.Engine;

/// <summary>
/// 在交互式控制台会话里执行锁屏，使「时间到」的硬锁不依赖客户端进程——
/// 孩子杀掉客户端也无法绕过。守护服务以 SYSTEM 运行于 Session 0，需要借助
/// WTS 用户令牌在用户会话中启动 rundll32 调用 LockWorkStation。
/// 任何失败都吞掉，绝不影响守护主循环。
/// </summary>
public static class SessionLocker
{
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const short SwHide = 0;
    private const uint StartfUseShowWindow = 0x00000001;
    private const uint InvalidSessionId = 0xFFFFFFFF;

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(int sessionId, out nint token);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool CreateProcessAsUser(
        nint hToken,
        [MarshalAs(UnmanagedType.LPTStr)] string? lpApplicationName,
        [MarshalAs(UnmanagedType.LPTStr)] string lpCommandLine,
        nint lpProcessAttributes,
        nint lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        nint lpEnvironment,
        [MarshalAs(UnmanagedType.LPTStr)] string? lpCurrentDirectory,
        ref StartupInfo lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint h);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public uint dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    /// <summary>锁住当前活动的控制台会话。成功返回 true。</summary>
    public static bool LockActiveSession()
    {
        try
        {
            var session = (int)WTSGetActiveConsoleSessionId();
            // 无效会话返回 0xFFFFFFFF，转成 int 即 -1。
            if (session < 0)
            {
                return false;
            }

            if (!WTSQueryUserToken(session, out var token))
            {
                return false;
            }

            try
            {
                var si = new StartupInfo
                {
                    cb = Marshal.SizeOf<StartupInfo>(),
                    lpDesktop = "winsta0\\default",
                    dwFlags = StartfUseShowWindow,
                    wShowWindow = SwHide,
                };

                var command = "rundll32.exe user32.dll,LockWorkStation";
                var ok = CreateProcessAsUser(
                    token,
                    null,
                    command,
                    nint.Zero,
                    nint.Zero,
                    false,
                    CreateUnicodeEnvironment | CreateNoWindow,
                    nint.Zero,
                    null,
                    ref si,
                    out var pi);

                if (ok)
                {
                    CloseHandle(pi.hProcess);
                    CloseHandle(pi.hThread);
                }

                return ok;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        catch (Exception)
        {
            return false;
        }
    }
}
