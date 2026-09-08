using Chengshi.Core;
using Xunit;

namespace Chengshi.Engine.Tests;

public class WorkstationLockPauseTests : IDisposable
{
    private const string TestDeskId = "lockpause-desk";
    private static readonly DateOnly TestDay = new(2026, 9, 1);

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "chengshi-lockpause-" + Guid.NewGuid().ToString("N"));

    public WorkstationLockPauseTests() => Directory.CreateDirectory(_dir);

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

    private sealed class FakeLockProbe : IWorkstationLockProbe
    {
        public bool Locked { get; set; }

        public bool IsLocked(int sessionId) => Locked;
    }

    /// <summary>只报告「正在运行」的软件，不碰真实进程。</summary>
    private sealed class FakeProbe : IRunningAppProbe
    {
        public List<string> Keys { get; set; } = [];

        public IReadOnlyCollection<string> RunningKeys(Desk desk) => Keys.ToArray();
    }

    private (SessionHost Host, ManualClock Clock, FakeLockProbe Lock, FakeProbe Apps) Build(TimeSpan daily)
    {
        var clock = new ManualClock();
        var calendar = new ManualCalendar { Today = TestDay };
        var familyStore = FamilyStore.Load(Path.Combine(_dir, "family.json"));
        familyStore.Save(FamilySettings.Create("1234", (int)daily.TotalMinutes, TestDeskId));
        var deskStore = DeskStore.Load(Path.Combine(_dir, "desks.json"));
        deskStore.Upsert(new Desk(
            TestDeskId,
            "测试桌",
            "记事本与计算器",
            [
                new AllowedApp("记事本", "notepad"),
                new AllowedApp("计算器", "calc"),
            ]));
        var lockProbe = new FakeLockProbe();
        var appProbe = new FakeProbe();
        var host = new SessionHost(
            clock,
            deskStore,
            familyStore,
            calendar,
            ScreenTimeStore.Load(calendar, daily, Path.Combine(_dir, "time.json")),
            enforcer: new NoopEnforcer(),
            network: new NoopNetworkGuard(),
            probe: appProbe,
            lockProbe: lockProbe);
        return (host, clock, lockProbe, appProbe);
    }

    private static void RunMinutes(SessionHost host, ManualClock clock, int minutes)
    {
        for (var i = 0; i < minutes; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            host.Tick();
        }
    }

    [Fact]
    public void Locked_minutes_do_not_consume_the_budget()
    {
        var (host, clock, lockProbe, _) = Build(TimeSpan.FromMinutes(60));
        using var _ = host;
        host.StartGuard();

        RunMinutes(host, clock, 10);
        Assert.Equal(TimeSpan.FromMinutes(50), host.Snapshot.Remaining);

        // 锁屏 10 分钟：剩余时间冻结在原地，「已用」也不涨。
        lockProbe.Locked = true;
        RunMinutes(host, clock, 10);
        Assert.Equal(TimeSpan.FromMinutes(50), host.Snapshot.Remaining);
        Assert.Equal(TimeSpan.FromMinutes(10), host.Budget.Used);

        // 解锁后接着正常倒计时。
        lockProbe.Locked = false;
        RunMinutes(host, clock, 1);
        Assert.Equal(TimeSpan.FromMinutes(49), host.Snapshot.Remaining);
    }

    [Fact]
    public void Per_app_usage_also_pauses_while_locked()
    {
        var (host, clock, lockProbe, apps) = Build(TimeSpan.FromHours(6));
        using var _ = host;
        apps.Keys = ["calc.exe"];
        host.StartGuard();

        RunMinutes(host, clock, 5);
        lockProbe.Locked = true;
        RunMinutes(host, clock, 10);
        lockProbe.Locked = false;
        RunMinutes(host, clock, 1);

        Assert.Equal(6, host.AppUsage.Single(row => row.Key == "calc.exe").UsedMinutes);
    }

    [Fact]
    public void Time_reaches_zero_only_after_the_save_grace_passes()
    {
        var (host, clock, _, _) = Build(TimeSpan.FromMinutes(60));
        using var _ = host;
        host.StartGuard();

        // 额度走完先进「保存进度」宽限，倒计时停在 0；宽限耗尽才真正锁。
        clock.Advance(TimeSpan.FromMinutes(60));
        var grace = host.Tick();
        Assert.Equal(SessionPhase.InDesk, grace.Phase);
        Assert.Equal(TimeSpan.Zero, grace.Remaining);
        Assert.True(grace.GraceRemaining > TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromMinutes(60), host.Budget.Used);

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(SessionPhase.TimeUp, host.Tick().Phase);
    }

    [Fact]
    public void Wts_probe_reports_session_zero_as_unlocked()
    {
        // 会话 0（服务会话）永远不该报「锁定」；真实探测失败时也必须回落到 false。
        Assert.False(WtsWorkstationLockProbe.Instance.IsLocked(0));
    }
}
