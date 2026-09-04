using System.Diagnostics;
using System.Security.Principal;

namespace Chengshi.App;

/// <summary>
/// 守护服务的安装/卸载/状态查询，全部走系统自带的 sc.exe，
/// 不依赖任何 NuGet 包，离线也能用。服务以 LocalSystem 运行于 Session 0，
/// 普通用户杀不掉、开机自动启动。
/// </summary>
internal static class ServiceControl
{
    public const string ServiceName = "Chengshi";

    private static string ScPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe");

    public static bool IsInstalled()
    {
        var output = RunSc("query", "");
        return output.Contains("STATE", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsRunning()
    {
        var output = RunSc("query", "");
        return output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAdministrator()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static void Install()
    {
        var exe = ServiceExe;
        RunSc("stop", "");
        RunSc("delete", "");
        Thread.Sleep(1000);
        RunSc("create",
            $"binPath= \"\\\"{exe}\\\"\" start= auto obj= LocalSystem DisplayName= \"澄时守护服务 (Chengshi Guardian)\"");
        RunSc("description",
            "\"澄时家长守护：屏幕时间管控、进程/网络/网站拦截，开机自启、防强杀。\"");
        RunSc("start", "");
    }

    public static void Uninstall()
    {
        RunSc("stop", "");
        RunSc("delete", "");
    }

    /// <summary>以管理员身份重跑本程序并带上指定参数（用于安装/卸载服务）。</summary>
    public static void RunElevated(string argument)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Process.GetCurrentProcess().MainModule?.FileName ?? "Chengshi.App.exe",
                Arguments = argument,
                UseShellExecute = true,
                Verb = "runas",
            }
        };
        process.Start();
    }

    private static string ServiceExe
    {
        get
        {
            var baseDir = AppContext.BaseDirectory;
            var candidates = new[]
            {
                // 正式安装布局：C:\Program Files\Chengshi\App\Chengshi.App.exe -> ..\Service\Chengshi.Service.exe
                Path.Combine(baseDir, "..", "Service", "Chengshi.Service.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Chengshi", "Service", "Chengshi.Service.exe"),
                Path.Combine(baseDir, "..", "Chengshi.Service", "bin", "Release", "net10.0-windows", "win-x64", "Chengshi.Service.exe"),
                Path.Combine(baseDir, "..", "Chengshi.Service", "bin", "Release", "net10.0-windows", "Chengshi.Service.exe"),
                Path.Combine(baseDir, "..", "Chengshi.Service", "bin", "Debug", "net10.0-windows", "win-x64", "Chengshi.Service.exe"),
                Path.Combine(baseDir, "..", "Chengshi.Service", "bin", "Debug", "net10.0-windows", "Chengshi.Service.exe"),
            };
            foreach (var candidate in candidates)
            {
                var full = Path.GetFullPath(candidate);
                if (File.Exists(full))
                {
                    return full;
                }
            }

            var root = Path.GetFullPath(Path.Combine(baseDir, "..", "Chengshi.Service"));
            if (Directory.Exists(root))
            {
                var found = Directory.EnumerateFiles(root, "Chengshi.Service.exe", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (found is not null)
                {
                    return found;
                }
            }

            return Path.Combine(baseDir, "Chengshi.Service.exe");
        }
    }

    private static string RunSc(string verb, string arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(ScPath, $"{verb} {ServiceName} {arguments}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            return output;
        }
        catch
        {
            return string.Empty;
        }
    }
}
