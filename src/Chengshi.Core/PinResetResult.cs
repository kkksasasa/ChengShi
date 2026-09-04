namespace Chengshi.Core;

/// <summary>
/// 家长密码重置的结果。走邮箱验证码重置时会生成新的找回码，
/// 只在这里（应答当场）返回一次给家长抄写，之后不再下发。
/// </summary>
public sealed record PinResetResult(
    FamilySettings Family,
    string Hint,
    string? NewRecoveryCode = null);
