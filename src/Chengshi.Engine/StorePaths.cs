using System.Security.AccessControl;
using System.Security.Principal;

namespace Chengshi.Engine;

/// <summary>
/// 界面程序与守护服务共享的数据目录。
/// 优先 %ProgramData%\Chengshi（服务以 SYSTEM 运行，只有这里两边都能读写），
/// 写不进时回退到 %LOCALAPPDATA%\Chengshi（开发/未安装场景）。
/// 设置环境变量 CHENGSHI_DATA_DIR 可覆盖（测试与便携部署用）。
/// </summary>
public static class StorePaths
{
    public const string EnvironmentVariable = "CHENGSHI_DATA_DIR";

    private static string? _cached;

    public static string Root
    {
        get
        {
            var cached = _cached;
            if (cached is not null)
            {
                return cached;
            }

            lock (typeof(StorePaths))
            {
                _cached ??= Resolve();
                return _cached;
            }
        }
    }

    /// <summary>把目录准备好：能建则建、能授 Users 修改权则授。</summary>
    public static void EnsureConfigured()
    {
        _ = Root;
    }

    private static string Resolve()
    {
        var overrideDir = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            try
            {
                var dir = Path.GetFullPath(overrideDir);
                TryCreate(dir);
                return dir;
            }
            catch (Exception)
            {
                // 环境变量被写坏（含非法字符）时退回默认目录，不崩溃。
            }
        }

        var programData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Chengshi");
        TryCreate(programData);

        // 目录已在（安装过服务/跑过安装脚本）就认它，哪怕当前进程没有写权
        // （普通权限的界面程序只读即可；改设置走守护服务的管道）。
        // 只有目录还不存在且建不出来（未安装的裸机）才退回 LOCALAPPDATA。
        if (Directory.Exists(programData) && IsReadable(programData))
        {
            // 界面进程（普通权限）动不了 ACL，真正收紧由守护服务/安装脚本做。
            MigrateFromLocalAppData(programData);
            return programData;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Chengshi");
    }

    private static bool IsReadable(string directory)
    {
        try
        {
            _ = Directory.EnumerateFileSystemEntries(directory);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 老版本把配置放在 %LOCALAPPDATA%\Chengshi；搬到 ProgramData 后，
    /// 把那边已有的配置补齐过来（只补目标里没有的文件），家长设置不丢。
    /// </summary>
    private static void MigrateFromLocalAppData(string target)
    {
        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Chengshi");
        MigrateFiles(local, target);
    }

    /// <summary>把 source 里的配置文件补齐到 target；目标已有同名文件时不动它。</summary>
    public static void MigrateFiles(string source, string target)
    {
        try
        {
            if (!Directory.Exists(source)
                || string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Directory.CreateDirectory(target);
            foreach (var name in new[] { "family.json", "desks.json", "screentime.json" })
            {
                var from = Path.Combine(source, name);
                var to = Path.Combine(target, name);
                if (File.Exists(from) && !File.Exists(to))
                {
                    File.Copy(from, to, overwrite: false);
                }
            }
        }
        catch (Exception)
        {
            // 迁移失败不挡启动，下次再试。
        }
    }

    private static void TryCreate(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception)
        {
            // 建不了就交给调用方决定去向。
        }
    }

    /// <summary>
    /// 数据目录只给 Users 读：配置只能由守护服务（SYSTEM）和管理员写。
    /// 否则孩子账号下的任何进程都能改 desks.json/family.json 白名单自己，
    /// 或换掉 family.json 里的密码哈希。做法是把继承断开（ProgramData 默认给
    /// Authenticated Users 留了可继承的写入面），再按 SID 精确重授三类权限。
    /// 只有提权进程（守护服务/安装脚本）真正执行；写不进去不挡启动。
    /// </summary>
    public static bool TryHardenAcl(string directory, out string? error)
    {
        error = null;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                error = "需要管理员权限才能收紧数据目录权限。";
                return false;
            }

            var info = new DirectoryInfo(directory);
            var security = info.GetAccessControl();

            // 断开继承：ProgramData 上游给 Authenticated Users/Users 的写入面不再渗进来。
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            // 兜底清掉显式的宽放行（老版本可能写过）。按 SID 比，不能按名字——
            // IdentityReference 是 SID 时 Value 形如 S-1-5-32-545，永远不等于「Users」。
            var wideWriteIdentities = new[]
            {
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            };
            foreach (var rule in security
                         .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
                         .OfType<FileSystemAccessRule>()
                         .Where(r => wideWriteIdentities.Any(id => id.Equals(r.IdentityReference)))
                         .Where(r => r.AccessControlType == AccessControlType.Allow)
                         .ToArray())
            {
                security.RemoveAccessRuleSpecific(rule);
            }

            const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null),
                FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute, Inherit, PropagationFlags.None, AccessControlType.Allow));

            info.SetAccessControl(security);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>守护服务启动时调用：把数据目录收紧成「Users 只读、SYSTEM/管理员可写」。</summary>
    public static void EnsureDataDirHardened()
    {
        if (TryHardenAcl(Root, out var error))
        {
            FileLog.Write("service", $"数据目录权限已收紧为 Users 只读。");
        }
        else if (error is not null)
        {
            FileLog.Error("service", "数据目录权限收紧失败。", new Exception(error));
        }
    }

    /// <summary>当前进程能否直接写入数据目录；不能时界面应进入只读展示模式。</summary>
    public static bool IsWritable(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return false;
            }

            var probe = Path.Combine(directory, ".write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
