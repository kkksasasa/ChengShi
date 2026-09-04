using Chengshi.Core;
using Xunit;

namespace Chengshi.Engine.Tests;

public class StoreReloadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chengshi-tests-" + Guid.NewGuid().ToString("N"));

    public StoreReloadTests()
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

    private string FamilyPath => Path.Combine(_dir, "family.json");
    private string DeskPath => Path.Combine(_dir, "desks.json");

    [Fact]
    public void FamilyStore_reload_picks_up_external_changes()
    {
        var path = FamilyPath;
        var store = FamilyStore.Load(path);
        Assert.Null(store.Settings);
        store.Save(FamilySettings.Create("1234", 60, BuiltinDesks.CodeId));

        var other = FamilyStore.Load(path);
        other.Save(other.Settings!.WithNewPin("5678"));

        store.Reload();
        Assert.NotNull(store.Settings);
        Assert.True(store.Settings!.VerifyPin("5678"));
        Assert.False(store.Settings.VerifyPin("1234"));
    }

    [Fact]
    public void DeskStore_reload_picks_up_external_changes()
    {
        var path = DeskPath;
        var store = DeskStore.Load(path);
        Assert.Equal(BuiltinDesks.Templates.Count, store.Desks.Count);

        var other = DeskStore.Load(path);
        var homework = other.Desks.First(d => d.Id == BuiltinDesks.HomeworkId);
        other.Upsert(homework.WithApps(homework.Apps.Append(new AllowedApp("扫雷", "winmine"))));

        store.Reload();
        var reloaded = store.Desks.First(d => d.Id == BuiltinDesks.HomeworkId);
        Assert.Contains(reloaded.Apps, a => a.FileName.Equals("winmine.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MigrateFiles_fills_missing_files_without_overwriting()
    {
        var source = Path.Combine(_dir, "old");
        var target = Path.Combine(_dir, "new");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "family.json"), "family-from-old");
        File.WriteAllText(Path.Combine(source, "desks.json"), "desks-from-old");
        File.WriteAllText(Path.Combine(target, "desks.json"), "desks-from-new");

        StorePaths.MigrateFiles(source, target);

        Assert.Equal("family-from-old", File.ReadAllText(Path.Combine(target, "family.json")));
        Assert.Equal("desks-from-new", File.ReadAllText(Path.Combine(target, "desks.json")));
        Assert.False(File.Exists(Path.Combine(target, "screentime.json")));
    }

    [Fact]
    public void MigrateFiles_ignores_missing_or_same_directory()
    {
        var target = Path.Combine(_dir, "target");
        StorePaths.MigrateFiles(Path.Combine(_dir, "does-not-exist"), target);
        Assert.False(Directory.Exists(target));

        Directory.CreateDirectory(target);
        StorePaths.MigrateFiles(target, target);
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public void RefreshFromDisk_reguards_with_new_settings()
    {
        var clock = new ManualClock();
        var calendar = new ManualCalendar();
        var familyStore = FamilyStore.Load(FamilyPath);
        familyStore.Save(FamilySettings.Create("1234", 60, BuiltinDesks.CodeId));
        var deskStore = DeskStore.Load(DeskPath);
        using var host = new SessionHost(
            clock,
            deskStore,
            familyStore,
            calendar,
            ScreenTimeStore.Load(calendar, TimeSpan.FromMinutes(60), Path.Combine(_dir, "time.json")),
            enforcer: new NoopEnforcer(),
            network: new NoopNetworkGuard());

        var result = host.StartGuard();
        Assert.Equal(StartSessionStatus.Started, result.Status);

        // 另一个“进程”改了磁盘上的时长和书桌。
        var other = FamilyStore.Load(FamilyPath);
        other.Save(other.Settings! with { DailyMinutes = 120, DeskId = BuiltinDesks.HomeworkId });

        Assert.True(host.RefreshFromDisk());
        Assert.Equal(120, host.Family?.DailyMinutes);
        Assert.Equal(BuiltinDesks.HomeworkId, host.Snapshot.DeskId);
        Assert.True(host.IsGuarding);
    }

    [Fact]
    public void Weekend_and_weekday_budgets_follow_the_calendar()
    {
        var clock = new ManualClock();
        var calendar = new ManualCalendar { Today = new DateOnly(2026, 8, 19) }; // 周三
        var familyStore = FamilyStore.Load(FamilyPath);
        familyStore.Save(FamilySettings.Create(
            "1234", 60, BuiltinDesks.CodeId, WeekdayMinutes: 45, WeekendMinutes: 150));
        var deskStore = DeskStore.Load(DeskPath);
        using var host = new SessionHost(
            clock,
            deskStore,
            familyStore,
            calendar,
            ScreenTimeStore.Load(calendar, TimeSpan.FromMinutes(45), Path.Combine(_dir, "time.json")),
            enforcer: new NoopEnforcer(),
            network: new NoopNetworkGuard());

        var result = host.StartGuard();
        Assert.Equal(StartSessionStatus.Started, result.Status);
        Assert.Equal(TimeSpan.FromMinutes(45), host.Budget.Limit);

        // 日历翻到周六：跨天重置把当天限额换成周末档。
        calendar.Today = new DateOnly(2026, 8, 22);
        clock.Advance(TimeSpan.FromHours(72));
        host.Tick();

        Assert.True(host.IsGuarding);
        Assert.Equal(new DateOnly(2026, 8, 22), host.Budget.Date);
        Assert.Equal(TimeSpan.FromMinutes(150), host.Budget.Limit);
    }
}

public sealed class NoopEnforcer : IProcessEnforcer
{
    event Action<ProcessIdentity>? IProcessEnforcer.Blocked
    {
        add { }
        remove { }
    }

    public bool TryEnforce(ProcessIdentity process, Desk desk) => false;

    public int SweepRunning(Desk desk) => 0;
}

public sealed class NoopNetworkGuard : NetworkGuard
{
    public bool? Applied { get; private set; }

    public override bool Apply(bool block)
    {
        Applied = block;
        return true;
    }
}
