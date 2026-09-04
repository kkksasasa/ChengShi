namespace Chengshi.Core;

public interface ILocalCalendar
{
    DateOnly Today { get; }
}

public sealed class SystemCalendar : ILocalCalendar
{
    public DateOnly Today => DateOnly.FromDateTime(DateTime.Now);
}

public sealed class ManualCalendar : ILocalCalendar
{
    public DateOnly Today { get; set; } = new(2026, 8, 18);
}
