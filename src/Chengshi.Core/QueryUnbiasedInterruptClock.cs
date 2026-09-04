using System.Runtime.InteropServices;

namespace Chengshi.Core;

public sealed class QueryUnbiasedInterruptClock : IUnbiasedClock
{
    public TimeSpan Elapsed
    {
        get
        {
            if (!QueryUnbiasedInterruptTime(out var hundredNanoSeconds))
            {
                throw new InvalidOperationException("QueryUnbiasedInterruptTime 失败。");
            }

            return TimeSpan.FromTicks((long)hundredNanoSeconds);
        }
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);
}
