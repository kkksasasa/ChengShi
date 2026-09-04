using System.Text.Json;
using System.Text.Json.Serialization;
using Chengshi.Core;

namespace Chengshi.Engine;

/// <summary>
/// SMTP 发信配置存取：数据目录里的 mail.json，授权码用 DPAPI 加密后落盘。
/// 只由守护服务写（管理员/SYSTEM 才可写目录），界面程序通过管道请求服务保存。
/// </summary>
public sealed class SmtpStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;
    private readonly Func<string, string> _protect;
    private readonly Func<string, string> _unprotect;

    public SmtpStore(string? path = null)
        : this(path, SecretProtector.Protect, SecretProtector.Unprotect)
    {
    }

    /// <summary>测试可注入恒等加解密，避免依赖 Windows DPAPI。</summary>
    public SmtpStore(string? path, Func<string, string> protect, Func<string, string> unprotect)
    {
        _filePath = path ?? DefaultFilePath;
        _protect = protect;
        _unprotect = unprotect;
    }

    public static string DefaultFilePath => Path.Combine(StorePaths.Root, "mail.json");

    public string FilePath => _filePath;

    /// <summary>最近一次写盘失败的原因；成功后清空。</summary>
    public string? LastError { get; private set; }

    public SmtpConfig? Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            var loaded = JsonSerializer.Deserialize<SmtpConfig>(File.ReadAllText(_filePath), Json);
            if (loaded is null || string.IsNullOrEmpty(loaded.Password))
            {
                return loaded;
            }

            return loaded with { Password = _unprotect(loaded.Password) };
        }
        catch (Exception ex)
        {
            FileLog.Error("service", "读取 SMTP 配置失败。", ex);
            return null;
        }
    }

    /// <summary>加密后写盘；写失败不抛，交由 LastError 提示。</summary>
    public SmtpConfig Save(SmtpConfig config)
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var toStore = string.IsNullOrEmpty(config.Password)
                ? config
                : config with { Password = _protect(config.Password) };
            File.WriteAllText(_filePath, JsonSerializer.Serialize(toStore, Json));
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }

        return config;
    }
}
