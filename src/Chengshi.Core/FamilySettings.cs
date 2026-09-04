using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Chengshi.Core;

public sealed record FamilySettings(
    string PinHash,
    int DailyMinutes,
    string DeskId,
    bool GuardOnLaunch = true,
    bool StartWithWindows = true,
    string? RecoveryCode = null,
    bool BedtimeEnabled = true,
    int BedtimeStartHour = 22,
    int BedtimeEndHour = 7,
    int? WeekdayMinutes = null,
    int? WeekendMinutes = null,
    int BreakReminderMinutes = 45,
    string? RecoveryEmail = null,
    Dictionary<DayOfWeek, int>? Schedule = null)
{
    [JsonIgnore]
    public TimeSpan DailyLimit => LimitFor(DateTime.Now);

    /// <summary>周内/周末分开限额：周末给两档值时才生效，否则回落到统一的 DailyMinutes。</summary>
    public TimeSpan LimitFor(DateOnly date) =>
        TimeSpan.FromMinutes(MinutesFor(date));

    public TimeSpan LimitFor(DateTime date) =>
        TimeSpan.FromMinutes(MinutesFor(date.DayOfWeek));

    public int MinutesFor(DateOnly date) => MinutesFor(date.DayOfWeek);

    /// <summary>某一天的具体时长：先取「按星期排」的单独设置，否则回落到周内/周末两档。</summary>
    public int MinutesFor(DayOfWeek day)
    {
        if (Schedule is not null && Schedule.TryGetValue(day, out var custom))
        {
            return Math.Max(1, custom);
        }

        return day is DayOfWeek.Saturday or DayOfWeek.Sunday
            ? Math.Max(1, WeekendMinutes ?? DailyMinutes)
            : Math.Max(1, WeekdayMinutes ?? DailyMinutes);
    }

    /// <summary>睡觉时段（默认 22:00–07:00）内是否该断网；支持跨午夜窗口。</summary>
    public bool IsBedtime(DateTime now)
    {
        if (!BedtimeEnabled)
        {
            return false;
        }

        var hour = now.Hour;
        if (BedtimeStartHour == BedtimeEndHour)
        {
            return false;
        }

        if (BedtimeStartHour < BedtimeEndHour)
        {
            return hour >= BedtimeStartHour && hour < BedtimeEndHour;
        }

        return hour >= BedtimeStartHour || hour < BedtimeEndHour;
    }

    public bool VerifyPin(string? pin) => PinHasher.Verify(pin ?? string.Empty, PinHash);

    /// <summary>
    /// 下发给界面程序（管道客户端）的副本：抹掉密码哈希与找回码。
    /// 哈希虽不可逆，但 4 位数字密码可以离线暴力枚举，绝不该离开守护服务。
    /// </summary>
    public FamilySettings SanitizedForClient() => this with
    {
        PinHash = string.Empty,
        RecoveryCode = null,
    };

    /// <summary>
    /// 界面程序拿到的配置没有哈希/找回码；它回存设置时，用已存的密钥补齐，
    /// 避免一次普通设置修改把家长密码和找回码一起清掉。
    /// </summary>
    public FamilySettings WithPreservedSecrets(FamilySettings current) => this with
    {
        PinHash = string.IsNullOrWhiteSpace(PinHash) ? current.PinHash : PinHash,
        RecoveryCode = string.IsNullOrWhiteSpace(RecoveryCode) ? current.RecoveryCode : RecoveryCode,
    };

    public bool MatchesRecovery(string? token)
    {
        if (string.IsNullOrWhiteSpace(RecoveryCode) || string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        return string.Equals(
            PinHasher.NormalizeRecovery(RecoveryCode),
            PinHasher.NormalizeRecovery(token),
            StringComparison.OrdinalIgnoreCase);
    }

    public FamilySettings WithNewPin(string pin)
    {
        var normalized = PinHasher.NormalizePin(pin);
        if (normalized.Length < 4)
        {
            throw new ArgumentException("家长密码至少 4 位。", nameof(pin));
        }

        return this with
        {
            PinHash = PinHasher.Hash(normalized),
            RecoveryCode = string.IsNullOrWhiteSpace(RecoveryCode) ? NewRecoveryCode() : RecoveryCode,
        };
    }

    public FamilySettings EnsureRecovery() =>
        string.IsNullOrWhiteSpace(RecoveryCode) ? this with { RecoveryCode = NewRecoveryCode() } : this;

    public static FamilySettings Create(
        string pin,
        int dailyMinutes,
        string deskId,
        int? WeekdayMinutes = null,
        int? WeekendMinutes = null,
        string? recoveryCode = null,
        string? recoveryEmail = null)
    {
        var normalized = PinHasher.NormalizePin(pin);
        if (normalized.Length < 4)
        {
            throw new ArgumentException("家长密码至少 4 位。", nameof(pin));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(dailyMinutes, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(deskId);
        return new FamilySettings(
            PinHasher.Hash(normalized),
            dailyMinutes,
            deskId,
            GuardOnLaunch: true,
            StartWithWindows: true,
            RecoveryCode: recoveryCode ?? NewRecoveryCode(),
            WeekdayMinutes: WeekdayMinutes,
            WeekendMinutes: WeekendMinutes,
            RecoveryEmail: recoveryEmail);
    }

    public static string NewRecoveryCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var bytes = RandomNumberGenerator.GetBytes(8);
        Span<char> chars = stackalloc char[9];
        for (var i = 0; i < 8; i++)
        {
            chars[i < 4 ? i : i + 1] = alphabet[bytes[i] % alphabet.Length];
        }

        chars[4] = '-';
        return new string(chars);
    }
}
