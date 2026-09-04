using Chengshi.Core;
using Microsoft.Win32;

namespace Chengshi.Engine;

/// <summary>一次要写进浏览器策略的规则快照。</summary>
public sealed record PolicySpec(IReadOnlyList<string> Blocklist, IReadOnlyList<string> Allowlist)
{
    public bool IsEmpty => Blocklist.Count == 0 && Allowlist.Count == 0;

    public string Signature =>
        string.Join("|", Blocklist.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        + "§"
        + string.Join("|", Allowlist.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
}

/// <summary>把书桌网站规则翻译成 Chrome/Edge 的 URLBlocklist/URLAllowlist 策略。</summary>
public static class SitePolicyBuilder
{
    public static PolicySpec Build(IReadOnlyList<string> allowedDomains, IReadOnlyList<string> blockedDomains)
    {
        var blocklist = new List<string>();
        var allowlist = new List<string>();

        if (allowedDomains.Count > 0)
        {
            // 白名单模式：先全封，再放行名单里的域名。
            blocklist.Add("*");
            allowlist.AddRange(Patterns(allowedDomains));
        }
        else if (blockedDomains.Count > 0)
        {
            blocklist.AddRange(Patterns(blockedDomains));
        }

        return new PolicySpec(blocklist, allowlist);
    }

    /// <summary>每个域名生成裸域名 + 子域名两条规则，避免“example.com 不挡 www.example.com”。</summary>
    public static IReadOnlyList<string> Patterns(IEnumerable<string> domains) =>
        domains
            .SelectMany(domain => new[] { $"*://{domain}", $"*://*.{domain}" })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

/// <summary>
/// 绿色上网：把网站规则写进 Chrome / Edge 的企业策略注册表（HKLM）。
/// 需要管理员权限——守护服务里生效；界面单独运行时给出提示。
/// 会话结束（或规则清空）时必须把策略键删掉，否则会一直限制家长的浏览器。
/// </summary>
public class SitePolicyGuard : IDisposable
{
    private static readonly string[] PolicyKeyPaths =
    [
        @"SOFTWARE\Policies\Google\Chrome",
        @"SOFTWARE\Policies\Microsoft\Edge",
    ];

    private readonly RegistryKey _baseKey;
    private PolicySpec? _applied;

    public SitePolicyGuard(RegistryKey? baseKey = null)
    {
        _baseKey = baseKey ?? Registry.LocalMachine;
    }

    public string? LastError { get; private set; }

    public bool IsActive => _applied is { IsEmpty: false };

    public virtual bool Apply(PolicySpec spec)
    {
        if (_applied is not null && _applied.Signature == spec.Signature)
        {
            return true;
        }

        try
        {
            foreach (var path in PolicyKeyPaths)
            {
                WritePolicy(path, spec);
            }

            _applied = spec;
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
            Apply(new PolicySpec([], []));
        }
        catch (Exception)
        {
            // 尽力而为。
        }
    }

    private void WritePolicy(string keyPath, PolicySpec spec)
    {
        using var key = _baseKey.CreateSubKey(keyPath, writable: true)
            ?? throw new InvalidOperationException($"无法打开策略键 {keyPath}。");
        if (spec.IsEmpty)
        {
            key.DeleteValue("URLBlocklistEnabled", throwOnMissingValue: false);
            key.DeleteValue("URLBlocklist", throwOnMissingValue: false);
            key.DeleteValue("URLAllowlist", throwOnMissingValue: false);
            return;
        }

        key.SetValue("URLBlocklistEnabled", 1, RegistryValueKind.DWord);
        key.SetValue("URLBlocklist", spec.Blocklist.ToArray(), RegistryValueKind.MultiString);
        if (spec.Allowlist.Count > 0)
        {
            key.SetValue("URLAllowlist", spec.Allowlist.ToArray(), RegistryValueKind.MultiString);
        }
        else
        {
            key.DeleteValue("URLAllowlist", throwOnMissingValue: false);
        }
    }
}
