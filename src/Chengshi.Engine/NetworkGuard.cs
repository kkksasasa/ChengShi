using System.Diagnostics;

namespace Chengshi.Engine;

/// <summary>
/// 「写作业」书桌的断网：通过 Windows 防火墙出站规则挡住整机网络出口。
/// 需要管理员权限——守护服务（SYSTEM）里一定生效；界面程序单独运行时给出提示。
/// </summary>
public class NetworkGuard : IDisposable
{
    public const string RuleName = "Chengshi-NoInternet";

    private bool _blocked;

    public string? LastError { get; private set; }

    public bool IsBlocked => _blocked;

    public virtual bool Apply(bool block)
    {
        if (block == _blocked)
        {
            return true;
        }

        try
        {
            if (block)
            {
                RunNetsh($"advfirewall firewall add rule name=\"{RuleName}\" dir=out action=block profile=any");
            }
            else
            {
                RunNetsh($"advfirewall firewall delete rule name=\"{RuleName}\"");
            }

            _blocked = block;
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            Apply(false);
        }
        catch (Exception)
        {
            // 尽力而为。
        }
    }

    private static void RunNetsh(string arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "netsh.exe"),
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 netsh.exe。");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("netsh 执行超时。");
        }

        if (process.ExitCode != 0)
        {
            var message = (output.Result + error.Result).Trim();
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(message) ? "netsh 返回失败。" : message);
        }
    }
}
