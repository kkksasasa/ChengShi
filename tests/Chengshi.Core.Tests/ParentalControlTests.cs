using Chengshi.Core;
using System.Collections.Generic;
using Xunit;

namespace Chengshi.Core.Tests;

public class DailyBudgetTests
{
    [Fact]
    public void Remaining_is_limit_minus_used()
    {
        var budget = new DailyBudget(new DateOnly(2026, 8, 18), TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(15));
        Assert.Equal(TimeSpan.FromMinutes(45), budget.Remaining);
        Assert.False(budget.Exhausted);
    }

    [Fact]
    public void New_day_resets_used()
    {
        var budget = new DailyBudget(new DateOnly(2026, 8, 18), TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(60));
        var next = budget.ForDay(new DateOnly(2026, 8, 19));
        Assert.Equal(TimeSpan.Zero, next.Used);
        Assert.Equal(TimeSpan.FromMinutes(60), next.Remaining);
        Assert.False(next.Exhausted);
    }

    [Fact]
    public void Used_beyond_limit_is_exhausted()
    {
        var budget = new DailyBudget(new DateOnly(2026, 8, 18), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(40));
        Assert.Equal(TimeSpan.Zero, budget.Remaining);
        Assert.True(budget.Exhausted);
    }
}

public class ParentalSessionTests
{
    [Fact]
    public void Parental_session_locks_out_instead_of_going_idle()
    {
        var clock = new ManualClock();
        var machine = new SessionStateMachine(clock);
        var family = FamilySettings.Create("home", 25, BuiltinDesks.SpikeId);
        machine.StartParental(BuiltinDesks.Spike(), TimeSpan.FromMinutes(25), family.PinHash);
        clock.Advance(TimeSpan.FromMinutes(25));
        var snapshot = machine.Tick();
        Assert.Equal(SessionPhase.TimeUp, snapshot.Phase);
        Assert.Equal(BuiltinDesks.LockdownId, snapshot.DeskId);
        Assert.Equal(TimeSpan.Zero, snapshot.Remaining);
        Assert.NotNull(machine.Current);
    }

    [Fact]
    public void Time_up_still_needs_parent_pin()
    {
        var clock = new ManualClock();
        var machine = new SessionStateMachine(clock);
        var family = FamilySettings.Create("home", 1, BuiltinDesks.SpikeId);
        machine.StartParental(BuiltinDesks.Spike(), TimeSpan.Zero, family.PinHash);
        Assert.Equal(SessionPhase.TimeUp, machine.Phase);
        Assert.Equal(StopSessionStatus.PinRequired, machine.Stop(null).Status);
        Assert.Equal(StopSessionStatus.PinRejected, machine.Stop("nope").Status);
        Assert.Equal(StopSessionStatus.Stopped, machine.Stop("home").Status);
        Assert.Equal(SessionPhase.Idle, machine.Phase);
    }

    [Fact]
    public void Family_pin_must_be_at_least_four_characters()
    {
        Assert.Throws<ArgumentException>(() => FamilySettings.Create("123", 60, "homework"));
    }

    [Fact]
    public void Full_width_digits_verify_against_ascii_pin()
    {
        var family = FamilySettings.Create("１２３６５４", 60, "homework");
        Assert.True(family.VerifyPin("123654"));
        Assert.False(family.VerifyPin("000000"));
    }

    [Fact]
    public void Recovery_code_resets_pin()
    {
        var family = FamilySettings.Create("oldpin", 60, "homework");
        Assert.False(string.IsNullOrWhiteSpace(family.RecoveryCode));
        Assert.True(family.MatchesRecovery(family.RecoveryCode));
        var next = family.WithNewPin("newpin");
        Assert.True(next.VerifyPin("newpin"));
        Assert.False(next.VerifyPin("oldpin"));
    }

    [Fact]
    public void Daily_limit_is_not_required_to_roundtrip_pin()
    {
        var family = FamilySettings.Create("secret", 60, "homework");
        var json = System.Text.Json.JsonSerializer.Serialize(
            family,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        Assert.DoesNotContain("dailyLimit", json, StringComparison.Ordinal);
        var copy = System.Text.Json.JsonSerializer.Deserialize<FamilySettings>(
            json,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        Assert.True(copy!.VerifyPin("secret"));
    }
}

public class FamilySettingsLimitTests
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void Weekday_and_weekend_limits_apply_separately()
    {
        var family = FamilySettings.Create("1234", 60, "homework") with
        {
            WeekdayMinutes = 45,
            WeekendMinutes = 150,
        };

        // 2026-08-19 是周三；8/22 周六、8/23 周日。
        Assert.Equal(TimeSpan.FromMinutes(45), family.LimitFor(new DateOnly(2026, 8, 19)));
        Assert.Equal(TimeSpan.FromMinutes(45), family.LimitFor(new DateOnly(2026, 8, 21)));
        Assert.Equal(TimeSpan.FromMinutes(150), family.LimitFor(new DateOnly(2026, 8, 22)));
        Assert.Equal(TimeSpan.FromMinutes(150), family.LimitFor(new DateOnly(2026, 8, 23)));
    }

    [Fact]
    public void Missing_weekday_or_weekend_falls_back_to_uniform_limit()
    {
        var legacyOnly = FamilySettings.Create("1234", 75, "homework");
        Assert.Equal(
            TimeSpan.FromMinutes(75),
            legacyOnly.LimitFor(new DateOnly(2026, 8, 19)));
        Assert.Equal(
            TimeSpan.FromMinutes(75),
            legacyOnly.LimitFor(new DateOnly(2026, 8, 22)));

        var weekdaySet = FamilySettings.Create("1234", 60, "homework") with { WeekdayMinutes = 30 };
        Assert.Equal(TimeSpan.FromMinutes(30), weekdaySet.LimitFor(new DateOnly(2026, 8, 19)));
        Assert.Equal(TimeSpan.FromMinutes(60), weekdaySet.LimitFor(new DateOnly(2026, 8, 22)));
    }

    [Fact]
    public void Limits_are_clamped_to_at_least_one_minute()
    {
        var family = new FamilySettings(
            PinHasher.Hash("1234"),
            0,
            "homework",
            WeekdayMinutes: -5,
            WeekendMinutes: 0);

        Assert.Equal(TimeSpan.FromMinutes(1), family.LimitFor(new DateOnly(2026, 8, 19)));
        Assert.Equal(TimeSpan.FromMinutes(1), family.LimitFor(new DateOnly(2026, 8, 22)));
    }

    [Fact]
    public void Legacy_json_without_new_fields_keeps_single_limit()
    {
        const string legacyJson =
            """{"pinHash":"cGF0aA==","dailyMinutes":45,"deskId":"homework"}""";
        var family = System.Text.Json.JsonSerializer.Deserialize<FamilySettings>(legacyJson, Json);

        Assert.NotNull(family);
        Assert.Equal(TimeSpan.FromMinutes(45), family!.LimitFor(new DateOnly(2026, 8, 19)));
        Assert.Equal(TimeSpan.FromMinutes(45), family.LimitFor(new DateOnly(2026, 8, 23)));
    }

    [Fact]
    public void New_fields_survive_a_json_roundtrip()
    {
        var family = FamilySettings.Create("1234", 60, "homework", WeekdayMinutes: 40, WeekendMinutes: 180);
        var json = System.Text.Json.JsonSerializer.Serialize(family, Json);
        var copy = System.Text.Json.JsonSerializer.Deserialize<FamilySettings>(json, Json);

        Assert.Contains("\"weekdayMinutes\":40", json, StringComparison.Ordinal);
        Assert.NotNull(copy);
        Assert.Equal(TimeSpan.FromMinutes(40), copy!.LimitFor(new DateOnly(2026, 8, 19)));
        Assert.Equal(TimeSpan.FromMinutes(180), copy.LimitFor(new DateOnly(2026, 8, 22)));
    }

    [Fact]
    public void Schedule_overrides_base_limits_per_day()
    {
        var family = FamilySettings.Create("1234", 60, "homework") with
        {
            WeekdayMinutes = 40,
            WeekendMinutes = 180,
            Schedule = new Dictionary<DayOfWeek, int>
            {
                [DayOfWeek.Friday] = 120,
                [DayOfWeek.Saturday] = 30,
            },
        };

        // 8/19 周三按基础工作日；8/21 周五被单独改成 120；8/22 周六被改成 30；8/23 周日回落周末基础值。
        Assert.Equal(TimeSpan.FromMinutes(40), family.LimitFor(new DateOnly(2026, 8, 19)));
        Assert.Equal(TimeSpan.FromMinutes(120), family.LimitFor(new DateOnly(2026, 8, 21)));
        Assert.Equal(TimeSpan.FromMinutes(30), family.LimitFor(new DateOnly(2026, 8, 22)));
        Assert.Equal(TimeSpan.FromMinutes(180), family.LimitFor(new DateOnly(2026, 8, 23)));
    }

    [Fact]
    public void Schedule_survives_json_roundtrip()
    {
        var family = FamilySettings.Create("1234", 60, "homework", WeekdayMinutes: 40, WeekendMinutes: 180) with
        {
            Schedule = new Dictionary<DayOfWeek, int>
            {
                [DayOfWeek.Friday] = 120,
                [DayOfWeek.Sunday] = 200,
            },
        };
        var json = System.Text.Json.JsonSerializer.Serialize(family, Json);
        var copy = System.Text.Json.JsonSerializer.Deserialize<FamilySettings>(json, Json);

        Assert.NotNull(copy);
        Assert.NotNull(copy!.Schedule);
        Assert.Equal(120, copy.Schedule[DayOfWeek.Friday]);
        Assert.Equal(200, copy.Schedule[DayOfWeek.Sunday]);
        Assert.Equal(TimeSpan.FromMinutes(120), copy.LimitFor(new DateOnly(2026, 8, 21)));
        Assert.Equal(TimeSpan.FromMinutes(200), copy.LimitFor(new DateOnly(2026, 8, 23)));
    }
}
