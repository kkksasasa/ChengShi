using Chengshi.Core;
using Xunit;

namespace Chengshi.Core.Tests;

public class EmailRecoveryTests
{
    /// <summary>测试替身：截获「发出的」验证码，模拟家长邮箱收信。</summary>
    private sealed class CapturingSender : IEmailSender
    {
        public List<(string Email, string Code)> Sent { get; } = [];

        public Task SendVerificationCodeAsync(string toEmail, string code, CancellationToken ct = default)
        {
            Sent.Add((toEmail, code));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void Smtp_presets_return_provider_settings()
    {
        var qq = SmtpConfig.Preset("qq");
        Assert.NotNull(qq);
        Assert.Equal("smtp.qq.com", qq!.Host);
        Assert.Equal(587, qq.Port);
        Assert.True(qq.UseSsl);

        var sina = SmtpConfig.Preset("sina");
        Assert.Equal("smtp.sina.com", sina!.Host);
        Assert.Equal(465, sina.Port);

        var netease = SmtpConfig.Preset("163");
        Assert.Equal("smtp.163.com", netease!.Host);

        Assert.Null(SmtpConfig.Preset("gmail"));
    }

    [Fact]
    public void Smtp_config_is_complete_only_when_fully_filled()
    {
        Assert.False(new SmtpConfig("smtp.qq.com", 587, true, "", "").IsComplete);
        Assert.True(new SmtpConfig("smtp.qq.com", 587, true, "a@b.com", "auth").IsComplete);
    }

    [Fact]
    public async Task Recovery_code_never_returns_to_caller_and_is_single_use()
    {
        var sender = new CapturingSender();
        var service = new PinRecoveryService(sender);
        var email = "parent@example.com";

        // 验证码只发往邮箱，调用方拿不到返回值。
        await service.RequestCodeAsync(email);
        var code = Assert.Single(sender.Sent).Code;
        Assert.Equal(6, code.Length);

        Assert.True(service.VerifyCode(email, code));
        // 用过即作废。
        Assert.False(service.VerifyCode(email, code));
        // 错误码不通过。
        Assert.False(service.VerifyCode(email, "000000"));
    }

    [Fact]
    public async Task Recovery_code_is_per_email_and_email_is_case_insensitive()
    {
        var sender = new CapturingSender();
        var service = new PinRecoveryService(sender);

        await service.RequestCodeAsync("A@Example.com");
        var codeA = sender.Sent[^1].Code;
        // 不同邮箱用同一个码不应通过。
        Assert.False(service.VerifyCode("b@example.com", codeA));
        Assert.True(service.VerifyCode("a@example.com", codeA));
    }

    [Fact]
    public async Task Recovery_code_resend_is_rate_limited()
    {
        var sender = new CapturingSender();
        var service = new PinRecoveryService(sender);
        var email = "parent@example.com";

        await service.RequestCodeAsync(email);
        // 一分钟内重复发码会被拒绝。
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RequestCodeAsync(email));
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task Recovery_code_is_invalidated_after_too_many_wrong_attempts()
    {
        var sender = new CapturingSender();
        var service = new PinRecoveryService(sender);
        var email = "parent@example.com";

        await service.RequestCodeAsync(email);
        var code = sender.Sent[^1].Code;
        for (var i = 0; i < 10; i++)
        {
            Assert.False(service.VerifyCode(email, "000000"));
        }

        // 连错 10 次后验证码作废，即使后面拿出正确的码也不通过。
        Assert.False(service.VerifyCode(email, code));
    }
}
