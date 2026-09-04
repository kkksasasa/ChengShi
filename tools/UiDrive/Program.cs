using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Forms;

namespace UiDrive;

// UiDrive: drive the Chengshi WPF app for audit screenshots.
//   windows <proc> <file>             dump top-level windows
//   dump    <proc> <file>             dump automation tree of all windows
//   shot    <proc> <file> [title]     foreground + screenshot window (title substring, '-' = first titled)
//   click   <proc> <title> <name>     click the center of an element whose name contains <name> ('-' = main window)
//   clickpw <proc> <title> <index>    click the center of the Nth password edit (1-based)
//   type    <text>                    send keystrokes to the foreground window (use {ENTER} etc.)
public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: uidrive <command> ...");
            return;
        }

        var command = args[0];
        var procName = args[1];

        try
        {
            switch (command)
            {
                case "windows":
                    DumpWindows(procName, args[2]);
                    break;
                case "dump":
                    DumpTree(procName, args[2]);
                    break;
                case "shot":
                    Shot(procName, args[2], args.Length > 3 ? TitleArg(args[3]) : string.Empty);
                    break;
                case "click":
                    Click(procName, TitleArg(args[2]), args[3]);
                    break;
                case "clickpw":
                    ClickPassword(procName, TitleArg(args[2]), int.Parse(args[3]));
                    break;
                case "setpw":
                    SetPassword(procName, TitleArg(args[2]), int.Parse(args[3]), args[4]);
                    break;
                case "settext":
                    SetText(procName, TitleArg(args[2]), int.Parse(args[3]), args[4]);
                    break;
                case "clickat":
                    ClickAtFraction(procName, TitleArg(args[2]), args[3], double.Parse(args[4]));
                    break;
                case "type":
                    SendKeys.SendWait(args.Length > 2 ? args[2] : string.Empty);
                    Console.WriteLine("typed");
                    break;
                default:
                    Console.WriteLine("unknown command");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("UIDRIVE ERROR: " + ex.Message);
            Environment.ExitCode = 1;
        }
    }

    private static string TitleArg(string value) => value == "-" ? string.Empty : value;

    private static Process? ResolveProcess(string name)
    {
        var processes = Process.GetProcessesByName(name);
        return processes.FirstOrDefault(p => !p.HasExited);
    }

    private sealed record TopWindow(IntPtr Handle, string Title, Rectangle Rect, bool Visible);

    private static List<TopWindow> TopWindows(int pid)
    {
        var list = new List<TopWindow>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var owner);
            if (owner != pid)
            {
                return true;
            }

            var sb = new StringBuilder(256);
            GetWindowText(h, sb, sb.Capacity);
            GetWindowRect(h, out var rect);
            list.Add(new TopWindow(h, sb.ToString(), rect, IsWindowVisible(h)));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static TopWindow Pick(int pid, string title)
    {
        var candidates = TopWindows(pid).Where(w => w.Visible).ToList();
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException("no visible windows for process " + pid);
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            // 首选有标题的主窗口；无标题的弹层（ToolTip 等）不要抢。
            return candidates.FirstOrDefault(w => w.Title.Length > 0) ?? candidates[0];
        }

        var match = candidates.FirstOrDefault(w => w.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            throw new InvalidOperationException($"no window with title containing '{title}'. have: "
                + string.Join(" | ", candidates.Select(w => w.Title)));
        }

        return match;
    }

    private static void BringToFront(TopWindow window)
    {
        SetForegroundWindow(window.Handle);
        ShowWindow(window.Handle, 9 /* SW_RESTORE */);
        Thread.Sleep(450);
    }

    private static void DumpWindows(string procName, string path)
    {
        var process = ResolveProcess(procName) ?? throw new InvalidOperationException("process not found: " + procName);
        var sb = new StringBuilder();
        foreach (var w in TopWindows(process.Id))
        {
            sb.AppendLine($"{(w.Visible ? "VISIBLE" : "hidden")} [{w.Title}] {w.Rect}");
        }

        File.WriteAllText(path, sb.ToString());
        Console.WriteLine("dumped " + path);
    }

    private static void DumpTree(string procName, string path)
    {
        var process = ResolveProcess(procName) ?? throw new InvalidOperationException("process not found: " + procName);
        var sb = new StringBuilder();
        foreach (var w in TopWindows(process.Id).Where(w => w.Visible))
        {
            sb.AppendLine($"WINDOW [{w.Title}] {w.Rect}");
            var root = AutomationElement.FromHandle(w.Handle);
            var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            foreach (AutomationElement el in all)
            {
                try
                {
                    var type = el.Current.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty);
                    var name = el.Current.Name ?? string.Empty;
                    var password = el.Current.IsPassword ? " [password]" : string.Empty;
                    var rect = el.Current.BoundingRectangle;
                    var patterns = el.Current.ControlType == ControlType.Edit
                        ? " patterns=" + string.Join(",", el.GetSupportedPatterns().Select(p => p.ProgrammaticName.Replace("PatternIdentifiers.", string.Empty)))
                        : string.Empty;
                    sb.AppendLine($"  {type,-12} name='{name}'{password}{patterns} rect={rect.Left:F0},{rect.Top:F0} {rect.Width:F0}x{rect.Height:F0}");
                }
                catch (Exception)
                {
                    // element vanished mid-dump
                }
            }
        }

        File.WriteAllText(path, sb.ToString());
        Console.WriteLine("dumped " + path);
    }

    private static void Shot(string procName, string path, string title)
    {
        var process = ResolveProcess(procName) ?? throw new InvalidOperationException("process not found: " + procName);
        var window = Pick(process.Id, title);
        BringToFront(window);
        GetWindowRect(window.Handle, out var rect);
        var width = Math.Max(1, rect.Width);
        var height = Math.Max(1, rect.Height);
        using var bitmap = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bitmap.Save(path, ImageFormat.Png);
        Console.WriteLine($"saved {path} {width}x{height} title='{window.Title}'");
    }

    private static AutomationElement? FindByName(AutomationElement root, string name)
    {
        // 优先点可交互控件；正文文本里也可能包含关键词（如提示语里的“开始守护”），
        // 只有找不到可点控件时才退而求其次。
        AutomationElement? fallback = null;
        var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        foreach (AutomationElement el in all)
        {
            try
            {
                if (!(el.Current.Name ?? string.Empty).Contains(name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var type = el.Current.ControlType;
                if (type == ControlType.Button
                    || type == ControlType.CheckBox
                    || type == ControlType.RadioButton
                    || type == ControlType.ListItem
                    || type == ControlType.MenuItem)
                {
                    return el;
                }

                fallback ??= el;
            }
            catch (Exception)
            {
                // skip
            }
        }

        return fallback;
    }

    private static List<AutomationElement> PasswordEdits(AutomationElement root)
    {
        var result = new List<AutomationElement>();
        var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        foreach (AutomationElement el in all)
        {
            try
            {
                if (el.Current.ControlType == ControlType.Edit && el.Current.IsPassword)
                {
                    result.Add(el);
                }
            }
            catch (Exception)
            {
                // skip
            }
        }

        return result;
    }

    private static void ClickAt(System.Windows.Point point)
    {
        Cursor.Position = new Point((int)point.X, (int)point.Y);
        Thread.Sleep(120);
        mouse_event(0x0002 /* LEFTDOWN */, 0, 0, 0, 0);
        Thread.Sleep(50);
        mouse_event(0x0004 /* LEFTUP */, 0, 0, 0, 0);
    }

    private static void Click(string procName, string title, string name)
    {
        var process = ResolveProcess(procName) ?? throw new InvalidOperationException("process not found: " + procName);
        var window = Pick(process.Id, title);
        BringToFront(window);
        var root = AutomationElement.FromHandle(window.Handle);
        var element = FindByName(root, name) ?? throw new InvalidOperationException("no element named like: " + name);
        var rect = element.Current.BoundingRectangle;
        if (rect.IsEmpty)
        {
            throw new InvalidOperationException("element has no clickable rect: " + name);
        }

        ClickAt(new System.Windows.Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2));
        Console.WriteLine($"clicked '{name}' at {rect.Left + rect.Width / 2:F0},{rect.Top + rect.Height / 2:F0}");
        Thread.Sleep(250);
    }

    private static void ClickAtFraction(string procName, string title, string name, double fx)
    {
        var process = ResolveProcess(procName) ?? throw new InvalidOperationException("process not found: " + procName);
        var window = Pick(process.Id, title);
        BringToFront(window);
        var root = AutomationElement.FromHandle(window.Handle);
        var element = FindByName(root, name) ?? throw new InvalidOperationException("no element named like: " + name);
        var rect = element.Current.BoundingRectangle;
        if (rect.IsEmpty)
        {
            throw new InvalidOperationException("element has no clickable rect: " + name);
        }

        ClickAt(new System.Windows.Point(rect.Left + (rect.Width * fx), rect.Top + (rect.Height / 2)));
        Console.WriteLine($"clicked '{name}' at fx={fx:F2} -> {rect.Left + (rect.Width * fx):F0},{rect.Top + (rect.Height / 2):F0}");
        Thread.Sleep(250);
    }

    private static void SetText(string procName, string title, int index, string value)
    {
        var process = ResolveProcess(procName) ?? throw new InvalidOperationException("process not found: " + procName);
        var window = Pick(process.Id, title);
        BringToFront(window);
        var root = AutomationElement.FromHandle(window.Handle);
        var edits = new List<AutomationElement>();
        var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        foreach (AutomationElement el in all)
        {
            try
            {
                if (el.Current.ControlType == ControlType.Edit && !el.Current.IsPassword)
                {
                    edits.Add(el);
                }
            }
            catch (Exception)
            {
                // skip
            }
        }

        if (edits.Count < index)
        {
            throw new InvalidOperationException($"only {edits.Count} text edits");
        }

        var element = edits[index - 1];
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
        {
            throw new InvalidOperationException("text box has no ValuePattern");
        }

        ((ValuePattern)pattern).SetValue(value);
        Console.WriteLine($"set text #{index}");
        Thread.Sleep(200);
    }

    private static void SetPassword(string procName, string title, int index, string value)
    {
        var process = ResolveProcess(procName) ?? throw new InvalidOperationException("process not found: " + procName);
        var window = Pick(process.Id, title);
        BringToFront(window);
        var root = AutomationElement.FromHandle(window.Handle);
        var edits = PasswordEdits(root);
        if (edits.Count < index)
        {
            throw new InvalidOperationException($"only {edits.Count} password edits");
        }

        var element = edits[index - 1];
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
        {
            throw new InvalidOperationException("password box has no ValuePattern");
        }

        ((ValuePattern)pattern).SetValue(value);
        Console.WriteLine($"set password #{index}");
        Thread.Sleep(200);
    }

    private static void ClickPassword(string procName, string title, int index)
    {
        var process = ResolveProcess(procName) ?? throw new InvalidOperationException("process not found: " + procName);
        var window = Pick(process.Id, title);
        BringToFront(window);
        var root = AutomationElement.FromHandle(window.Handle);
        var edits = PasswordEdits(root);
        if (edits.Count < index)
        {
            throw new InvalidOperationException($"only {edits.Count} password edits");
        }

        var rect = edits[index - 1].Current.BoundingRectangle;
        ClickAt(new System.Windows.Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2));
        Console.WriteLine($"clicked password #{index} at {rect.Left + rect.Width / 2:F0},{rect.Top + rect.Height / 2:F0}");
        Thread.Sleep(250);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rectangle rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
}
