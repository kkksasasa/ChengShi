using Chengshi.Core;
using Xunit;

namespace Chengshi.Engine.Tests;

public class GrantExtraTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chengshi-extra-" + Guid.NewGuid().ToString("N"));

    public GrantExtraTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception)
        {
            // ignore
        }
    }

    private string TimePath => Path.Combine(_dir, "time.json");
    private string LogPath => Path.Combine(_dir, "usagelog.jsonl");

    [Fact]
    public void Extra_time_persists_within_the_same_day_and_survives_limit_sync()
    {
        var calendar = new ManualCalendar { Today = new DateOnly(2026, 8, 19) };
        var store = ScreenTimeStore.Load(calendar, TimeSpan.FromMinutes(60), TimePath);

        store.GrantExtra(TimeSpan.FromMinutes(30));

        Assert.Equal(TimeSpan.FromMinutes(90), store.Budget.Limit);

        // 服务重启后重读盘：加时不能丢。
        var reloaded = ScreenTimeStore.Load(calendar, TimeSpan.FromMinutes(60), TimePath);
        Assert.Equal(TimeSpan.FromMinutes(90), reloaded.Budget.Limit);

        // 家长改基础档（60→45）也不清掉今天已批的加时。
        reloaded.SyncLimit(TimeSpan.FromMinutes(45));
        Assert.Equal(TimeSpan.FromMinutes(75), reloaded.Budget.Limit);
    }

    [Fact]
    public void Rollover_drops_yesterday_extra_back_to_base()
    {
        var calendar = new ManualCalendar { Today = new DateOnly(2026, 8, 19) };
        var store = ScreenTimeStore.Load(calendar, TimeSpan.FromMinutes(60), TimePath);
        store.GrantExtra(TimeSpan.FromMinutes(30));
        Assert.Equal(TimeSpan.FromMinutes(90), store.Budget.Limit);

        calendar.Today = new DateOnly(2026, 8, 20);
        store.Rollover(TimeSpan.FromMinutes(60));

        Assert.Equal(new DateOnly(2026, 8, 20), store.Budget.Date);
        Assert.Equal(TimeSpan.FromMinutes(60), store.Budget.Limit);
        Assert.Equal(TimeSpan.Zero, store.Budget.Used);
    }

    [Fact]
    public void Used_accounting_stays_consistent_after_mid_desk_grant()
    {
        // 记账公式：used = 当天总额度 - 场次剩余。两边要同步上调，否则加时会凭空变出时间。
        var clock = new ManualClock();
        var calendar = new ManualCalendar { Today = new DateOnly(2026, 8, 19) };
        var familyStore = FamilyStore.Load(Path.Combine(_dir, "family.json"));
        familyStore.Save(FamilySettings.Create("1234", 60, BuiltinDesks.CodeId));
        var deskStore = DeskStore.Load(Path.Combine(_dir, "desks.json"));
        using var host = new SessionHost(
            clock,
            deskStore,
            familyStore,
            calendar,
            ScreenTimeStore.Load(calendar, TimeSpan.FromMinutes(60), TimePath),
            enforcer: new NoopEnforcer(),
            network: new NoopNetworkGuard(),
            usageLog: new UsageLogStore(LogPath));

        host.StartGuard();
        clock.Advance(TimeSpan.FromMinutes(20));
        var granted = host.GrantExtra("1234", 30);

        Assert.True(granted.Ok);
        Assert.Equal(TimeSpan.FromMinutes(90), host.Budget.Limit);
        Assert.Equal(TimeSpan.FromMinutes(70), host.Snapshot.Remaining);

        // 走一次持久化，验证「已用 = 总额度 - 场次剩余」在加时后依然自洽。
        clock.Advance(TimeSpan.FromMinutes(1));
        var snap = host.Tick();
        Assert.Equal(snap.Remaining.TotalMinutes, (host.Budget.Limit - host.Budget.Used).TotalMinutes, precision: 0);
    }

    [Fact]
    public void Wrong_pin_does_not_grant_anything()
    {
        var clock = new ManualClock();
        var calendar = new ManualCalendar { Today = new DateOnly(2026, 8, 19) };
        var familyStore = FamilyStore.Load(Path.Combine(_dir, "family.json"));
        familyStore.Save(FamilySettings.Create("1234", 60, BuiltinDesks.CodeId));
        var deskStore = DeskStore.Load(Path.Combine(_dir, "desks.json"));
        using var host = new SessionHost(
            clock,
            deskStore,
            familyStore,
            calendar,
            ScreenTimeStore.Load(calendar, TimeSpan.FromMinutes(60), TimePath),
            enforcer: new NoopEnforcer(),
            network: new NoopNetworkGuard(),
            usageLog: new UsageLogStore(LogPath));

        host.StartGuard();
        clock.Advance(TimeSpan.FromMinutes(60));
        Assert.Equal(SessionPhase.TimeUp, host.Snapshot.Phase);
        // 真实服务每秒都会 Tick 把「用满 60 分钟」落账；测试里手动补这一拍。
        host.Tick();
        Assert.Equal(TimeSpan.FromMinutes(60), host.Budget.Used);

        var denied = host.GrantExtra("0000", 15);
        Assert.False(denied.Ok);
        Assert.Equal(SessionPhase.TimeUp, host.Snapshot.Phase);
        Assert.Equal(TimeSpan.FromMinutes(60), host.Budget.Limit);
    }

    [Fact]
    public void Time_up_then_grant_reopens_the_desk_with_extra()
    {
        var clock = new ManualClock();
        var calendar = new ManualCalendar { Today = new DateOnly(2026, 8, 19) };
        var familyStore = FamilyStore.Load(Path.Combine(_dir, "family.json"));
        familyStore.Save(FamilySettings.Create("1234", 60, BuiltinDesks.CodeId));
        var deskStore = DeskStore.Load(Path.Combine(_dir, "desks.json"));
        using var host = new SessionHost(
            clock,
            deskStore,
            familyStore,
            calendar,
            ScreenTimeStore.Load(calendar, TimeSpan.FromMinutes(60), TimePath),
            enforcer: new NoopEnforcer(),
            network: new NoopNetworkGuard(),
            usageLog: new UsageLogStore(LogPath));

        host.StartGuard();
        clock.Advance(TimeSpan.FromMinutes(60));
        Assert.Equal(SessionPhase.TimeUp, host.Snapshot.Phase);
        host.Tick();
        Assert.Equal(TimeSpan.FromMinutes(60), host.Budget.Used);

        var granted = host.GrantExtra("1234", 20);

        Assert.True(granted.Ok);
        Assert.Equal(SessionPhase.InDesk, granted.Snapshot.Phase);
        Assert.Equal(BuiltinDesks.CodeId, granted.Snapshot.DeskId);
        Assert.True(host.IsGuarding);
        Assert.Equal(TimeSpan.FromMinutes(80), host.Budget.Limit);
        Assert.InRange(host.Snapshot.Remaining.TotalMinutes, 19, 21);
    }

    [Fact]
    public void Rollover_writes_yesterday_usage_into_the_log()
    {
        var clock = new ManualClock();
        var calendar = new ManualCalendar { Today = new DateOnly(2026, 8, 19) };
        var familyStore = FamilyStore.Load(Path.Combine(_dir, "family.json"));
        familyStore.Save(FamilySettings.Create("1234", 60, BuiltinDesks.CodeId));
        var deskStore = DeskStore.Load(Path.Combine(_dir, "desks.json"));
        using var host = new SessionHost(
            clock,
            deskStore,
            familyStore,
            calendar,
            ScreenTimeStore.Load(calendar, TimeSpan.FromMinutes(60), TimePath),
            enforcer: new NoopEnforcer(),
            network: new NoopNetworkGuard(),
            usageLog: new UsageLogStore(LogPath));

        host.StartGuard();
        clock.Advance(TimeSpan.FromMinutes(25));
        host.Stop("1234");
        calendar.Today = new DateOnly(2026, 8, 20);
        clock.Advance(TimeSpan.FromHours(24));
        host.Tick();

        var log = new UsageLogStore(LogPath).ReadRecent(7);
        var entry = Assert.Single(log);
        Assert.Equal(new DateOnly(2026, 8, 19), entry.Date);
        Assert.InRange(entry.UsedMinutes, 24, 26);
    }

    [Fact]
    public void Usage_log_skips_corrupt_lines_but_keeps_history()
    {
        File.WriteAllLines(LogPath,
        [
            """{"date":"2026-08-17","usedMinutes":50,"blockedCount":2}""",
            "{corrupt",
            """{"date":"2026-08-18","usedMinutes":10,"blockedCount":0}""",
        ]);

        var recent = new UsageLogStore(LogPath).ReadRecent(7);

        Assert.Equal(2, recent.Count);
        Assert.Equal(new DateOnly(2026, 8, 18), recent[0].Date);
        Assert.Equal(50, recent[^1].UsedMinutes);
        Assert.Equal(2, recent[^1].BlockedCount);
    }
}
