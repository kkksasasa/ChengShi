using System.Text.Json;
using Chengshi.Core;

namespace Chengshi.Engine;

public sealed record UsageDay(DateOnly Date, int UsedMinutes, int BlockedCount);

/// <summary>
/// 每天的用量流水（JSON Lines，一天一行）：跨天重置时把昨天的总用量和拦截次数落盘，
/// 给家长看「最近一周用了多少」。写失败静默——报表缺一天不比守护中断更糟。
/// </summary>
public sealed class UsageLogStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string FilePath { get; }

    /// <summary>最近一次写盘失败的原因；成功后清空。</summary>
    public string? LastError { get; private set; }

    public static string DefaultFilePath { get; } = Path.Combine(StorePaths.Root, "usagelog.jsonl");

    public UsageLogStore(string? path = null)
    {
        FilePath = path ?? DefaultFilePath;
    }

    public void Append(UsageDay day)
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(FilePath, JsonSerializer.Serialize(day, Json) + Environment.NewLine);
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    /// <summary>最近 count 天（含今天有记录的话），按日期倒序；损坏的行跳过。</summary>
    public IReadOnlyList<UsageDay> ReadRecent(int count)
    {
        var days = new List<UsageDay>();
        try
        {
            if (!File.Exists(FilePath))
            {
                return days;
            }

            foreach (var line in File.ReadLines(FilePath))
            {
                try
                {
                    var day = JsonSerializer.Deserialize<UsageDay>(line, Json);
                    if (day is not null)
                    {
                        days.Add(day);
                    }
                }
                catch (JsonException)
                {
                    // 半行坏数据不影响其余历史。
                }
            }
        }
        catch (Exception)
        {
            // 读不到就当没有历史。
        }

        return days
            .OrderByDescending(d => d.Date)
            .Take(Math.Max(0, count))
            .ToList();
    }
}
