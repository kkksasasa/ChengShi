namespace Chengshi.Core;

public sealed record DailyBudget(DateOnly Date, TimeSpan Limit, TimeSpan Used)
{
    public TimeSpan Remaining
    {
        get
        {
            var left = Limit - Used;
            return left < TimeSpan.Zero ? TimeSpan.Zero : left;
        }
    }

    public bool Exhausted => Remaining <= TimeSpan.Zero;

    public DailyBudget ForDay(DateOnly today, TimeSpan? limit = null)
    {
        var nextLimit = limit ?? Limit;
        if (today == Date && limit is null)
        {
            return this;
        }

        if (today == Date)
        {
            return this with { Limit = nextLimit };
        }

        return new DailyBudget(today, nextLimit, TimeSpan.Zero);
    }

    public DailyBudget WithUsed(TimeSpan used, DateOnly today)
    {
        var current = ForDay(today);
        if (used < TimeSpan.Zero)
        {
            used = TimeSpan.Zero;
        }

        return current with { Used = used };
    }
}
