using Chengshi.Engine;
using Xunit;

namespace Chengshi.Engine.Tests;

public class UsageLogStoreTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "chengshi-usagelog-" + Guid.NewGuid().ToString("N"));

    public UsageLogStoreTests() => Directory.CreateDirectory(_dir);

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

    private string LogPath => Path.Combine(_dir, "usagelog.jsonl");

    [Fact]
    public void Append_WithApps_RoundTrips()
    {
        var store = new UsageLogStore(LogPath);
        store.Append(new UsageDay(new DateOnly(2026, 9, 5), 95, 3,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["notepad.exe"] = 40,
                ["calc.exe"] = 55,
            }));

        var days = new UsageLogStore(LogPath).ReadRecent(7);
        var day = Assert.Single(days);
        Assert.Equal(new DateOnly(2026, 9, 5), day.Date);
        Assert.Equal(95, day.UsedMinutes);
        Assert.Equal(3, day.BlockedCount);
        Assert.NotNull(day.Apps);
        Assert.Equal(40, day.Apps!["notepad.exe"]);
        Assert.Equal(55, day.Apps!["calc.exe"]);
    }

    [Fact]
    public void ReadRecent_LegacyLineWithoutApps_ParsesWithNullApps()
    {
        // 升级前的日志行没有 apps 字段：统计页对这样的天只画总量，不能解析失败。
        File.WriteAllText(
            LogPath,
            """{"date":"2026-09-04","usedMinutes":60,"blockedCount":1}""" + Environment.NewLine);

        var days = new UsageLogStore(LogPath).ReadRecent(7);
        var day = Assert.Single(days);
        Assert.Equal(60, day.UsedMinutes);
        Assert.Equal(1, day.BlockedCount);
        Assert.Null(day.Apps);
    }

    [Fact]
    public void ReadRecent_CorruptLinesAreSkipped()
    {
        File.WriteAllText(LogPath,
            "not json" + Environment.NewLine +
            """{"date":"2026-09-03","usedMinutes":30,"blockedCount":0}""" + Environment.NewLine);

        var days = new UsageLogStore(LogPath).ReadRecent(7);
        var day = Assert.Single(days);
        Assert.Equal(30, day.UsedMinutes);
    }

    [Fact]
    public void ReadRecent_ReturnsMostRecentDaysFirst()
    {
        var store = new UsageLogStore(LogPath);
        store.Append(new UsageDay(new DateOnly(2026, 9, 1), 10, 0));
        store.Append(new UsageDay(new DateOnly(2026, 9, 3), 30, 0));
        store.Append(new UsageDay(new DateOnly(2026, 9, 2), 20, 0));

        var days = new UsageLogStore(LogPath).ReadRecent(2);
        Assert.Equal([new DateOnly(2026, 9, 3), new DateOnly(2026, 9, 2)], days.Select(d => d.Date));
    }
}
