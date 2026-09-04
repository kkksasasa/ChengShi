namespace Chengshi.Core;

public sealed record ProcessIdentity(
    int Pid,
    int ParentPid,
    string FileName,
    string? ImagePath,
    string? Publisher,
    string? PackageFamilyName,
    int SessionId = 0)
{
    public static ProcessIdentity Unknown(int pid, int parentPid, string fileName) =>
        new(pid, parentPid, fileName, null, null, null);
}
