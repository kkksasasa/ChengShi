using Chengshi.Core;
using Xunit;

namespace Chengshi.Core.Tests;

public class SessionStateMachineTests
{
    [Fact]
    public void Session_ends_by_unbiased_clock_not_wall_clock()
    {
        var clock = new ManualClock { Elapsed = TimeSpan.FromMinutes(10) };
        var machine = new SessionStateMachine(clock);
        var start = machine.Start(BuiltinDesks.Spike(), TimeSpan.FromMinutes(50), pinned: false, pin: null);
        Assert.Equal(StartSessionStatus.Started, start.Status);
        Assert.Equal(SessionPhase.InDesk, machine.Phase);

        clock.Advance(TimeSpan.FromMinutes(49));
        Assert.Equal(SessionPhase.InDesk, machine.Tick().Phase);
        Assert.Equal(TimeSpan.FromMinutes(1), machine.Tick().Remaining);

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(SessionPhase.Idle, machine.Tick().Phase);
    }

    [Fact]
    public void Pinned_session_rejects_stop_without_pin()
    {
        var clock = new ManualClock();
        var machine = new SessionStateMachine(clock);
        machine.Start(BuiltinDesks.Spike(), TimeSpan.FromMinutes(25), pinned: true, pin: "home");
        var stop = machine.Stop(pin: null);
        Assert.Equal(StopSessionStatus.PinRequired, stop.Status);
        Assert.Equal(SessionPhase.InDesk, machine.Phase);
    }

    [Fact]
    public void Pinned_session_stops_with_correct_pin()
    {
        var clock = new ManualClock();
        var machine = new SessionStateMachine(clock);
        machine.Start(BuiltinDesks.Spike(), TimeSpan.FromMinutes(25), pinned: true, pin: "home");
        var wrong = machine.Stop("nope");
        Assert.Equal(StopSessionStatus.PinRejected, wrong.Status);
        var ok = machine.Stop("home");
        Assert.Equal(StopSessionStatus.Stopped, ok.Status);
        Assert.Equal(SessionPhase.Idle, machine.Phase);
    }

    [Fact]
    public void Cannot_start_second_session()
    {
        var clock = new ManualClock();
        var machine = new SessionStateMachine(clock);
        machine.Start(BuiltinDesks.Spike(), TimeSpan.FromMinutes(10), pinned: false, pin: null);
        var second = machine.Start(BuiltinDesks.Homework(), TimeSpan.FromMinutes(10), pinned: false, pin: null);
        Assert.Equal(StartSessionStatus.AlreadyRunning, second.Status);
    }

    [Fact]
    public void Extend_grows_remaining_while_in_desk()
    {
        var clock = new ManualClock();
        var machine = new SessionStateMachine(clock);
        machine.StartParental(BuiltinDesks.Spike(), TimeSpan.FromMinutes(10), "hash");
        clock.Advance(TimeSpan.FromMinutes(9));

        var extended = machine.Extend(TimeSpan.FromMinutes(15));

        Assert.Equal(TimeSpan.FromMinutes(16), extended.Remaining);
        Assert.Equal(SessionPhase.InDesk, machine.Phase);
        clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(SessionPhase.TimeUp, machine.Phase);
    }

    [Fact]
    public void Extend_is_ignored_when_idle_or_locked_out()
    {
        var clock = new ManualClock();
        var machine = new SessionStateMachine(clock);
        var idle = machine.Extend(TimeSpan.FromMinutes(5));
        Assert.Equal(SessionPhase.Idle, idle.Phase);

        var family = FamilySettings.Create("home", 1, BuiltinDesks.SpikeId);
        machine.StartParental(BuiltinDesks.Spike(), TimeSpan.Zero, family.PinHash);
        Assert.Equal(SessionPhase.TimeUp, machine.Phase);

        var lockedOut = machine.Extend(TimeSpan.FromMinutes(5));
        Assert.Equal(TimeSpan.Zero, lockedOut.Remaining);
        Assert.Equal(SessionPhase.TimeUp, machine.Phase);
    }
}
