using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;

namespace Chengshi.Core;

/// <summary>
/// 邮件发送出口：配置好 SMTP 后真实发信。验证码只发往家长预留的邮箱，
/// 绝不回传给调用方——界面上能看到验证码的地方就是孩子的屏幕。
/// 用可插拔接口，未来接邮件 API（Resend/SendGrid/阿里云推送）也只换一个实现。
/// </summary>
public interface IEmailSender
{
    Task SendVerificationCodeAsync(string toEmail, string code, CancellationToken cancellationToken = default);
}

/// <summary>SMTP 配置。授权码（不是邮箱登录密码）存这里，本地优先、不上云。</summary>
public sealed record SmtpConfig(
    string Host,
    int Port,
    bool UseSsl,
    string User,
    string Password)
{
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Host)
        && !string.IsNullOrWhiteSpace(User)
        && !string.IsNullOrWhiteSpace(Password)
        && Port > 0;

    /// <summary>是否填了服务器与账号（密码可以留空表示沿用已保存的）。</summary>
    public bool HasAccount => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(User) && Port > 0;

    /// <summary>QQ / 新浪等常见服务商的 host/端口/加密预设；User 与 Password 仍需家长填自己的。</summary>
    public static SmtpConfig? Preset(string provider) => provider.Trim().ToLowerInvariant() switch
    {
        "qq" => new SmtpConfig("smtp.qq.com", 587, true, string.Empty, string.Empty),
        "sina" or "sina.com" or "新浪" => new SmtpConfig("smtp.sina.com", 465, true, string.Empty, string.Empty),
        "163" or "网易" => new SmtpConfig("smtp.163.com", 465, true, string.Empty, string.Empty),
        _ => null,
    };
}

/// <summary>真实 SMTP 发送器，使用 System.Net.Mail（.NET 自带，QQ/新浪/163 通用）。</summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly SmtpConfig _cfg;

    public SmtpEmailSender(SmtpConfig cfg) => _cfg = cfg;

    public async Task SendVerificationCodeAsync(string toEmail, string code, CancellationToken ct = default)
    {
        using var message = new MailMessage
        {
            From = new MailAddress(_cfg.User),
            Subject = "澄时 · 家长密码找回验证码",
            Body = $"你正在通过备用邮箱找回澄时家长密码。\n验证码：{code}\n（10 分钟内有效，请勿告诉他人。）",
        };
        message.To.Add(toEmail);

        using var client = new SmtpClient(_cfg.Host, _cfg.Port)
        {
            EnableSsl = _cfg.UseSsl,
            Credentials = new NetworkCredential(_cfg.User, _cfg.Password),
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };

        await client.SendMailAsync(message);
    }
}

/// <summary>
/// 管理「邮箱 + 验证码」找回的验证码生成、有效期与校验。验证码只存在内存里，进程重启即失效。
/// 同一邮箱两次发码至少间隔一分钟，验证码连错 10 次即作废——防止被当作在线猜码的口子。
/// </summary>
public sealed class PinRecoveryService
{
    private readonly IEmailSender _sender;
    private readonly Dictionary<string, PendingCode> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ResendCooldown = TimeSpan.FromMinutes(1);
    private const int MaxVerifyAttempts = 10;

    public PinRecoveryService(IEmailSender sender) => _sender = sender;

    /// <summary>
    /// 生成并发送验证码。验证码本身不返回给调用方。
    /// 发得太频繁时抛 <see cref="InvalidOperationException"/>，把剩余等待秒数放进消息。
    /// </summary>
    public async Task RequestCodeAsync(string email, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(email, out var existing))
            {
                var wait = existing.RequestedAt + ResendCooldown - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    throw new InvalidOperationException($"验证码刚发过，请 {Math.Ceiling(wait.TotalSeconds)} 秒后再试。");
                }
            }
        }

        var code = GenerateCode();
        lock (_gate)
        {
            _pending[email] = new PendingCode(code, DateTime.UtcNow + Lifetime, DateTime.UtcNow);
        }

        await _sender.SendVerificationCodeAsync(email, code, ct);
    }

    public bool VerifyCode(string email, string code)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(email, out var pending))
            {
                return false;
            }

            if (DateTime.UtcNow > pending.Expiry)
            {
                _pending.Remove(email);
                return false;
            }

            if (string.Equals(pending.Code, code.Trim(), StringComparison.Ordinal))
            {
                _pending.Remove(email);
                return true;
            }

            // 连错太多次就让这枚验证码作废，家长重新申请即可。
            pending.Attempts++;
            if (pending.Attempts >= MaxVerifyAttempts)
            {
                _pending.Remove(email);
            }

            return false;
        }
    }

    private static string GenerateCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(4);
        var value = BitConverter.ToUInt32(bytes, 0) % 1_000_000;
        return value.ToString("D6");
    }

    private sealed class PendingCode(string code, DateTime expiry, DateTime requestedAt)
    {
        public string Code { get; } = code;
        public DateTime Expiry { get; } = expiry;
        public DateTime RequestedAt { get; } = requestedAt;
        public int Attempts { get; set; }
    }
}
