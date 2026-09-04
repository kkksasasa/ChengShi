using Chengshi.Core;
using Chengshi.Ipc;

namespace Chengshi.Engine;

/// <summary>
/// 界面程序看到的守护后端：本机 SessionHost 或管道另一头的守护服务（SessionClient）。
/// </summary>
public interface ISessionControl : IDisposable
{
    IReadOnlyList<Desk> Desks { get; }
    FamilySettings? Family { get; }
    DailyBudget Budget { get; }

    /// <summary>今天每个软件用了多久、各自的单独限额是多少（没有守护时按当前书桌展示）。</summary>
    IReadOnlyList<AppUsage> AppUsage { get; }
    bool IsConfigured { get; }
    bool IsGuarding { get; }
    bool IsRemote { get; }
    string EtwHint { get; }
    string GuardHint { get; }
    SessionSnapshot Snapshot { get; }

    event Action<SessionSnapshot>? StateChanged;
    event Action<BlockedMessage>? ProcessBlocked;
    event Action<bool>? ConnectionChanged;

    Desk? FindDesk(string id);
    Desk SaveDesk(Desk desk);
    FamilySettings SaveFamily(FamilySettings settings);
    bool VerifyParentPin(string? pin);
    FamilySettings ChangePin(string oldPin, string newPin);
    FamilySettings RecoverPin(string token, string newPin);

    /// <summary>
    /// 「邮箱找回」：向预留邮箱发验证码。发码与校验都在守护服务端完成，
    /// 验证码不经过界面进程。返回的 PinResetResult 带新生成的找回码（仅此一次）。
    /// </summary>
    Task<PinResetResult> RecoverPinWithEmailAsync(string email, string code, string newPin);
    Task SendEmailRecoveryCodeAsync(string email);

    /// <summary>读 SMTP 发信配置；授权码不回传（Password 恒为空串），由 HasPassword 判断是否已存。</summary>
    Task<SmtpConfig?> GetSmtpAsync();

    /// <summary>保存 SMTP 发信配置（需家长密码授权；授权码加密后由服务落盘）。</summary>
    Task SaveSmtpAsync(SmtpConfig config);

    StartSessionResult StartGuard();
    StartSessionResult Start(string deskId, TimeSpan duration, bool pinned, string? pin);
    StopSessionResult Stop(string? pin);
    GrantExtraResult GrantExtra(string? pin, int minutes);
    SessionSnapshot Tick();
}
