using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Chengshi.App;

internal sealed class AppTray : IDisposable
{
    private readonly NotifyIcon _notify;
    private readonly MainWindow _window;
    private bool _balloonShown;

    public AppTray(MainWindow window)
    {
        _window = window;
        _notify = new NotifyIcon
        {
            Text = "澄时",
            Visible = true,
            Icon = LoadIcon(),
            ContextMenuStrip = BuildMenu(),
        };
        _notify.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                _window.ShowFromTray();
            }
        };
    }

    public void HintHidden()
    {
        if (_balloonShown)
        {
            return;
        }

        _balloonShown = true;
        _notify.ShowBalloonTip(2800, "澄时", "还在后台运行。点右下角台灯图标打开。退出请右键托盘图标。", ToolTipIcon.None);
    }

    public void Dispose()
    {
        _notify.Visible = false;
        _notify.Dispose();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("打开澄时");
        open.Click += (_, _) => _window.ShowFromTray();
        var exit = new ToolStripMenuItem("退出…");
        exit.Click += (_, _) => _window.RequestExit();
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);
        return menu;
    }

    private static Icon LoadIcon()
    {
        try
        {
            var exe = Path.Combine(AppContext.BaseDirectory, "Chengshi.App.exe");
            var fromExe = Icon.ExtractAssociatedIcon(exe);
            if (fromExe is not null)
            {
                return fromExe;
            }
        }
        catch (Exception)
        {
            // fall through
        }

        try
        {
            foreach (var name in new[] { "chengshi.ico", Path.Combine("Assets", "chengshi.ico") })
            {
                var path = Path.Combine(AppContext.BaseDirectory, name);
                if (File.Exists(path))
                {
                    return new Icon(path);
                }
            }
        }
        catch (Exception)
        {
            // fall through
        }

        return SystemIcons.Application;
    }
}
