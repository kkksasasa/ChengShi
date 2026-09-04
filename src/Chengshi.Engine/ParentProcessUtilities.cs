using System.Runtime.InteropServices;

namespace Chengshi.Engine;

internal static class ParentProcessUtilities
{
    public static int GetParentProcessId(int pid)
    {
        var pbi = new ProcessBasicInformation();
        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            var status = NtQueryInformationProcess(
                process,
                0,
                ref pbi,
                Marshal.SizeOf<ProcessBasicInformation>(),
                out _);
            return status == 0 ? pbi.InheritedFromUniqueProcessId.ToInt32() : 0;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private const int ProcessQueryLimitedInformation = 0x1000;

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }
}
