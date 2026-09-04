using System.Text.Json;
using Chengshi.Core;

namespace Chengshi.Engine;

/// <summary>
/// 每天额度的持久化：基础限额来自家长设置，另记一笔当天批准的「加时」。
/// 加时只在今天有效——跨天重置后回到基础限额。
/// </summary>
public sealed class ScreenTimeStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ILocalCalendar _calendar;
    private int _baseMinutes;
    private int _extraMinutes;

    public string FilePath { get; }

    public static string DefaultFilePath { get; } = Path.Combine(StorePaths.Root, "screentime.json");

    public DailyBudget Budget { get; private set; }

    /// <summary>最近一次写盘失败的原因；成功后清空。计时持久化失败不该打断守护。</summary>
    public string? LastError { get; private set; }

    private ScreenTimeStore(ILocalCalendar calendar, DailyBudget budget, int baseMinutes, int extraMinutes, string filePath)
    {
        _calendar = calendar;
        Budget = budget;
        _baseMinutes = baseMinutes;
        _extraMinutes = extraMinutes;
        FilePath = filePath;
    }

    /// <summary>今天总共还剩多少分钟可以批准为加时的上限（防止无限加）。</summary>
    public const int MaxExtraMinutesPerGrant = 240;

    public static ScreenTimeStore Load(ILocalCalendar calendar, TimeSpan limit, string? path = null)
    {
        var filePath = path ?? DefaultFilePath;
        var baseLimit = limit;
        var extra = TimeSpan.Zero;
        DateOnly? savedDate = null;
        double usedSeconds = 0;
        try
        {
            if (File.Exists(filePath))
            {
                var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(filePath), Json);
                if (dto is not null && DateOnly.TryParse(dto.Date, out var date))
                {
                    savedDate = date;
                    usedSeconds = Math.Max(0, dto.UsedSeconds);
                    extra = TimeSpan.FromMinutes(Math.Max(0, dto.ExtraMinutes));
                    // 老文件没有 ExtraMinutes：整档都是基础额度。
                    baseLimit = TimeSpan.FromMinutes(
                        Math.Max(1, dto.LimitMinutes - (int)extra.TotalMinutes));
                }
            }
        }
        catch (Exception)
        {
            // 损坏则从今天零开始。
        }

        var today = calendar.Today;
        if (savedDate is { } date0 && date0 == today)
        {
            var store = new ScreenTimeStore(
                calendar,
                new DailyBudget(today, baseLimit + extra, TimeSpan.FromSeconds(usedSeconds)),
                (int)Math.Round(baseLimit.TotalMinutes),
                (int)Math.Round(extra.TotalMinutes),
                filePath);
            store.Persist();
            return store;
        }

        // 旧的一天：从今天零开始重新计。
        return FromScratch(calendar, baseLimit, filePath);
    }

    public DailyBudget SyncLimit(TimeSpan baseLimit)
    {
        _baseMinutes = (int)Math.Round(baseLimit.TotalMinutes);
        RebuildSameDay();
        Persist();
        return Budget;
    }

    /// <summary>把当天可用的总时长上调 delta（家长批准加时）。</summary>
    public DailyBudget GrantExtra(TimeSpan delta)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(delta, TimeSpan.Zero);
        if (_baseMinutes <= 0)
        {
            _baseMinutes = (int)Math.Round(Budget.Limit.TotalMinutes);
        }

        if (Budget.Date != _calendar.Today)
        {
            // 已是新的一天：加时就是这个新一天的额度，基础档明天才生效。
            _extraMinutes = (int)Math.Round(delta.TotalMinutes);
            Budget = Budget.ForDay(_calendar.Today, LimitWithExtra());
            Persist();
            return Budget;
        }

        _extraMinutes += (int)Math.Round(delta.TotalMinutes);
        Budget = Budget with { Limit = Budget.Limit + delta };
        Persist();
        return Budget;
    }

    public DailyBudget SaveUsed(TimeSpan used)
    {
        Budget = Budget.WithUsed(used, _calendar.Today);
        Persist();
        return Budget;
    }

    public DailyBudget Rollover(TimeSpan baseLimit)
    {
        _baseMinutes = (int)Math.Round(baseLimit.TotalMinutes);
        _extraMinutes = 0;
        Budget = Budget.ForDay(_calendar.Today, baseLimit);
        Persist();
        return Budget;
    }

    private TimeSpan LimitWithExtra(TimeSpan? additional = null)
    {
        var baseMinutes = _baseMinutes > 0 ? _baseMinutes : (int)Math.Round(Budget.Limit.TotalMinutes - _extraMinutes);
        var total = Math.Max(1, baseMinutes) + _extraMinutes + (int)Math.Round(additional?.TotalMinutes ?? 0);
        return TimeSpan.FromMinutes(total);
    }

    private void RebuildSameDay()
    {
        if (_baseMinutes <= 0)
        {
            return;
        }

        Budget = Budget.ForDay(_calendar.Today, TimeSpan.FromMinutes(_baseMinutes + _extraMinutes));
    }

    private static ScreenTimeStore FromScratch(ILocalCalendar calendar, TimeSpan baseLimit, string filePath)
    {
        var store = new ScreenTimeStore(
            calendar,
            new DailyBudget(calendar.Today, baseLimit, TimeSpan.Zero),
            (int)Math.Round(baseLimit.TotalMinutes),
            0,
            filePath);
        store.Persist();
        return store;
    }

    private void Persist()
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var dto = new Dto(
                Budget.Date.ToString("yyyy-MM-dd"),
                Budget.Used.TotalSeconds,
                (int)Budget.Limit.TotalMinutes,
                Math.Max(0, _extraMinutes));
            File.WriteAllText(FilePath, JsonSerializer.Serialize(dto, Json));
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    internal sealed record Dto(string Date, double UsedSeconds, int LimitMinutes, int ExtraMinutes = 0);
}
