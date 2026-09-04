using Chengshi.Core;
using Xunit;

namespace Chengshi.Engine.Tests;

public class ProcessEnforcerTests
{
    private sealed class RecordingEnforcer : ProcessEnforcer
    {
        public RecordingEnforcer(Func<int>? activeSession = null)
            : base(activeSession)
        {
        }

        public List<int> Killed { get; } = [];
        public List<ProcessIdentity> Reported { get; } = [];

        protected override bool TryKill(int pid)
        {
            Killed.Add(pid);
            return true;
        }

        public void RaiseBlocked(ProcessIdentity process) => OnBlocked(process);
    }

    private static ProcessIdentity Process(string fileName, int sessionId) =>
        new(1234 + sessionId, 1, fileName, null, null, null, sessionId);

    [Fact]
    public void Allowed_app_is_not_killed()
    {
        var enforcer = new RecordingEnforcer(() => 42);
        var desk = BuiltinDesks.Code();
        Assert.False(enforcer.TryEnforce(Process("Code.exe", 42), desk));
        Assert.Empty(enforcer.Killed);
    }

    [Fact]
    public void Disallowed_app_in_active_session_is_killed_and_reported()
    {
        var enforcer = new RecordingEnforcer(() => 42);
        var desk = BuiltinDesks.Code();
        var game = Process("game.exe", 42);
        ProcessIdentity? reported = null;
        enforcer.Blocked += p => reported = p;
        Assert.True(enforcer.TryEnforce(game, desk));
        Assert.Contains(game.Pid, enforcer.Killed);
        Assert.Equal(game.Pid, reported?.Pid);
    }

    [Fact]
    public void Processes_in_other_sessions_are_never_touched()
    {
        var enforcer = new RecordingEnforcer(() => 42);
        var desk = BuiltinDesks.Code();
        Assert.False(enforcer.TryEnforce(Process("game.exe", 43), desk));
        Assert.False(enforcer.TryEnforce(Process("game.exe", 0), desk));
        Assert.Empty(enforcer.Killed);
    }

    [Fact]
    public void Nothing_is_killed_when_there_is_no_active_console_session()
    {
        var enforcer = new RecordingEnforcer(() => 0);
        var desk = BuiltinDesks.Code();
        Assert.False(enforcer.TryEnforce(Process("game.exe", 1), desk));
        Assert.Empty(enforcer.Killed);
    }

    [Fact]
    public void System_processes_are_always_allowed()
    {
        var enforcer = new RecordingEnforcer(() => 42);
        var desk = BuiltinDesks.Code();
        Assert.False(enforcer.TryEnforce(new ProcessIdentity(4, 0, "system", null, null, null, 42), desk));
        Assert.Empty(enforcer.Killed);
    }
}
