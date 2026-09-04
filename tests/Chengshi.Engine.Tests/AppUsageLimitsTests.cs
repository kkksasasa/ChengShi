using Chengshi.Core;
using Xunit;

namespace Chengshi.Engine.Tests;

public class AppUsageLimitsTests : IDisposable
{
    private const string TestDeskId = "per-app-limits";
    private static readonly DateOnly Wednesday = new(2026, 8, 19);

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "chengshi-appusage-" + Guid.NewGuid().ToString("N"));

    public AppUsageLimitsTests() => Directory.CreateDirectory(_dir);

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

    private string FamilyPath => Path.Combine(_dir, "family.json");
    private string DeskPath => Path.Combine(_dir, "desks.json");
    private string TimePath => Path.Combine(_dir, "screentime.json");
    private string AppUsagePath => Path.Combine(_dir, "appusage.json");

    /// <summary>只报告「正在运行」的软件，不碰真实进程。</summary>
    private sealed class FakeProbe : IRunningAppProbe
    {
        public List<string> Keys { get; set; } = [];

        public IReadOnlyCollection<string> RunningKeys(Desk desk) => Keys.ToArray();
    }

    /// <summary>只记录交进来的书桌，绝不真的结束进程——测试里碰真进程太危险。</summary>
    private sealed class RecordingEnforcer : IProcessEnforcer
    {
        public List<Desk> Swept { get; } = [];

        event Action<ProcessIdentity>? IProcessEnforcer.Blocked
        {
            add { }
            remove { }
        }

        public bool TryEnforce(ProcessIdentity process, Desk desk) => false;

        public int SweepRunning(Desk desk)
        {
            Swept.Add(desk);
            return 0;
        }
    }

    private sealed class NoopSiteGuard : SitePolicyGuard
    {
        public override bool Apply(PolicySpec spec) => true;
    }

    private Fixture Build(int? calcLimit, TimeSpan daily)
    {
        var calendar = new ManualCalendar { Today = Wednesday };
        var clock = new ManualClock();

        var familyStore = FamilyStore.Load(FamilyPath);
        familyStore.Save(FamilySettings.Create("1234", (int)daily.TotalMinutes, TestDeskId));

        var deskStore = DeskStore.Load(DeskPath);
        deskStore.Upsert(new Desk(
            TestDeskId,
            "测试桌",
            "记事本与计算器",
            [
                new AllowedApp("记事本", "notepad"),
                new AllowedApp("计算器", "calc", dailyMinutes: calcLimit),
            ]));

        var probe = new FakeProbe();
        var enforcer = new RecordingEnforcer();
        var store = new AppUsageStore(AppUsagePath);
        var host = new SessionHost(
            clock,
            deskStore,
            familyStore,
            calendar,
            ScreenTimeStore.Load(calendar, daily, TimePath),
            enforcer,
            new NoopNetworkGuard(),
            new NoopSiteGuard(),
            new UsageLogStore(Path.Combine(_dir, "usagelog.jsonl")),
            () => DateTime.Now,
            probe,
            store);

        return new Fixture(host, clock, calendar, probe, enforcer, store);
    }

    private sealed record Fixture(
        SessionHost Host,
        ManualClock Clock,
        ManualCalendar Calendar,
        FakeProbe Probe,
        RecordingEnforcer Enforcer,
        AppUsageStore Store) : IDisposable
    {
        public void Dispose() => Host.Dispose();
        /// <summary>按 1 分钟一步推进：记账有 5 分钟的单次上限，一次跳太久会被丢弃。</summary>
        public void RunMinutes(int minutes)
        {
            for (var i = 0; i < minutes; i++)
            {
                Clock.Advance(TimeSpan.FromMinutes(1));
                Host.Tick();
            }
        }

        public AppUsage Usage(string key) => Host.AppUsage.Single(row => row.Key == key);
    }

    [Fact]
    public void Only_running_apps_accumulate_usage()
    {
        using var f = Build(calcLimit: null, TimeSpan.FromHours(6));
        f.Probe.Keys = ["calc.exe"];
        f.Host.StartGuard();

        f.RunMinutes(10);

        Assert.Equal(10, f.Usage("calc.exe").UsedMinutes);
        Assert.Equal(0, f.Usage("notepad.exe").UsedMinutes);
    }

    [Fact]
    public void Usage_keeps_accruing_while_the_app_stays_open()
    {
        using var f = Build(calcLimit: null, TimeSpan.FromHours(6));
        f.Probe.Keys = ["calc.exe", "notepad.exe"];
        f.Host.StartGuard();

        f.RunMinutes(5);
        Assert.Equal(5, f.Usage("calc.exe").UsedMinutes);

        // 记事本关掉后只累计计算器。
        f.Probe.Keys = ["calc.exe"];
        f.RunMinutes(5);

        Assert.Equal(10, f.Usage("calc.exe").UsedMinutes);
        Assert.Equal(5, f.Usage("notepad.exe").UsedMinutes);
    }

    [Fact]
    public void Nothing_is_counted_when_no_desk_session_is_running()
    {
        using var f = Build(calcLimit: null, TimeSpan.FromHours(6));
        f.Probe.Keys = ["calc.exe"];

        f.RunMinutes(10);

        Assert.Equal(0, f.Usage("calc.exe").UsedMinutes);
    }

    [Fact]
    public void App_over_its_own_limit_is_dropped_from_the_enforced_desk()
    {
        using var f = Build(calcLimit: 30, TimeSpan.FromHours(6));
        f.Probe.Keys = ["calc.exe"];
        f.Host.StartGuard();

        f.RunMinutes(29);
        Assert.False(f.Usage("calc.exe").Exhausted);
        Assert.Contains(f.Host.EnforcedDesk!.Apps, a => a.Key == "calc.exe");

        f.RunMinutes(2);
        Assert.True(f.Usage("calc.exe").Exhausted);

        var enforced = f.Host.EnforcedDesk!;
        Assert.DoesNotContain(enforced.Apps, a => a.Key == "calc.exe");
        Assert.Contains(enforced.Apps, a => a.Key == "notepad.exe");
    }

    [Fact]
    public void App_without_its_own_limit_is_never_dropped()
    {
        using var f = Build(calcLimit: null, TimeSpan.FromHours(6));
        f.Probe.Keys = ["calc.exe"];
        f.Host.StartGuard();

        f.RunMinutes(120);

        Assert.False(f.Usage("calc.exe").Exhausted);
        Assert.Contains(f.Host.EnforcedDesk!.Apps, a => a.Key == "calc.exe");
    }

    [Fact]
    public void Yesterday_exhausted_app_stays_blocked_when_guard_starts()
    {
        var calendar = new ManualCalendar { Today = Wednesday };
        var store = new AppUsageStore(AppUsagePath);
        store.Save(calendar.Today, new Dictionary<string, double> { ["calc.exe"] = 31 * 60 });

        using var f = Build(calcLimit: 30, TimeSpan.FromHours(6));
        f.Probe.Keys = ["calc.exe"];
        f.Host.StartGuard();

        // 首轮清场用的就必须是「有效书桌」，否则额度用完的软件一重启澄时就复活。
        Assert.NotEmpty(f.Enforcer.Swept);
        Assert.DoesNotContain(f.Enforcer.Swept[0].Apps, a => a.Key == "calc.exe");
    }

    [Fact]
    public void A_gap_longer_than_five_minutes_is_not_counted()
    {
        using var f = Build(calcLimit: null, TimeSpan.FromHours(24));
        f.Probe.Keys = ["calc.exe"];
        f.Host.StartGuard();

        // 休眠或服务长时间挂起后，中间那段不能算到孩子头上。
        f.Clock.Advance(TimeSpan.FromHours(8));
        f.Host.Tick();

        Assert.Equal(0, f.Usage("calc.exe").UsedMinutes);
    }

    [Fact]
    public void Usage_resets_on_a_new_day()
    {
        using var f = Build(calcLimit: 30, TimeSpan.FromHours(6));
        f.Probe.Keys = ["calc.exe"];
        f.Host.StartGuard();

        f.RunMinutes(10);
        Assert.Equal(10, f.Usage("calc.exe").UsedMinutes);

        f.Calendar.Today = Wednesday.AddDays(1);
        f.Clock.Advance(TimeSpan.FromHours(12));
        f.Host.Tick();

        Assert.Equal(0, f.Usage("calc.exe").UsedMinutes);
        Assert.Contains(f.Host.EnforcedDesk!.Apps, a => a.Key == "calc.exe");
    }

    [Fact]
    public void Usage_survives_a_restart_within_the_same_day()
    {
        var f = Build(calcLimit: null, TimeSpan.FromHours(6));
        f.Probe.Keys = ["calc.exe"];
        f.Host.StartGuard();
        f.RunMinutes(10);
        f.Host.Dispose();

        var reloaded = new AppUsageStore(AppUsagePath).Load(Wednesday);
        Assert.True(reloaded.TryGetValue("calc.exe", out var seconds));
        Assert.Equal(10, (int)Math.Round(seconds / 60d));
    }

    [Fact]
    public void Changing_the_limit_takes_effect_immediately()
    {
        using var f = Build(calcLimit: 30, TimeSpan.FromHours(6));
        f.Probe.Keys = ["calc.exe"];
        f.Host.StartGuard();
        f.RunMinutes(10);

        // 家长把 30 分钟改成 5 分钟：已经用了 10 分钟，应立刻算超额。
        var desk = f.Host.Desks.Single(d => d.Id == TestDeskId);
        f.Host.SaveDesk(desk.WithAppLimit("calc.exe", 5));

        Assert.True(f.Usage("calc.exe").Exhausted);
        Assert.DoesNotContain(f.Host.EnforcedDesk!.Apps, a => a.Key == "calc.exe");
    }
}

public class AppUsageStoreTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "chengshi-appusage-store-" + Guid.NewGuid().ToString("N"));

    public AppUsageStoreTests() => Directory.CreateDirectory(_dir);

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

    [Fact]
    public void Roundtrip_keeps_seconds_per_app()
    {
        var path = Path.Combine(_dir, "appusage.json");
        var store = new AppUsageStore(path);
        var date = new DateOnly(2026, 8, 19);
        store.Save(date, new Dictionary<string, double> { ["calc.exe"] = 600, ["notepad.exe"] = 90 });

        var loaded = new AppUsageStore(path).Load(date);

        Assert.Equal(600, loaded["calc.exe"]);
        Assert.Equal(90, loaded["notepad.exe"]);
    }

    [Fact]
    public void Another_day_reads_as_empty()
    {
        var path = Path.Combine(_dir, "appusage.json");
        var store = new AppUsageStore(path);
        var date = new DateOnly(2026, 8, 19);
        store.Save(date, new Dictionary<string, double> { ["calc.exe"] = 600 });

        Assert.Empty(new AppUsageStore(path).Load(date.AddDays(1)));
    }

    [Fact]
    public void Missing_file_reads_as_empty()
    {
        var store = new AppUsageStore(Path.Combine(_dir, "nope.json"));
        Assert.Empty(store.Load(new DateOnly(2026, 8, 19)));
    }

    [Fact]
    public void Broken_file_reads_as_empty_instead_of_throwing()
    {
        var path = Path.Combine(_dir, "broken.json");
        File.WriteAllText(path, "{ this is not json");

        var store = new AppUsageStore(path);
        Assert.Empty(store.Load(new DateOnly(2026, 8, 19)));
    }

    [Fact]
    public void Zero_seconds_are_dropped_on_save()
    {
        var path = Path.Combine(_dir, "appusage.json");
        var date = new DateOnly(2026, 8, 19);
        var store = new AppUsageStore(path);
        store.Save(date, new Dictionary<string, double> { ["calc.exe"] = 0, ["notepad.exe"] = 120 });

        var loaded = new AppUsageStore(path).Load(date);

        Assert.Single(loaded);
        Assert.Equal(120, loaded["notepad.exe"]);
    }
}
