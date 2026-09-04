using Chengshi.Core;
using Xunit;

namespace Chengshi.Core.Tests;

public class AlwaysAllowTests
{
    private static string WindowsPath(string file) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", file);

    [Theory]
    [InlineData("cursor.exe")]
    [InlineData("Cursor Helper.exe")]
    [InlineData("Cursor Helper (GPU).exe")]
    public void Cursor_is_not_always_allowed_any_more(string fileName)
    {
        // Cursor 不是系统进程：家长想让孩子用，应该把它加进书桌名单，
        // 而不是永远放行（旧版按名字前缀放行 cursor* 是个可以自由改名的口子）。
        Assert.False(AlwaysAllow.IsAlwaysAllowed(fileName));
    }

    [Fact]
    public void Cursor_install_path_is_not_always_allowed()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var path = Path.Combine(local, "Programs", "cursor", "Cursor.exe");
        Assert.False(AlwaysAllow.IsAlwaysAllowed(null, path));
        Assert.False(AlwaysAllow.IsAlwaysAllowed("unknown.exe", path));
    }

    [Fact]
    public void Unrelated_apps_are_not_always_allowed()
    {
        Assert.False(AlwaysAllow.IsAlwaysAllowed("notepad.exe"));
        Assert.False(AlwaysAllow.IsAlwaysAllowed("chrome.exe"));
    }

    [Fact]
    public void System_names_only_pass_inside_windows_directory()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system32 = Path.Combine(windows, "System32");

        // 系统进程名 + 真实系统路径 → 放行。
        Assert.True(AlwaysAllow.IsAlwaysAllowed("svchost.exe", WindowsPath("svchost.exe")));
        Assert.True(AlwaysAllow.IsAlwaysAllowed("svchost.exe", Path.Combine(system32, "wbem", "wmiprvse.exe")));

        // 同样的名字落在别的目录 → 冒充，不放行。
        Assert.False(AlwaysAllow.IsAlwaysAllowed("svchost.exe", Path.Combine(@"C:\Users\kid\Downloads", "svchost.exe")));
        Assert.False(AlwaysAllow.IsAlwaysAllowed("explorer.exe", @"D:\evil\explorer.exe"));

        // 系统进程名但取不到路径 → 失败关闭，不放行。
        Assert.False(AlwaysAllow.IsAlwaysAllowed("taskmgr.exe", null));
        Assert.False(AlwaysAllow.IsAlwaysAllowed("svchost.exe"));
    }

    [Fact]
    public void Defender_engine_is_allowed_from_its_platform_directory()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var engine = Path.Combine(
            programData, "Microsoft", "Windows Defender", "Platform", "1.1.0.0", "MsMpEng.exe");
        Assert.True(AlwaysAllow.IsAlwaysAllowed("MsMpEng.exe", engine));
        Assert.False(AlwaysAllow.IsAlwaysAllowed("MsMpEng.exe", @"D:\evil\MsMpEng.exe"));
    }

    [Fact]
    public void Pathless_kernel_processes_stay_allowed()
    {
        Assert.True(AlwaysAllow.IsAlwaysAllowed("System"));
        Assert.True(AlwaysAllow.IsAlwaysAllowed("Registry"));
        Assert.True(AlwaysAllow.IsAlwaysAllowed("Memory Compression"));
        Assert.True(AlwaysAllow.IsAlwaysAllowed("vmmem"));
    }

    [Fact]
    public void Chengshi_self_processes_stay_allowed()
    {
        Assert.True(AlwaysAllow.IsAlwaysAllowed("chengshi.app.exe"));
        Assert.True(AlwaysAllow.IsAlwaysAllowed("Chengshi.Service.exe"));
    }

    [Fact]
    public void Missing_identity_is_allowed_to_avoid_killing_unknowns()
    {
        // 连名字都拿不到的进程不碰（保持旧行为：避免误杀系统关键进程）。
        Assert.True(AlwaysAllow.IsAlwaysAllowed(null, null));
        Assert.True(AlwaysAllow.IsAlwaysAllowed("", ""));
    }
}
