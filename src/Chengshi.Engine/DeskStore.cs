using System.Text.Json;
using System.Text.Json.Serialization;
using Chengshi.Core;

namespace Chengshi.Engine;

public sealed class DeskStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly List<Desk> _desks;

    private DeskStore(List<Desk> desks, string filePath)
    {
        _desks = desks;
        FilePath = filePath;
    }

    public string FilePath { get; }

    /// <summary>最近一次写盘失败的原因；成功后清空。守护不因配置写不进去而中断。</summary>
    public string? LastError { get; private set; }

    public IReadOnlyList<Desk> Desks => _desks;

    public static string DefaultFilePath { get; } = Path.Combine(StorePaths.Root, "desks.json");

    public static DeskStore Load(string? path = null)
    {
        var filePath = path ?? DefaultFilePath;
        try
        {
            if (File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);
                var loaded = JsonSerializer.Deserialize<List<Desk>>(json, Json);
                if (loaded is { Count: > 0 })
                {
                    return new DeskStore(loaded, filePath);
                }
            }
        }
        catch (Exception)
        {
            // 损坏的配置回退到模板。
        }

        var store = new DeskStore(BuiltinDesks.Templates.ToList(), filePath);
        store.Save();
        return store;
    }

    /// <summary>重新读盘；读不到或读坏时保持现状。用于跨进程修改后的热刷新。</summary>
    public void Reload()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            var loaded = JsonSerializer.Deserialize<List<Desk>>(File.ReadAllText(FilePath), Json);
            if (loaded is not { Count: > 0 })
            {
                return;
            }

            _desks.Clear();
            _desks.AddRange(loaded);
        }
        catch (Exception)
        {
            // 保持现状。
        }
    }

    public Desk? Find(string id) =>
        _desks.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    public Desk Upsert(Desk desk)
    {
        var index = _desks.FindIndex(d => string.Equals(d.Id, desk.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _desks[index] = desk;
        }
        else
        {
            _desks.Add(desk);
        }

        Save();
        return desk;
    }

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(FilePath, JsonSerializer.Serialize(_desks, Json));
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }
}
