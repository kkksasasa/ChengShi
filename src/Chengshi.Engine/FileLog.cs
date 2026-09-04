using System.Text;

namespace Chengshi.Engine;

/// <summary>
/// 极简文件日志：写到数据目录 logs\&lt;source&gt;-YYYYMMDD.log。
/// 守护类产品出问题必须能从现场机器还原经过，控制台和事件日志都不可靠。
/// 数据目录只读（未提权的界面程序）时自动退回 %LOCALAPPDATA%\Chengshi\logs——
/// 现场排障最怕的不是报错，是什么都没留下。
/// 线程安全、写失败静默（日志永远不能反过来弄崩产品）。
/// </summary>
public static class FileLog
{
    private const int MaxFileBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();
    private static string? _resolvedDirectory;

    /// <summary>日志目录；未指定时用数据目录下的 logs。测试里可以指到临时目录。</summary>
    public static string? DirectoryOverride { get; set; }

    public static void Write(string source, string message) =>
        Write(source, "INFO", message);

    public static void Error(string source, string message, Exception? exception = null) =>
        Write(source, "ERROR", exception is null ? message : message + " :: " + exception);

    private static void Write(string source, string level, string message)
    {
        try
        {
            var directory = ResolveDirectory();
            if (directory is null)
            {
                return;
            }

            lock (Gate)
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"{source}-{DateTime.Now:yyyyMMdd}.log");
                if (File.Exists(path) && new FileInfo(path).Length > MaxFileBytes)
                {
                    // 超限就换 .old，最多留一代，避免把客户磁盘写满。
                    var old = Path.Combine(directory, $"{source}.old.log");
                    File.Copy(path, old, overwrite: true);
                    File.Delete(path);
                }

                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // 日志写不进去就算了。
        }
    }

    private static string? ResolveDirectory()
    {
        if (DirectoryOverride is not null)
        {
            return DirectoryOverride;
        }

        var cached = _resolvedDirectory;
        if (cached is not null)
        {
            return cached;
        }

        var primary = Path.Combine(StorePaths.Root, "logs");
        try
        {
            Directory.CreateDirectory(primary);
            var probe = Path.Combine(primary, ".write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return _resolvedDirectory = primary;
        }
        catch (Exception)
        {
            // 数据目录只读（普通权限的界面程序）：退回用户目录，至少留得下现场。
            var fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Chengshi",
                "logs");
            try
            {
                Directory.CreateDirectory(fallback);
                return _resolvedDirectory = fallback;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
