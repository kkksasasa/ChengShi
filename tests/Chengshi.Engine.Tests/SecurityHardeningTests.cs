using Chengshi.Core;
using Chengshi.Ipc;
using Xunit;

namespace Chengshi.Engine.Tests;

/// <summary>
/// 商用安全底线回归：密码哈希/找回码不出服务、客户端回存设置不清密钥、
/// 密码穷举有锁定、万能重置口令不复存在、邮箱找回全流程在服务端校验。
/// </summary>
public class SecurityHardeningTests
{
    [Fact]
    public void Config_over_pipe_never_carries_pin_hash_or_recovery_code()
    {
        var fixture = new PipeFixture();
        using (fixture)
        {
            var client = SessionClient.Connect(TimeSpan.FromSeconds(5), fixture.PipeName);
            using (client)
            {
                var family = client.Family;
                Assert.NotNull(family);
                Assert.Equal(string.Empty, family!.PinHash);
                Assert.Null(family.RecoveryCode);
                // 其他字段照常下发，界面功能不受影响。
                Assert.Equal(60, family.DailyMinutes);
            }
        }
    }

    [Fact]
    public void Client_saving_settings_does_not_wipe_pin_or_recovery_code()
    {
        var fixture = new PipeFixture();
        using (fixture)
        {
            var client = SessionClient.Connect(TimeSpan.FromSeconds(5), fixture.PipeName);
            using (client)
            {
                Assert.True(client.VerifyParentPin("1234"));
                var updated = client.SaveFamily(client.Family! with { DailyMinutes = 90 });

                // 界面拿回来的配置依然没有密钥……
                Assert.Equal(string.Empty, updated.PinHash);
                Assert.Null(updated.RecoveryCode);

                // ……但服务端的原密码和找回码都还在。
                Assert.True(fixture.Host.VerifyParentPin("1234"));
                Assert.True(fixture.Host.Family!.MatchesRecovery(fixture.Host.Family.RecoveryCode));
            }
        }
    }

    [Fact]
    public void Wrong_pin_attempts_lock_out_even_the_correct_pin()
    {
        var fixture = new PipeFixture();
        using (fixture)
        {
            for (var i = 0; i < PinGate.Threshold; i++)
            {
                Assert.False(fixture.Host.VerifyParentPin("0000"));
            }

            // 锁定期内连正确密码也被拒绝，在线穷举走不通。
            Assert.Throws<InvalidOperationException>(() => fixture.Host.VerifyParentPin("1234"));

            // 等过锁定期后恢复。
            fixture.Clock.Advance(TimeSpan.FromMinutes(2));
            Assert.True(fixture.Host.VerifyParentPin("1234"));
        }
    }

    [Fact]
    public void Master_reset_phrase_is_gone()
    {
        var fixture = new PipeFixture();
        using (fixture)
        {
            var ex = Assert.Throws<ArgumentException>(() =>
                fixture.Host.RecoverPin("我是家长", "9999"));
            Assert.Contains("找回码不对", ex.Message);
            // 原密码没被动过。
            Assert.True(fixture.Host.VerifyParentPin("1234"));
        }
    }

    [Fact]
    public async Task Email_recovery_flow_runs_server_side_and_rotates_code()
    {
        var fixture = new PipeFixture();
        using (fixture)
        {
            var host = fixture.Host;
            var family = host.SaveFamily(FamilySettings.Create("1234", 60, BuiltinDesks.CodeId)
                with { RecoveryEmail = "parent@example.com" });
            Assert.Equal("parent@example.com", family.RecoveryEmail);
            var oldRecovery = host.Family!.RecoveryCode;

            // 客户端发码请求 → 验证码由测试发信器截获（模拟家长邮箱收信）。
            await host.SendEmailRecoveryCodeAsync("parent@example.com");
            var code = CapturingEmailSender.LastCode
                ?? throw new InvalidOperationException("验证码没有被发出来。");

            // 邮箱不对 / 验证码不对都过不去。
            await Assert.ThrowsAsync<ArgumentException>(
                () => host.RecoverPinWithEmailAsync("kid@example.com", code, "9999"));
            await Assert.ThrowsAsync<ArgumentException>(
                () => host.RecoverPinWithEmailAsync("parent@example.com", "000000", "9999"));
            Assert.True(host.VerifyParentPin("1234"));

            // 正确验证码 → 重置成功，找回码换新，且只在结果里出现一次。
            var result = await host.RecoverPinWithEmailAsync("parent@example.com", code, "9999");
            Assert.True(host.VerifyParentPin("9999"));
            Assert.False(host.VerifyParentPin("1234"));
            Assert.NotEqual(oldRecovery, result.NewRecoveryCode);
            Assert.True(host.Family!.MatchesRecovery(result.NewRecoveryCode));

            // 同一枚验证码不能重放。
            await Assert.ThrowsAsync<ArgumentException>(
                () => host.RecoverPinWithEmailAsync("parent@example.com", code, "8888"));
        }
    }

    [Fact]
    public async Task Email_recovery_without_smtp_configured_fails_cleanly()
    {
        using var fixture = new PipeFixture();
        CapturingEmailSender.Configured = false;
        var host = fixture.Host;
        host.SaveFamily(FamilySettings.Create("1234", 60, BuiltinDesks.CodeId)
            with { RecoveryEmail = "parent@example.com" });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.SendEmailRecoveryCodeAsync("parent@example.com"));
    }

    [Fact]
    public void Smtp_password_is_encrypted_on_disk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chengshi-smtp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "mail.json");
            var store = new SmtpStore(path, SecretProtector.Protect, SecretProtector.Unprotect);
            store.Save(new SmtpConfig("smtp.qq.com", 587, true, "a@b.com", "secret-auth-code"));

            // 磁盘上没有明文。
            var raw = File.ReadAllText(path);
            Assert.DoesNotContain("secret-auth-code", raw);
            Assert.Contains("dpapi:v1:", raw);

            // 读回来能用。
            var loaded = store.Load();
            Assert.Equal("secret-auth-code", loaded!.Password);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>管道 + 守护宿主的公共底座。</summary>
    private sealed class PipeFixture : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "chengshi-sec-" + Guid.NewGuid().ToString("N"));

        public SessionHost Host { get; }
        public NamedPipeSessionServer Server { get; }
        public ManualClock Clock { get; }
        public string PipeName { get; } = "Chengshi-SecTest-" + Guid.NewGuid().ToString("N");

        public PipeFixture()
        {
            CapturingEmailSender.Configured = true;
            CapturingEmailSender.LastCode = null;
            Directory.CreateDirectory(_dir);
            Clock = new ManualClock();
            var calendar = new ManualCalendar();
            var familyStore = FamilyStore.Load(Path.Combine(_dir, "family.json"));
            familyStore.Save(FamilySettings.Create("1234", 60, BuiltinDesks.CodeId));
            var deskStore = DeskStore.Load(Path.Combine(_dir, "desks.json"));
            Host = new SessionHost(
                Clock,
                deskStore,
                familyStore,
                calendar,
                ScreenTimeStore.Load(calendar, TimeSpan.FromMinutes(60), Path.Combine(_dir, "time.json")),
                enforcer: new NoopEnforcer(),
                network: new NoopNetworkGuard(),
                smtpStore: new SmtpStore(
                    Path.Combine(_dir, "mail.json"),
                    protect: static s => s,
                    unprotect: static s => s),
                emailSender: CapturingEmailSender.Create());
            Server = new NamedPipeSessionServer(Host, PipeName);
            Server.Start();
        }

        public void Dispose()
        {
            Server.Dispose();
            Host.Dispose();
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (Exception)
            {
            }
        }
    }
}

/// <summary>测试发信器：截获验证码，模拟家长邮箱；可配置「未配置 SMTP」的失败场景。</summary>
public static class CapturingEmailSender
{
    public static bool Configured { get; set; } = true;

    public static string? LastCode { get; set; }

    public static IEmailSender Create() => new Sender();

    private sealed class Sender : IEmailSender
    {
        public Task SendVerificationCodeAsync(string toEmail, string code, CancellationToken ct = default)
        {
            if (!Configured)
            {
                throw new InvalidOperationException("还没有配置发信邮箱：请在家长设置里填好 SMTP（QQ/163/新浪）再使用邮箱找回。");
            }

            LastCode = code;
            return Task.CompletedTask;
        }
    }
}
