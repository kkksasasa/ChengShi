using System.Text.Json;
using System.Text.Json.Serialization;
using Chengshi.Core;

namespace Chengshi.Engine;

public sealed class FamilyStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private FamilyStore(string filePath)
    {
        FilePath = filePath;
    }

    public string FilePath { get; }

    /// <summary>最近一次写盘失败的原因；成功后清空。守护不因配置写不进去而中断。</summary>
    public string? LastError { get; private set; }

    public FamilySettings? Settings { get; private set; }

    public static string DefaultFilePath { get; } = Path.Combine(StorePaths.Root, "family.json");

    public static FamilyStore Load(string? path = null)
    {
        var filePath = path ?? DefaultFilePath;
        var store = new FamilyStore(filePath);
        try
        {
            if (File.Exists(filePath))
            {
                var loaded = JsonSerializer.Deserialize<FamilySettings>(File.ReadAllText(filePath), Json);
                if (loaded is not null && !string.IsNullOrWhiteSpace(loaded.PinHash) && loaded.DailyMinutes > 0)
                {
                    store.Settings = loaded;
                }
            }
        }
        catch (Exception)
        {
            // 损坏则当作未设置。
        }

        return store;
    }

    /// <summary>
    /// 重新读盘。文件读不到或读坏时保留内存里的现状：
    /// 守护服务绝不能因为配置文件一时失踪就把守护整个丢掉。
    /// </summary>
    public void Reload()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            var loaded = JsonSerializer.Deserialize<FamilySettings>(File.ReadAllText(FilePath), Json);
            if (loaded is not null && !string.IsNullOrWhiteSpace(loaded.PinHash) && loaded.DailyMinutes > 0)
            {
                Settings = loaded;
            }
        }
        catch (Exception)
        {
            // 保持现状。
        }
    }

    public FamilySettings Save(FamilySettings settings)
    {
        Settings = settings;
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Json));
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }

        return settings;
    }
}
