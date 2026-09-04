using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Principal;

namespace Chengshi.Setup;

/// <summary>
/// 澄时安装引导程序（自包含单文件 exe）。双击即触发 UAC 提权，把内置的 payload.zip
/// （编译时作为资源嵌进本 exe）解压到临时目录后运行其中的 install.ps1 / uninstall.ps1，
/// 复用已验证过的安装逻辑。整个安装包就是这一个 exe，无需附带任何外部文件。
/// 用法：ChengshiSetup.exe            安装
///       ChengshiSetup.exe /uninstall  卸载
/// </summary>
internal static class Program
{
    /// <summary>内嵌资源名（默认 LogicalName = RootNamespace.文件名，这里用后缀匹配更稳健）。</summary>
    private const string EmbeddedPayloadSuffix = "payload.zip";

    private static int Main(string[] args)
    {
        var isUninstall = args.Length > 0 &&
            (args[0].Equals("/uninstall", StringComparison.OrdinalIgnoreCase)
             || args[0].Equals("-u", StringComparison.OrdinalIgnoreCase)
             || args[0].Equals("uninstall", StringComparison.OrdinalIgnoreCase));

        Console.Title = isUninstall ? "澄时卸载程序" : "澄时安装程序";

        if (!IsAdministrator())
        {
            Console.WriteLine("本程序需要管理员权限才能安装/卸载系统服务。");
            Console.WriteLine("请右键 ChengshiSetup.exe，选择“以管理员身份运行”。");
            Pause();
            return 1;
        }

        // 优先使用 exe 同目录下的 payload.zip（便于调试/覆盖），否则用内嵌资源。
        var sideBySide = FindSideBySidePayload();
        Stream? embedded = null;
        if (sideBySide is null)
        {
            embedded = LoadEmbeddedPayload();
            if (embedded is null)
            {
                Console.WriteLine("内置的安装资源 (payload.zip) 未能加载，安装程序可能已损坏。");
                Console.WriteLine("请重新下载完整的 ChengshiSetup.exe。");
                Pause();
                return 1;
            }
        }

        var stage = Path.Combine(Path.GetTempPath(), "ChengshiSetup_" + Guid.NewGuid().ToString("N"));
        var tmpZip = Path.Combine(Path.GetTempPath(), "ChengshiPayload_" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            Directory.CreateDirectory(stage);
            Console.WriteLine("正在解压安装文件…");

            if (sideBySide is not null)
            {
                ZipFile.ExtractToDirectory(sideBySide, stage);
            }
            else
            {
                // 内嵌资源先落临时文件再解压（规避流可寻址性差异）。
                using (embedded)
                using (var fs = File.Create(tmpZip))
                {
                    embedded!.CopyTo(fs);
                }

                if (!File.Exists(tmpZip) || new FileInfo(tmpZip).Length < 1024)
                {
                    Console.WriteLine("内嵌安装资源解压失败（临时文件异常）。");
                    Pause();
                    return 1;
                }

                ZipFile.ExtractToDirectory(tmpZip, stage);
            }

            var script = isUninstall ? "uninstall.ps1" : "install.ps1";
            var scriptPath = Path.Combine(stage, script);
            if (!File.Exists(scriptPath))
            {
                Console.WriteLine($"解压后找不到 {script}。");
                Pause();
                return 1;
            }

            Console.WriteLine(isUninstall ? "正在卸载澄时守护服务…" : "正在安装澄时守护服务（复制到 Program Files、注册开机自启服务）…");
            Console.WriteLine(new string('-', 60));

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = false,
            };
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                Console.WriteLine("无法启动 PowerShell 来执行安装脚本。");
                Pause();
                return 1;
            }

            proc.WaitForExit();

            Console.WriteLine(new string('-', 60));
            Console.WriteLine(isUninstall
                ? (proc.ExitCode == 0 ? "卸载完成。" : "卸载过程中出现问题，请查看上方输出。")
                : (proc.ExitCode == 0 ? "安装完成。重启电脑即可生效，或直接打开「澄时」开始配置。" : "安装过程中出现问题，请查看上方输出。"));
            Pause();
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine("出错：" + ex.Message);
            Pause();
            return 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(stage)) Directory.Delete(stage, true);
            }
            catch { /* 临时目录清不掉不重要 */ }

            try
            {
                if (File.Exists(tmpZip)) File.Delete(tmpZip);
            }
            catch { /* 同上 */ }
        }
    }

    /// <summary>在 exe 同目录 / 当前目录寻找可选的 payload.zip（用于开发期覆盖内嵌资源）。</summary>
    private static string? FindSideBySidePayload()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "payload.zip"),
            Path.Combine(Environment.CurrentDirectory, "payload.zip"),
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        return null;
    }

    /// <summary>从程序集内嵌资源中读取 payload.zip 流。</summary>
    private static Stream? LoadEmbeddedPayload()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(EmbeddedPayloadSuffix, StringComparison.OrdinalIgnoreCase));
        return name is null ? null : asm.GetManifestResourceStream(name);
    }

    private static bool IsAdministrator()
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

    private static void Pause()
    {
        Console.WriteLine();
        Console.WriteLine("按任意键退出…");
        try { Console.ReadKey(intercept: true); } catch { }
    }
}
