namespace Chengshi.Core;

public enum SessionPhase
{
    Idle,
    InDesk,
    TimeUp,
}

public enum StopReason
{
    User,
    Expired,
    Pin,
    Cooldown,
}

public sealed record DeskSession(
    Desk Desk,
    TimeSpan StartElapsed,
    TimeSpan Duration,
    bool Pinned,
    string? PinHash,
    bool Parental = false,
    bool LockedOut = false,
    TimeSpan Grace = default)
{
    public TimeSpan EndElapsed => StartElapsed + Duration;

    public TimeSpan Remaining(TimeSpan now)
    {
        if (LockedOut)
        {
            return TimeSpan.Zero;
        }

        var left = EndElapsed - now;
        return left < TimeSpan.Zero ? TimeSpan.Zero : left;
    }

    public bool IsExpired(TimeSpan now) => !LockedOut && now >= EndElapsed;

    /// <summary>时间到但还在「保存进度」宽限内：场次没散，额度也不再走。</summary>
    public bool InGrace(TimeSpan now) => !LockedOut && now >= EndElapsed && now < EndElapsed + Grace;

    public TimeSpan GraceRemaining(TimeSpan now)
    {
        if (!InGrace(now))
        {
            return TimeSpan.Zero;
        }

        var left = EndElapsed + Grace - now;
        return left < TimeSpan.Zero ? TimeSpan.Zero : left;
    }
}

public sealed record SessionSnapshot(
    SessionPhase Phase,
    string? DeskId,
    string? DeskName,
    TimeSpan Remaining,
    bool Pinned,
    bool DisconnectNetwork,
    bool Parental = false,
    TimeSpan GraceRemaining = default)
{
    public bool IsGuarding => Parental && Phase is SessionPhase.InDesk or SessionPhase.TimeUp;
}

public enum StartSessionStatus
{
    Started,
    AlreadyRunning,
    UnknownDesk,
}

public readonly record struct StartSessionResult(StartSessionStatus Status, SessionSnapshot Snapshot);

public enum StopSessionStatus
{
    Stopped,
    Idle,
    PinRequired,
    PinRejected,
}

public readonly record struct StopSessionResult(StopSessionStatus Status, SessionSnapshot Snapshot);

public readonly record struct GrantExtraResult(bool Ok, string Hint, SessionSnapshot Snapshot);
