using System.Text.Json.Serialization;
using Chengshi.Core;

namespace Chengshi.Ipc;

public static class PipeNames
{
    public const string Default = "Chengshi";
}

/// <summary>
/// 请求带自增 RequestId，服务端应答原样带回；推送消息固定为 0。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloRequest), "hello")]
[JsonDerivedType(typeof(GetStateRequest), "getState")]
[JsonDerivedType(typeof(GetConfigRequest), "getConfig")]
[JsonDerivedType(typeof(StartSessionRequest), "start")]
[JsonDerivedType(typeof(StartGuardRequest), "startGuard")]
[JsonDerivedType(typeof(StopSessionRequest), "stop")]
[JsonDerivedType(typeof(SaveDeskRequest), "saveDesk")]
[JsonDerivedType(typeof(SaveFamilyRequest), "saveFamily")]
[JsonDerivedType(typeof(ChangePinRequest), "changePin")]
[JsonDerivedType(typeof(RecoverPinRequest), "recoverPin")]
[JsonDerivedType(typeof(VerifyPinRequest), "verifyPin")]
[JsonDerivedType(typeof(GrantExtraRequest), "grantExtra")]
[JsonDerivedType(typeof(EmailRecoveryCodeRequest), "emailRecoveryCode")]
[JsonDerivedType(typeof(EmailRecoveryResetRequest), "emailRecoveryReset")]
[JsonDerivedType(typeof(GetSmtpRequest), "getSmtp")]
[JsonDerivedType(typeof(SaveSmtpRequest), "saveSmtp")]
public abstract record ClientMessage
{
    public int RequestId { get; init; }
}

public sealed record HelloRequest : ClientMessage;

public sealed record GetStateRequest : ClientMessage;

public sealed record GetConfigRequest : ClientMessage;

public sealed record StartGuardRequest : ClientMessage;

public sealed record StartSessionRequest(string DeskId, int DurationMinutes, bool Pinned, string? Pin)
    : ClientMessage;

public sealed record StopSessionRequest(string? Pin) : ClientMessage;

public sealed record SaveDeskRequest(Desk Desk) : ClientMessage;

public sealed record SaveFamilyRequest(FamilySettings Settings) : ClientMessage;

public sealed record ChangePinRequest(string OldPin, string NewPin) : ClientMessage;

public sealed record RecoverPinRequest(string Token, string NewPin) : ClientMessage;

public sealed record VerifyPinRequest(string Pin) : ClientMessage;

/// <summary>孩子申请、家长当场输密码批准的加时请求；密码随消息自带校验（与停止守护同款）。</summary>
public sealed record GrantExtraRequest(int Minutes, string? Pin) : ClientMessage;

/// <summary>家长走「邮箱找回」：请求向预留邮箱发验证码。发码与校验都在守护服务端完成，
/// 验证码不经过界面进程，避免孩子进程截获或伪造。</summary>
public sealed record EmailRecoveryCodeRequest(string Email) : ClientMessage;

/// <summary>家长把收到的验证码 + 新密码交给守护服务校验并重置。</summary>
public sealed record EmailRecoveryResetRequest(string Email, string Code, string NewPin) : ClientMessage;

/// <summary>查询 SMTP 发信配置（不含授权码明文）。</summary>
public sealed record GetSmtpRequest : ClientMessage;

/// <summary>保存 SMTP 发信配置；需要家长密码授权过的连接。</summary>
public sealed record SaveSmtpRequest(SmtpConfig Config) : ClientMessage;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ErrorMessage), "error")]
[JsonDerivedType(typeof(StateMessage), "state")]
[JsonDerivedType(typeof(ConfigMessage), "config")]
[JsonDerivedType(typeof(StartReply), "startReply")]
[JsonDerivedType(typeof(StopReply), "stopReply")]
[JsonDerivedType(typeof(BoolMessage), "bool")]
[JsonDerivedType(typeof(BlockedMessage), "blocked")]
[JsonDerivedType(typeof(GrantExtraReply), "grantExtraReply")]
[JsonDerivedType(typeof(PongMessage), "pong")]
[JsonDerivedType(typeof(RecoverPinReply), "recoverPinReply")]
[JsonDerivedType(typeof(SmtpConfigReply), "smtpConfig")]
public abstract record ServerMessage
{
    public int RequestId { get; init; }
}

public sealed record PongMessage : ServerMessage;

public sealed record ErrorMessage(string Message) : ServerMessage;

public sealed record BoolMessage(bool Value) : ServerMessage;

public sealed record StateMessage(
    SessionPhase Phase,
    string? DeskId,
    string? DeskName,
    int RemainingSeconds,
    bool Pinned,
    bool DisconnectNetwork,
    bool Parental,
    string Hint,
    IReadOnlyList<AppUsage>? AppUsage = null) : ServerMessage
{
    public static StateMessage From(
        SessionSnapshot snapshot,
        string hint = "",
        IReadOnlyList<AppUsage>? appUsage = null) => new(
        snapshot.Phase,
        snapshot.DeskId,
        snapshot.DeskName,
        (int)Math.Ceiling(snapshot.Remaining.TotalSeconds),
        snapshot.Pinned,
        snapshot.DisconnectNetwork,
        snapshot.Parental,
        hint,
        appUsage);

    public SessionSnapshot ToSnapshot() => new(
        Phase,
        DeskId,
        DeskName,
        TimeSpan.FromSeconds(RemainingSeconds),
        Pinned,
        DisconnectNetwork,
        Parental);
}

public sealed record ConfigMessage(
    FamilySettings? Family,
    IReadOnlyList<Desk> Desks,
    DailyBudget Budget,
    string EtwHint,
    string GuardHint,
    SessionSnapshot Snapshot,
    IReadOnlyList<AppUsage>? AppUsage = null) : ServerMessage;

public sealed record StartReply(StartSessionStatus Status, SessionSnapshot Snapshot, string Hint) : ServerMessage;

public sealed record StopReply(StopSessionStatus Status, SessionSnapshot Snapshot, string Hint) : ServerMessage;

public sealed record GrantExtraReply(bool Ok, string Hint, SessionSnapshot Snapshot) : ServerMessage;

public sealed record BlockedMessage(int Pid, string FileName, string? ImagePath) : ServerMessage;

/// <summary>
/// 邮箱验证码重置密码的应答。NewRecoveryCode 只在这次应答里出现一次：
/// 重置会生成新找回码，家长当场抄下，之后服务不再下发。
/// </summary>
public sealed record RecoverPinReply(bool Ok, string Hint, string? NewRecoveryCode = null) : ServerMessage;

/// <summary>SMTP 发信配置的只读视图：授权码永不回传，只告诉界面「已存过密码」。</summary>
public sealed record SmtpConfigReply(
    string Host,
    int Port,
    bool UseSsl,
    string User,
    bool HasPassword,
    string? LastError = null) : ServerMessage;
