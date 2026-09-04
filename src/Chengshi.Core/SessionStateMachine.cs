namespace Chengshi.Core;

public sealed class SessionStateMachine
{
    private readonly IUnbiasedClock _clock;
    private readonly object _gate = new();
    private DeskSession? _current;

    public SessionStateMachine(IUnbiasedClock clock)
    {
        _clock = clock;
    }

    public SessionPhase Phase
    {
        get
        {
            lock (_gate)
            {
                ExpireIfNeeded();
                return PhaseUnlocked();
            }
        }
    }

    public DeskSession? Current
    {
        get
        {
            lock (_gate)
            {
                ExpireIfNeeded();
                return _current;
            }
        }
    }

    public SessionSnapshot Snapshot()
    {
        lock (_gate)
        {
            ExpireIfNeeded();
            return SnapshotUnlocked();
        }
    }

    public StartSessionResult Start(Desk desk, TimeSpan duration, bool pinned, string? pin)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        lock (_gate)
        {
            ExpireIfNeeded();
            if (_current is not null)
            {
                return new StartSessionResult(StartSessionStatus.AlreadyRunning, SnapshotUnlocked());
            }

            string? pinHash = null;
            if (pinned)
            {
                if (string.IsNullOrWhiteSpace(pin))
                {
                    throw new ArgumentException("钉住场次需要约定码。", nameof(pin));
                }

                pinHash = PinHasher.Hash(pin);
            }

            _current = new DeskSession(desk, _clock.Elapsed, duration, pinned, pinHash);
            return new StartSessionResult(StartSessionStatus.Started, SnapshotUnlocked());
        }
    }

    public StartSessionResult StartParental(Desk desk, TimeSpan duration, string pinHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pinHash);
        lock (_gate)
        {
            ExpireIfNeeded();
            if (_current is not null)
            {
                return new StartSessionResult(StartSessionStatus.AlreadyRunning, SnapshotUnlocked());
            }

            if (duration <= TimeSpan.Zero)
            {
                _current = Lockout(pinHash);
            }
            else
            {
                _current = new DeskSession(
                    desk,
                    _clock.Elapsed,
                    duration,
                    Pinned: true,
                    pinHash,
                    Parental: true);
            }

            return new StartSessionResult(StartSessionStatus.Started, SnapshotUnlocked());
        }
    }

    public StopSessionResult Stop(string? pin)
    {
        lock (_gate)
        {
            ExpireIfNeeded();
            if (_current is null)
            {
                return new StopSessionResult(StopSessionStatus.Idle, SnapshotUnlocked());
            }

            if (_current.Pinned)
            {
                if (string.IsNullOrWhiteSpace(pin))
                {
                    return new StopSessionResult(StopSessionStatus.PinRequired, SnapshotUnlocked());
                }

                if (!PinHasher.Verify(pin, _current.PinHash))
                {
                    return new StopSessionResult(StopSessionStatus.PinRejected, SnapshotUnlocked());
                }
            }

            _current = null;
            return new StopSessionResult(StopSessionStatus.Stopped, SnapshotUnlocked());
        }
    }

    public void Abandon()
    {
        lock (_gate)
        {
            _current = null;
        }
    }

    /// <summary>
    /// 家长批准加时：把当前书桌场次整体延长。
    /// 只对「正在书桌里」的场次有效；时间用完（锁定）状态由上层按新额度重新开场。
    /// </summary>
    public SessionSnapshot Extend(TimeSpan delta)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(delta, TimeSpan.Zero);
        lock (_gate)
        {
            ExpireIfNeeded();
            if (_current is not { LockedOut: false } current)
            {
                return SnapshotUnlocked();
            }

            _current = current with { Duration = current.Duration + delta };
            return SnapshotUnlocked();
        }
    }

    public SessionSnapshot Tick() => Snapshot();

    private void ExpireIfNeeded()
    {
        if (_current is null || !_current.IsExpired(_clock.Elapsed))
        {
            return;
        }

        if (_current.Parental)
        {
            _current = Lockout(_current.PinHash ?? string.Empty);
            return;
        }

        _current = null;
    }

    private DeskSession Lockout(string pinHash) => new(
        BuiltinDesks.Lockdown(),
        _clock.Elapsed,
        TimeSpan.FromHours(18),
        Pinned: true,
        pinHash,
        Parental: true,
        LockedOut: true);

    private SessionPhase PhaseUnlocked()
    {
        if (_current is null)
        {
            return SessionPhase.Idle;
        }

        return _current.LockedOut ? SessionPhase.TimeUp : SessionPhase.InDesk;
    }

    private SessionSnapshot SnapshotUnlocked()
    {
        if (_current is null)
        {
            return new SessionSnapshot(SessionPhase.Idle, null, null, TimeSpan.Zero, false, false);
        }

        return new SessionSnapshot(
            PhaseUnlocked(),
            _current.Desk.Id,
            _current.Desk.Name,
            _current.Remaining(_clock.Elapsed),
            _current.Pinned,
            _current.Desk.DisconnectNetwork,
            _current.Parental);
    }
}
