using Chengshi.Core;
using Xunit;

namespace Chengshi.Core.Tests;

public class AllowlistMatcherTests
{
    private readonly AllowlistMatcher _matcher = new();
    private readonly Desk _spike = BuiltinDesks.Spike();

    [Fact]
    public void Calculator_is_allowed_on_spike_desk()
    {
        var calc = new ProcessIdentity(100, 1, "calc.exe", @"C:\Windows\System32\calc.exe", null, null);
        Assert.True(_matcher.IsAllowed(calc, _spike));
    }

    [Fact]
    public void Notepad_is_not_allowed_on_spike_desk()
    {
        var notepad = new ProcessIdentity(101, 1, "notepad.exe", @"C:\Windows\System32\notepad.exe", null, null);
        Assert.False(_matcher.IsAllowed(notepad, _spike));
    }

    [Fact]
    public void Explorer_is_always_allowed()
    {
        var explorer = new ProcessIdentity(102, 1, "explorer.exe", @"C:\Windows\explorer.exe", null, null);
        Assert.True(_matcher.IsAllowed(explorer, _spike));
    }

    [Fact]
    public void System_idle_process_is_allowed()
    {
        var idle = new ProcessIdentity(0, 0, "System Idle Process", null, null, null);
        Assert.True(_matcher.IsAllowed(idle, _spike));
    }

    [Fact]
    public void Directory_rule_matches_children()
    {
        var desk = new Desk(
            "docs",
            "文档",
            "",
            [new AllowedApp("editor", "editor.exe", @"C:\Work\Notes\")]);
        var child = new ProcessIdentity(
            200,
            1,
            "editor.exe",
            @"C:\Work\Notes\editor.exe",
            null,
            null);
        var outsider = new ProcessIdentity(
            201,
            1,
            "game.exe",
            @"C:\Games\game.exe",
            null,
            null);
        Assert.True(_matcher.IsAllowed(child, desk));
        Assert.False(_matcher.IsAllowed(outsider, desk));
    }
}
