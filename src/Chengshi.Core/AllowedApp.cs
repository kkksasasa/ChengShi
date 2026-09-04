namespace Chengshi.Core;

public sealed record AllowedApp
{
    /// <summary>单独限时的合理区间：太短没有意义，太长就不如直接不限。</summary>
    public const int MinDailyMinutes = 5;
    public const int MaxDailyMinutes = 600;

    public AllowedApp(string displayName, string fileName, string? imagePath = null, int? dailyMinutes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        DisplayName = displayName.Trim();
        FileName = NormalizeFileName(fileName);
        ImagePath = string.IsNullOrWhiteSpace(imagePath) ? null : imagePath.Trim();
        DailyMinutes = NormalizeLimit(dailyMinutes);
    }

    public string DisplayName { get; }
    public string FileName { get; }
    public string? ImagePath { get; }

    /// <summary>今天最多能用多少分钟；为空表示不单独限时，只受每天总时长约束。</summary>
    public int? DailyMinutes { get; }

    public string Key => ImagePath ?? FileName;

    public IEnumerable<AllowRule> ToRules()
    {
        yield return new FileNameAllowRule(FileName);
        if (!string.IsNullOrWhiteSpace(ImagePath))
        {
            yield return new PathAllowRule(ImagePath);
        }
    }

    /// <summary>记录是只读的，改限额只能换一个新实例。</summary>
    public AllowedApp WithDailyMinutes(int? minutes) =>
        DailyMinutes == minutes
            ? this
            : new AllowedApp(DisplayName, FileName, ImagePath, minutes);

    /// <summary>把家长输入的分钟数收进合法区间；0/负数/null 都按「不限时」处理。</summary>
    public static int? NormalizeLimit(int? minutes) =>
        minutes is null ? null : (int?)Math.Clamp(minutes.Value, MinDailyMinutes, MaxDailyMinutes);

    private static string NormalizeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName.Trim());
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name += ".exe";
        }

        return name;
    }
}
