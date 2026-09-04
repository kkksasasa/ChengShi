namespace Chengshi.Core;

/// <summary>
/// 永远放行的进程：澄时自身、没有镜像路径的内核/虚拟进程，
/// 以及确实住在 Windows 目录里的系统进程。
/// 系统名单按「文件名 + 所在目录」一起校验：孩子把游戏改名成 svchost.exe
/// 放不进 Windows 目录，也就冒充不了系统进程。
/// </summary>
public static class AlwaysAllow
{
    /// <summary>澄时自己的进程（安装目录随部署变化，只按名字认）。</summary>
    public static IReadOnlySet<string> SelfFileNames { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "chengshi.app.exe", "chengshi.service.exe" };

    /// <summary>内核与虚拟进程，取不到镜像路径，只按名字认。</summary>
    public static IReadOnlySet<string> PathlessNames { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "registry",
            "system",
            "secure system",
            "memory compression",
            "vmmem",
            "vmmemwsl",
        };

    /// <summary>Windows 目录里的系统进程；必须同时校验镜像路径确实在 Windows 目录下。</summary>
    public static IReadOnlySet<string> WindowsDirectoryNames { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer.exe",
            "dwm.exe",
            "sihost.exe",
            "ctfmon.exe",
            "runtimebroker.exe",
            "csrss.exe",
            "winlogon.exe",
            "services.exe",
            "lsass.exe",
            "smss.exe",
            "wininit.exe",
            "svchost.exe",
            "conhost.exe",
            "dllhost.exe",
            "taskmgr.exe",
            "taskhostw.exe",
            "searchhost.exe",
            "startmenuexperiencehost.exe",
            "shellexperiencehost.exe",
            "textinputhost.exe",
            "applicationframehost.exe",
            "systemsettings.exe",
            "securityhealthsystray.exe",
            "securityhealthservice.exe",
            "msmpeng.exe",
            "nissrv.exe",
            "smartscreen.exe",
            "fontdrvhost.exe",
            "crossdeviceresume.exe",
            "lockapp.exe",
            "logonui.exe",
            "userinit.exe",
            "backgroundtaskhost.exe",
            "searchindexer.exe",
            "audiodg.exe",
            "spoolsv.exe",
            "wlanext.exe",
            "unsecapp.exe",
            "wmiprvse.exe",
            "dashost.exe",
        };

    /// <summary>Defender 的引擎住在 ProgramData 平台目录里，是唯一在 Windows 目录外的系统进程。</summary>
    public static IReadOnlySet<string> DefenderNames { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "msmpeng.exe", "nissrv.exe" };

    public static bool IsAlwaysAllowed(string? fileName, string? imagePath = null)
    {
        if (string.IsNullOrWhiteSpace(fileName) && string.IsNullOrWhiteSpace(imagePath))
        {
            return true;
        }

        var name = Path.GetFileName(string.IsNullOrWhiteSpace(fileName) ? imagePath : fileName);
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        if (SelfFileNames.Contains(name))
        {
            return true;
        }

        if (PathlessNames.Contains(name))
        {
            return true;
        }

        // 系统名单里的名字还要求镜像路径可信：路径取不到就不放行（失败关闭），
        // 否则任何目录里改名成系统进程的程序都能逃过拦截。
        var candidate = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
        if (!FileMatches(candidate))
        {
            return false;
        }

        return IsInsideWindowsOrDefender(imagePath, candidate);
    }

    private static bool FileMatches(string name) =>
        WindowsDirectoryNames.Contains(name) || DefenderNames.Contains(name);

    private static bool IsInsideWindowsOrDefender(string? imagePath, string name)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return false;
        }

        var path = imagePath.Replace('/', Path.DirectorySeparatorChar);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows))
        {
            var prefix = windows.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return DefenderNames.Contains(name) && IsUnderDefenderPlatform(path);
    }

    private static bool IsUnderDefenderPlatform(string path)
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrEmpty(programData))
        {
            return false;
        }

        var prefix = Path.Combine(programData, "Microsoft", "Windows Defender", "Platform");
        return path.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
