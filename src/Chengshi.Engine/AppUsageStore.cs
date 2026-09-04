using System.Text.Json;
using Chengshi.Core;

namespace Chengshi.Engine;

/// <summary>
/// 按软件的当天用量持久化。一天一个文件，跨天自然作废——
/// 与 ScreenTimeStore 一样，写失败只记录原因，不能因为记账失败打断守护。
/// </summary>
public sealed class AppUsageStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string FilePath { get; }

    /// <summary>最近一次写盘失败的原因；成功后清空。</summary>
    public string? LastError { get; private set; }

    public static string DefaultFilePath { get; } = Path.Combine(StorePaths.Root, "appusage.json");

    public AppUsageStore(string? path = null)
    {
        FilePath = path ?? DefaultFilePath;
    }

    /// <summary>读出某一天的记录；文件缺失、损坏、日期不符都当作「还没有用量」。</summary>
    public IReadOnlyDictionary<string, double> Load(DateOnly date)
    {
        var rows = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(FilePath))
            {
                return rows;
            }

            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(FilePath), Json);
            if (dto is null || !DateOnly.TryParse(dto.Date, out var saved) || saved != date)
            {
                return rows;
            }

            foreach (var app in dto.Apps ?? [])
            {
                if (!string.IsNullOrWhiteSpace(app.Key) && app.Seconds > 0)
                {
                    rows[app.Key] = app.Seconds;
                }
            }
        }
        catch (Exception)
        {
            // 损坏则从零开始，缺一天用量不比守护中断更糟。
        }

        return rows;
    }

    public void Save(DateOnly date, IReadOnlyDictionary<string, double> seconds)
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var dto = new Dto(
                date.ToString("yyyy-MM-dd"),
                seconds
                    .Where(pair => pair.Value > 0)
                    .Select(pair => new AppDto(pair.Key, pair.Value))
                    .ToArray());
            File.WriteAllText(FilePath, JsonSerializer.Serialize(dto, Json));
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    internal sealed record Dto(string Date, AppDto[]? Apps);

    internal sealed record AppDto(string Key, double Seconds);
}
