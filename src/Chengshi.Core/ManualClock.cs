namespace Chengshi.Core;

public sealed class ManualClock : IUnbiasedClock
{
    public TimeSpan Elapsed { get; set; }

    public void Advance(TimeSpan delta) => Elapsed += delta;
}
