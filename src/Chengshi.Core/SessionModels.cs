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
    bool LockedOut = false)
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
}

public sealed record SessionSnapshot(
    SessionPhase Phase,
    string? DeskId,
    string? DeskName,
    TimeSpan Remaining,
    bool Pinned,
    bool DisconnectNetwork,
    bool Parental = false)
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
