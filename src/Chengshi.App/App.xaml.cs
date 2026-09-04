using System.Threading;
using System.Windows;
using Microsoft.Win32;
using Chengshi.Core;
using Chengshi.Engine;

namespace Chengshi.App;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _show;
    private AppTray? _tray;
    private CancellationTokenSource? _showLoop;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        // 从提权后的实例里安装/卸载守护服务，做完就退出，不进界面。
        if (e.Args.Any(a => a.Equals("--install-service", StringComparison.OrdinalIgnoreCase)))
        {
            if (ServiceControl.IsAdministrator())
            {
                ServiceControl.Install();
                System.Windows.MessageBox.Show("澄时守护服务已安装并启动。", "澄时");
            }
            else
            {
                System.Windows.MessageBox.Show("安装守护服务需要管理员权限。", "澄时");
            }

            Shutdown();
            return;
        }

        if (e.Args.Any(a => a.Equals("--uninstall-service", StringComparison.OrdinalIgnoreCase)))
        {
            if (ServiceControl.IsAdministrator())
            {
                ServiceControl.Uninstall();
                System.Windows.MessageBox.Show("澄时守护服务已卸载。", "澄时");
            }
            else
            {
                System.Windows.MessageBox.Show("卸载守护服务需要管理员权限。", "澄时");
            }

            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            FileLog.Error("app", "界面线程未处理异常。", args.Exception);
            System.Windows.MessageBox.Show(args.Exception.Message, "澄时");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            FileLog.Error("app", "未处理异常（进程可能退出）。", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            FileLog.Error("app", "未被观察的任务异常。", args.Exception);
            args.SetObserved();
        };

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _mutex = new Mutex(initiallyOwned: true, @"Local\Chengshi.App", out var created);
        _show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Chengshi.App.Show");
        if (!created)
        {
            _show.Set();
            Shutdown();
            return;
        }

        var host = ConnectBackend(out var startupHint);
        FileLog.Write("app", $"澄时已启动（{(host.IsRemote ? "守护服务模式" : "本机回退模式")}）。");
        var window = new MainWindow(host, startupHint);
        MainWindow = window;
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
        window.Topmost = true;
        window.Topmost = false;

        try
        {
            _tray = new AppTray(window);
            window.Tray = _tray;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("托盘图标没能打开：" + ex.Message, "澄时");
        }

        if (!window.IsFamilyConfigured)
        {
            try
            {
                var onboarding = new OnboardingWindow(host) { Owner = window };
                if (onboarding.ShowDialog() == true)
                {
                    window.ReloadAll();
                }
            }
            catch (Exception ex)
            {
                // 带堆栈落盘（数据目录只读时自动退回用户目录），现场问题才有的查。
                FileLog.Error("app", "引导设置打开失败。", ex);
                System.Windows.MessageBox.Show(
                    "引导设置没能打开：" + ex.Message
                    + "\n\n详细原因已记录到 %LOCALAPPDATA%\\Chengshi\\logs\\（或数据目录 logs\\），反馈问题时请附上。",
                    "澄时");
            }
        }

        // 工作站被解锁（时间到锁屏后家长登录进来）时：自动把窗口弹到前台，
        // 家长直接就能看到“时间到”和解锁按钮。
        // 不再向服务上报「已解锁」：解锁宽限期只由服务端验证家长密码成功后开启，
        // 孩子的进程伪造不了这条消息，也就压不住时间到后的强制锁屏。
        try
        {
            SystemEvents.SessionSwitch += (_, args) =>
            {
                if (args.Reason != SessionSwitchReason.SessionUnlock)
                {
                    return;
                }

                // 仅在“时间到”时自动把解锁界面弹到前台；正常守护中锁屏解锁不打扰家长。
                if (host.Snapshot.Phase == SessionPhase.TimeUp)
                {
                    window.Dispatcher.Invoke(() =>
                    {
                        window.ShowFromTray();
                        window.Activate();
                    });
                }
            };
        }
        catch
        {
            // 某些会话（服务/无消息泵）没有 SessionSwitch，忽略即可。
        }

        _showLoop = new CancellationTokenSource();
        _ = WaitForShowAsync(window, _showLoop.Token);

        var startInTray = e.Args.Any(a =>
            string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase)
            || string.Equals(a, "/tray", StringComparison.OrdinalIgnoreCase));
        if (startInTray && window.IsFamilyConfigured)
        {
            window.HideToTray(silent: true);
        }
    }

    /// <summary>
    /// 优先把守护交给系统服务（管理员权限、孩子杀不掉）；
    /// 服务没装/没起时回退到本机守护，界面照样能用。
    /// verifyServer：连接后核对管道对端确实是澄时服务进程，
    /// 防止本机其他进程抢先占住管道名冒充守护、钓家长密码。
    /// </summary>
    private static ISessionControl ConnectBackend(out string? startupHint)
    {
        try
        {
            startupHint = null;
            return SessionClient.Connect(TimeSpan.FromSeconds(2.5), verifyServer: true);
        }
        catch (Exception)
        {
            startupHint = "没连上守护服务：断网和防强杀不生效。安装并启动澄时服务后自动升级。";
            return new SessionHost();
        }
    }

    private async Task WaitForShowAsync(MainWindow window, CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_show is not null && _show.WaitOne(TimeSpan.FromMilliseconds(400)))
                {
                    Dispatcher.Invoke(window.ShowFromTray);
                }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        _showLoop?.Cancel();
        _tray?.Dispose();
        _show?.Dispose();
        _mutex?.Dispose();
    }
}
