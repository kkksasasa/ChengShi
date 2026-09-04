namespace Chengshi.Core;

public static class BuiltinDesks
{
    public const string HomeworkId = "homework";
    public const string ClassId = "class";
    public const string CodeId = "code";
    public const string SpikeId = "spike";
    public const string LockdownId = "lockdown";

    public static IReadOnlyList<Desk> All { get; } =
    [
        Homework(),
        Class(),
        Code(),
        Spike(),
    ];

    public static IReadOnlyList<Desk> Templates { get; } =
    [
        Homework(),
        Class(),
        Code(),
    ];

    public static Desk? Find(string id)
    {
        if (string.Equals(id, LockdownId, StringComparison.OrdinalIgnoreCase))
        {
            return Lockdown();
        }

        return All.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public static Desk Homework() => new(
        HomeworkId,
        "写作业",
        "文档、词典、计算器",
        [
            App("Word", "winword"),
            App("Excel", "excel"),
            App("PowerPoint", "powerpnt"),
            App("WPS 文字", "wps"),
            App("WPS 表格", "et"),
            App("WPS 演示", "wpp"),
            App("WPS PDF", "wpspdf"),
            App("Acrobat", "acrobat"),
            App("Adobe Reader", "acrord32"),
            App("Edge", "msedge"),
            App("计算器", "calc"),
            App("计算器", "calculatorapp"),
            App("记事本", "notepad"),
            App("Notepad++", "notepad++"),
            App("有道词典", "youdaodict"),
            App("GoldenDict", "GoldenDict"),
        ],
        DisconnectNetwork: true);

    public static Desk Class() => new(
        ClassId,
        "网课",
        "浏览器与笔记",
        [
            App("Edge", "msedge"),
            App("Chrome", "chrome"),
            App("Firefox", "firefox"),
            App("Word", "winword"),
            App("WPS 文字", "wps"),
            App("PowerPoint", "powerpnt"),
            App("WPS 演示", "wpp"),
            App("记事本", "notepad"),
            App("OBS", "obs64"),
        ],
        BlockCategories: ["video", "games", "adult"]);

    public static Desk Code() => new(
        CodeId,
        "编程",
        "IDE、终端、文档",
        [
            App("Visual Studio", "devenv"),
            App("VS Code", "Code"),
            App("Cursor", "Cursor"),
            App("Rider", "rider64"),
            App("IntelliJ IDEA", "idea64"),
            App("PyCharm", "pycharm64"),
            App("Windows Terminal", "WindowsTerminal"),
            App("终端", "wt"),
            App("命令提示符", "cmd"),
            App("PowerShell", "powershell"),
            App("PowerShell 7", "pwsh"),
            App("Edge", "msedge"),
            App("Chrome", "chrome"),
            App("记事本", "notepad"),
            App("Notepad++", "notepad++"),
            App("Git", "git"),
        ],
        BlockCategories: ["games", "adult"]);

    public static Desk Lockdown() => new(
        LockdownId,
        "时间用完",
        "今天不能再打开其他软件",
        []);

    public static Desk Spike() => new(
        SpikeId,
        "尖刺",
        "只留计算器",
        [
            App("计算器", "calc"),
            App("计算器", "calculatorapp"),
        ]);

    private static AllowedApp App(string displayName, string fileName) => new(displayName, fileName);
}
