using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Chengshi.Core;
using Chengshi.Engine;
using Chengshi.Ipc;

namespace Chengshi.App;

public partial class MainWindow : Window
{
    private readonly ISessionControl _host;
    private readonly DispatcherTimer _timer;
    private readonly List<string> _blocked = [];
    private readonly string? _startupHint;
    private readonly UsageLogStore _usageLog = new();
    private DateTime _weekUsageNextRead = DateTime.MinValue;
    private int _weekdayMinutes = 60;
    private int _weekendMinutes = 120;
    private Dictionary<DayOfWeek, int>? _schedule;
    private List<DayLimitRow> _dayRows = [];
    private readonly DayOfWeek[] _weekOrder =
    {
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    };
    private bool _parentUnlocked;
    private string _recoveryEmail = string.Empty;
    private bool _exitAllowed;
    private bool _ready;
    private bool _refreshing;
    private bool _dashboardAnimated;
    private DateTime _lastBreakTick = DateTime.Now;
    private TimeSpan _breakAccum;
    private DateTime _lastBreakShown = DateTime.MinValue;
    private int _breakReminderMinutes;

    internal AppTray? Tray { get; set; }

    public bool IsFamilyConfigured => _host.IsConfigured;

    public MainWindow(ISessionControl host, string? startupHint = null)
    {
        InitializeComponent();
        AppIcon.Apply(this);
        _host = host;
        _startupHint = startupHint;
        ReloadDesks(_host.Family?.DeskId);
        LoadDurationFromFamily();
        LoadScheduleFromFamily();
        RefreshParentForm();

        _host.ProcessBlocked += OnBlocked;
        _host.ConnectionChanged += OnConnectionChanged;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
        _timer.Start();
        _lastBreakTick = DateTime.Now;
        Closing += OnClosing;
        StateChanged += OnStateChanged;
        Closed += (_, _) =>
        {
            _timer.Stop();
            _host.ProcessBlocked -= OnBlocked;
            _host.ConnectionChanged -= OnConnectionChanged;
            _host.Dispose();
            System.Windows.Application.Current?.Shutdown();
        };

        if (_host.IsConfigured && _host.Family?.GuardOnLaunch == true)
        {
            try
            {
                _host.StartGuard();
            }
            catch (Exception ex)
            {
                ParentHint.Text = ex.Message;
            }
        }

        _ready = true;
        if (_host.Family is { } family)
        {
            StartupRegistration.Apply(family.StartWithWindows);
        }

        ParentHint.Text = EngineHint(_startupHint);
        Render(_host.Snapshot);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    internal void HideToTray(bool silent = false)
    {
        WindowState = WindowState.Normal;
        ShowInTaskbar = false;
        Hide();
        if (!silent)
        {
            Tray?.HintHidden();
        }
    }

    internal void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    internal void RequestExit()
    {
        if (!_host.IsConfigured)
        {
            _exitAllowed = true;
            Close();
            return;
        }

        ShowFromTray();
        if (!TryParentPin("退出后孩子就能打开任意软件。确定退出澄时吗？", out var pin))
        {
            return;
        }

        if (_host.Snapshot.Parental)
        {
            _host.Stop(pin);
        }

        _exitAllowed = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exitAllowed)
        {
            return;
        }

        if (Tray is null)
        {
            return;
        }

        e.Cancel = true;
        HideToTray();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && !_exitAllowed)
        {
            HideToTray();
        }
    }

    private void OnConnectionChanged(bool connected)
    {
        Dispatcher.Invoke(() =>
        {
            if (connected)
            {
                ParentHint.Text = EngineHint(null);
                DashboardHintText.Text = EngineHint(null);
            }
            else if (ChildRoot.Visibility != Visibility.Visible)
            {
                ParentHint.Text = "守护服务连接中断，正在重连…（已开的守护仍由服务执行）";
                DashboardHintText.Text = "守护服务连接中断，正在重连…（已开的守护仍由服务执行）";
            }
        });
    }

    private string EngineHint(string? extra)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(extra))
        {
            parts.Add(extra);
        }

        if (!_host.IsRemote && !string.IsNullOrWhiteSpace(_host.EtwHint))
        {
            parts.Add(_host.EtwHint);
        }

        if (!string.IsNullOrWhiteSpace(_host.GuardHint))
        {
            parts.Add(_host.GuardHint);
        }

        return string.Join(" ", parts);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var snapshot = _host.Tick();
        AccumulateBreak(snapshot);
        Render(snapshot);
    }

    /// <summary>守护中累计连续用机时长，到「护眼休息」间隔就弹一次温柔提醒（不强制）。</summary>
    private void AccumulateBreak(SessionSnapshot snapshot)
    {
        var now = DateTime.Now;
        var dt = now - _lastBreakTick;
        _lastBreakTick = now;

        if (!snapshot.IsGuarding || _breakReminderMinutes <= 0)
        {
            _breakAccum = TimeSpan.Zero;
            return;
        }

        _breakAccum += dt;
        var interval = TimeSpan.FromMinutes(_breakReminderMinutes);
        if (_breakAccum >= interval && (now - _lastBreakShown) >= interval)
        {
            ShowBreakOverlay();
            _breakAccum = TimeSpan.Zero;
            _lastBreakShown = now;
        }
    }

    private void ShowBreakOverlay()
    {
        if (BreakOverlay.Visibility == Visibility.Visible)
        {
            return;
        }

        BreakOverlay.Visibility = Visibility.Visible;
        Fade(BreakOverlay, 0, 1, null);
    }

    private void BreakDismiss_Click(object sender, RoutedEventArgs e)
    {
        BreakOverlay.Visibility = Visibility.Collapsed;
        _breakAccum = TimeSpan.Zero;
        _lastBreakShown = DateTime.Now;
    }

    private void BreakReminder_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _refreshing || sender is not RadioButton { Tag: string tag })
        {
            return;
        }

        _breakReminderMinutes = int.TryParse(tag, out var minutes) ? minutes : 0;
        _breakAccum = TimeSpan.Zero;
        PersistFamilyIfConfigured();
    }

    private void Reward_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParentPin("输入家长密码，给孩子奖励 15 分钟屏幕时间。", out var pin))
        {
            return;
        }

        try
        {
            var result = _host.GrantExtra(pin, 15);
            DashboardHintText.Text = result.Ok ? "已奖励 15 分钟，孩子继续玩吧。" : result.Hint;
        }
        catch (Exception ex) when (ex is InvalidOperationException or RemoteFaultException)
        {
            DashboardHintText.Text = ex.Message;
        }
    }

    internal void ReloadAll()
    {
        ReloadDesks(_host.Family?.DeskId);
        LoadDurationFromFamily();
        LoadScheduleFromFamily();
        RefreshParentForm();
        RefreshServiceStatus();
        Render(_host.Snapshot);
    }

    private void DeskList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        RefreshParentForm();
        PersistFamilyIfConfigured();
    }

    private void Duration_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag })
        {
            return;
        }

        if (tag == "custom")
        {
            CustomMinutesPanel.Visibility = Visibility.Visible;
            if (!_ready)
            {
                return;
            }

            CustomMinutesBox.Focus();
            CustomMinutesBox.SelectAll();
            RefreshDurationTexts();
            return;
        }

        if (int.TryParse(tag, out var minutes))
        {
            ActiveMinutes = minutes;
            CustomMinutesPanel.Visibility = Visibility.Collapsed;
            if (!_ready)
            {
                return;
            }

            RefreshDurationTexts();
            PersistFamilyIfConfigured();
        }
    }

    private void CustomMinutes_LostFocus(object sender, RoutedEventArgs e) => CommitCustomMinutes();

    private void CustomMinutes_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            CommitCustomMinutes();
            e.Handled = true;
        }
    }

    private void CommitCustomMinutes()
    {
        if (!int.TryParse(CustomMinutesBox.Text.Trim(), out var minutes))
        {
            RefreshDurationTexts();
            return;
        }

        minutes = Math.Clamp(minutes, 5, 600);
        if (CustomMinutesBox.Text.Trim() != minutes.ToString())
        {
            CustomMinutesBox.Text = minutes.ToString();
        }

        ActiveMinutes = minutes;
        RefreshDurationTexts();
        PersistFamilyIfConfigured();
    }

    private void ReloadDesks(string? selectId = null)
    {
        var id = selectId ?? (DeskList.SelectedItem as Desk)?.Id ?? _host.Family?.DeskId;
        var desks = _host.Desks
            .Where(d => d.Id is not BuiltinDesks.SpikeId and not BuiltinDesks.LockdownId)
            .ToList();
        DeskList.ItemsSource = desks;
        DeskList.SelectedItem = desks.FirstOrDefault(d => d.Id == id) ?? desks.FirstOrDefault();
        RefreshParentForm();
    }

    private bool WeekendTabActive => DayTabWeekend.IsChecked == true;

    private int ActiveMinutes
    {
        get => WeekendTabActive ? _weekendMinutes : _weekdayMinutes;
        set
        {
            if (WeekendTabActive)
            {
                _weekendMinutes = value;
            }
            else
            {
                _weekdayMinutes = value;
            }
        }
    }

    /// <summary>从已保存的家长设置恢复两档时长（旧配置只有统一档时按原值平移）。</summary>
    private void LoadDurationFromFamily()
    {
        var family = _host.Family;
        _weekdayMinutes = family?.WeekdayMinutes ?? family?.DailyMinutes ?? 60;
        _weekendMinutes = family?.WeekendMinutes ?? family?.DailyMinutes ?? 120;
        ApplyDurationEditor();
    }

    private void ApplyDurationEditor()
    {
        var minutes = ActiveMinutes;
        var preset = minutes is 30 or 60 or 90 or 120;
        Dur30.IsChecked = minutes == 30;
        Dur60.IsChecked = minutes == 60;
        Dur90.IsChecked = minutes == 90;
        Dur120.IsChecked = minutes == 120;
        DurCustom.IsChecked = !preset;
        CustomMinutesPanel.Visibility = preset ? Visibility.Collapsed : Visibility.Visible;
        if (!preset)
        {
            CustomMinutesBox.Text = minutes.ToString();
        }

        RefreshDurationTexts();
    }

    private void RefreshDurationTexts()
    {
        DailyMinutesText.Text = DescribeMinutes(ActiveMinutes);
        OtherDaysText.Text = WeekendTabActive
            ? $"周中每天 {DescribeMinutes(_weekdayMinutes)}"
            : $"周末每天 {DescribeMinutes(_weekendMinutes)}";
    }

    private static string DescribeMinutes(int minutes) => minutes switch
    {
        30 => "30 分钟",
        60 => "1 小时",
        90 => "90 分钟",
        120 => "2 小时",
        _ => $"{minutes} 分钟",
    };

    private void DayType_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || _refreshing)
        {
            return;
        }

        ApplyDurationEditor();
    }

    /// <summary>从已保存设置恢复「按星期排」的单独时长；没有则为 null，回落到基础时长。</summary>
    private void LoadScheduleFromFamily()
    {
        _schedule = _host.Family?.Schedule is { } dict && dict.Count > 0
            ? new Dictionary<DayOfWeek, int>(dict)
            : null;
        RefreshScheduleEditor();
    }

    /// <summary>把 7 天卡片重画一遍：有单独设置的显示自定义值，否则跟随基础时长。</summary>
    private void RefreshScheduleEditor()
    {
        _dayRows = _weekOrder.Select(day =>
        {
            if (_schedule is not null && _schedule.TryGetValue(day, out var custom))
            {
                return new DayLimitRow(day, DayLabel(day), custom, true);
            }

            var baseMin = day is DayOfWeek.Saturday or DayOfWeek.Sunday
                ? _weekendMinutes
                : _weekdayMinutes;
            return new DayLimitRow(day, DayLabel(day), baseMin, false);
        }).ToList();
        DayScheduleList.ItemsSource = _dayRows;
    }

    private void DayMinutes_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
        {
            CommitDayMinutes(box);
        }
    }

    private void DayMinutes_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && sender is TextBox box)
        {
            CommitDayMinutes(box);
            e.Handled = true;
        }
    }

    private void CommitDayMinutes(TextBox box)
    {
        if (box.DataContext is not DayLimitRow row)
        {
            return;
        }

        if (!int.TryParse(box.Text.Trim(), out var minutes))
        {
            RefreshScheduleEditor();
            return;
        }

        minutes = Math.Clamp(minutes, 5, 600);
        if (box.Text.Trim() != minutes.ToString())
        {
            box.Text = minutes.ToString();
        }

        var baseMin = row.Day is DayOfWeek.Saturday or DayOfWeek.Sunday
            ? _weekendMinutes
            : _weekdayMinutes;
        _schedule ??= new Dictionary<DayOfWeek, int>();
        if (minutes == baseMin)
        {
            _schedule.Remove(row.Day);
        }
        else
        {
            _schedule[row.Day] = minutes;
        }

        if (_schedule.Count == 0)
        {
            _schedule = null;
        }

        if (!EnsureParentUnlocked())
        {
            LoadScheduleFromFamily();
            return;
        }

        PersistSchedule();
        RefreshScheduleEditor();
    }

    private void PersistSchedule()
    {
        if (_host.Family is not { } family)
        {
            return;
        }

        try
        {
            _host.SaveFamily(family with { Schedule = _schedule });
            if (_host.IsRemote)
            {
                _parentUnlocked = true;
            }
        }
        catch (Exception ex) when (ex is RemoteFaultException or UnauthorizedAccessException or IOException)
        {
            ParentHint.Text = "周计划没有保存：" + ex.Message;
        }
    }

    private void RefreshParentForm()
    {
        var configured = _host.IsConfigured;
        FirstRunPinPanel.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
        ConfiguredPinPanel.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
        ParentTitle.Text = configured ? "家长设置" : "给孩子设屏幕时间和软件";
        ParentLead.Text = configured
            ? "改时长或软件后点开始守护。孩子在守护画面里改不了。"
            : "开始守护后，不在名单里的软件会被关掉；今天的时间用完就只剩系统桌面。";
        RefreshDurationTexts();

        if (DeskList.SelectedItem is not Desk desk)
        {
            return;
        }

        var names = desk.Apps
            .Select(a => a.DisplayName)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var empty = names.Length == 0;
        AppChips.ItemsSource = desk.Apps;
        AppChips.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyAppsText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        GuardButton.Content = "开始守护";
        GuardButton.IsEnabled = !empty;
        RecoveryCodeText.Text = _host.IsRemote
            ? "找回码已由守护服务保管（不再显示，请用首次设置时抄下的那枚）"
            : _host.Family?.RecoveryCode ?? "进入设置后自动生成";
        _recoveryEmail = _host.Family?.RecoveryEmail ?? string.Empty;
        if (RecoveryEmailBox.Text != _recoveryEmail)
        {
            RecoveryEmailBox.Text = _recoveryEmail;
        }
        var startWithWindows = _host.Family?.StartWithWindows ?? true;
        if (StartWithWindowsBox.IsChecked != startWithWindows)
        {
            StartWithWindowsBox.IsChecked = startWithWindows;
        }

        _refreshing = true;
        try
        {
            AllowedSitesChips.ItemsSource = desk.AllowedSiteList.ToList();
            BlockedSitesChips.ItemsSource = desk.BlockedSiteList.ToList();
            SiteAllowEmpty.Visibility = desk.AllowedSiteList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            CatVideo.IsChecked = desk.BlockCategoryList.Contains("video", StringComparer.OrdinalIgnoreCase);
            CatGames.IsChecked = desk.BlockCategoryList.Contains("games", StringComparer.OrdinalIgnoreCase);
            CatAdult.IsChecked = desk.BlockCategoryList.Contains("adult", StringComparer.OrdinalIgnoreCase);
            SitesHintText.Text = desk.AllowedSiteList.Count > 0
                ? "已开启白名单模式：浏览器只能打开上面这些网站。"
                : "浏览器（Chrome / Edge）里，勾选的类别和禁止的网站会被拦掉，写作业时还可整机断网。";
            var bedtime = _host.Family?.BedtimeEnabled ?? true;
            if (BedtimeBox.IsChecked != bedtime)
            {
                BedtimeBox.IsChecked = bedtime;
            }

            var guardOnLaunch = _host.Family?.GuardOnLaunch ?? true;
            if (GuardOnLaunchBox.IsChecked != guardOnLaunch)
            {
                GuardOnLaunchBox.IsChecked = guardOnLaunch;
            }

            _breakReminderMinutes = _host.Family?.BreakReminderMinutes ?? 0;
            BreakOff.IsChecked = _breakReminderMinutes == 0;
            Break30.IsChecked = _breakReminderMinutes == 30;
            Break45.IsChecked = _breakReminderMinutes == 45;
            Break60.IsChecked = _breakReminderMinutes == 60;
        }
        finally
        {
            _refreshing = false;
        }

        RefreshAppLimits();
        RefreshScheduleEditor();

        WhatHappensText.Text = empty
            ? "先加一款软件，否则孩子几乎什么都开不了。"
            : "用完后，名单外的软件会被关掉，直到明天或输入家长密码。";
    }

    private void StartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        PersistFamilyIfConfigured();
    }

    private void Bedtime_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _refreshing)
        {
            return;
        }

        PersistFamilyIfConfigured();
    }

    private void GuardOnLaunch_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _refreshing)
        {
            return;
        }

        PersistFamilyIfConfigured();
    }

    /// <summary>
    /// 改配置的门槛：连着守护服务时，先验证过家长密码才会被服务接受。
    /// 没有服务或还没设密码时不拦（服务端对未配置状态放行首次设置）。
    /// </summary>
    private bool EnsureParentUnlocked()
    {
        if (!_host.IsRemote || _parentUnlocked || !_host.IsConfigured)
        {
            return true;
        }

        if (!TryParentPin("修改每天时长、软件名单前，请输入家长密码。", out var pin))
        {
            return false;
        }

        try
        {
            _parentUnlocked = _host.VerifyParentPin(pin);
        }
        catch (RemoteFaultException)
        {
            _parentUnlocked = false;
        }

        if (!_parentUnlocked)
        {
            ParentHint.Text = "密码不对，设置没有解锁。";
        }

        return _parentUnlocked;
    }

    private void PersistFamilyIfConfigured()
    {
        if (_host.Family is not { } family || DeskList.SelectedItem is not Desk desk)
        {
            return;
        }

        if (!EnsureParentUnlocked())
        {
            RefreshDurationTexts();
            LoadDurationFromFamily();
            return;
        }

        var startWithWindows = StartWithWindowsBox.IsChecked == true;
        try
        {
            _host.SaveFamily(family with
            {
                DailyMinutes = ActiveMinutes,
                WeekdayMinutes = _weekdayMinutes,
                WeekendMinutes = _weekendMinutes,
                DeskId = desk.Id,
                StartWithWindows = startWithWindows,
                GuardOnLaunch = GuardOnLaunchBox.IsChecked == true,
                BedtimeEnabled = BedtimeBox.IsChecked == true,
                BreakReminderMinutes = _breakReminderMinutes,
                Schedule = _schedule,
                RecoveryEmail = string.IsNullOrWhiteSpace(_recoveryEmail) ? null : _recoveryEmail,
            });
            if (_host.IsRemote)
            {
                // 解锁只对当前连接有效，保存成功即视为本窗口已授权。
                _parentUnlocked = true;
            }
        }
        catch (Exception ex) when (ex is RemoteFaultException or UnauthorizedAccessException or IOException)
        {
            ParentHint.Text = "设置没有保存：" + ex.Message;
            LoadDurationFromFamily();
            return;
        }

        StartupRegistration.Apply(startWithWindows);
    }

    private void SaveRecoveryEmail_Click(object sender, RoutedEventArgs e)
    {
        var email = RecoveryEmailBox.Text.Trim();
        if (email.Length > 0 && (!email.Contains('@') || !email.Contains('.')))
        {
            MailHint.Text = "邮箱格式看起来不太对，请检查。";
            return;
        }

        _recoveryEmail = email;
        if (!EnsureParentUnlocked())
        {
            MailHint.Text = "请先在弹出的密码框里验证家长密码。";
            LoadDurationFromFamily();
            return;
        }

        PersistFamilyIfConfigured();
        MailHint.Text = string.IsNullOrWhiteSpace(email)
            ? "已清除备用邮箱。"
            : $"已保存备用邮箱：{email}（忘记密码时可用它收验证码）。";
    }

    private void RefreshServiceStatus()
    {
        if (ServiceStatusText is null)
        {
            return;
        }

        if (ServiceControl.IsInstalled())
        {
            ServiceStatusText.Text = ServiceControl.IsRunning()
                ? "守护服务已安装并在运行：开机自动守护已生效，进程孩子杀不掉。"
                : "守护服务已安装但未运行：点「重新安装/启动」会重启它。";
            InstallServiceButton.Content = "重新安装/启动";
            UninstallServiceButton.Visibility = Visibility.Visible;
        }
        else
        {
            ServiceStatusText.Text = "尚未安装守护服务：现在只在软件运行时守护，重启电脑后不自动生效。建议点「安装守护服务」。";
            InstallServiceButton.Content = "安装守护服务";
            UninstallServiceButton.Visibility = Visibility.Collapsed;
        }
    }

    private void InstallService_Click(object sender, RoutedEventArgs e)
    {
        if (!ServiceControl.IsAdministrator())
        {
            try
            {
                ServiceControl.RunElevated("--install-service");
                ScheduleServiceStatusRefresh();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("提权失败：" + ex.Message, "澄时");
            }

            return;
        }

        try
        {
            ServiceControl.Install();
            RefreshServiceStatus();
            System.Windows.MessageBox.Show("守护服务已安装并启动。", "澄时");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("安装失败：" + ex.Message, "澄时");
        }
    }

    private void UninstallService_Click(object sender, RoutedEventArgs e)
    {
        if (!ServiceControl.IsAdministrator())
        {
            try
            {
                ServiceControl.RunElevated("--uninstall-service");
                ScheduleServiceStatusRefresh();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("提权失败：" + ex.Message, "澄时");
            }

            return;
        }

        try
        {
            ServiceControl.Uninstall();
            RefreshServiceStatus();
            System.Windows.MessageBox.Show("守护服务已卸载。", "澄时");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("卸载失败：" + ex.Message, "澄时");
        }
    }

    private void ScheduleServiceStatusRefresh()
    {
        _ = Task.Delay(3000).ContinueWith(_ =>
        {
            try
            {
                Dispatcher.Invoke(RefreshServiceStatus);
            }
            catch
            {
                // 窗口已关闭等情况忽略。
            }
        }, TaskScheduler.Default);
    }

    private void MailPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
        {
            return;
        }

        var preset = SmtpConfig.Preset(tag);
        if (preset is null)
        {
            return;
        }

        SmtpHostBox.Text = preset.Host;
        SmtpPortBox.Text = preset.Port.ToString();
        SmtpSslBox.IsChecked = preset.UseSsl;        MailHint.Text = $"已填入 {tag.ToUpperInvariant()} 的服务器与端口，请补全邮箱账号和授权码。";
    }

    private async void SaveMail_Click(object sender, RoutedEventArgs e)
    {
        var host = SmtpHostBox.Text.Trim();
        var user = SmtpUserBox.Text.Trim();
        var pass = SmtpPassBox.Password;
        int.TryParse(SmtpPortBox.Text.Trim(), out var port);

        if (string.IsNullOrWhiteSpace(host) && string.IsNullOrWhiteSpace(user) && string.IsNullOrWhiteSpace(pass))
        {
            // 三项全空 = 清除邮件设置。
            try
            {
                await _host.SaveSmtpAsync(new SmtpConfig(string.Empty, 0, false, string.Empty, string.Empty));
                MailHint.Text = "已清除邮件设置。邮箱找回密码在配置好 SMTP 之前不可用。";
            }
            catch (Exception ex) when (ex is RemoteFaultException or InvalidOperationException)
            {
                MailHint.Text = "清除失败：" + ex.Message;
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user))
        {
            MailHint.Text = "服务器和账号要一起填；授权码留空表示沿用已保存的。";
            return;
        }

        try
        {
            // 授权码只发往守护服务（加密落盘），界面不保存、不再读取。
            await _host.SaveSmtpAsync(new SmtpConfig(host, port, SmtpSslBox.IsChecked == true, user, pass));
            MailHint.Text = "已保存邮件设置（授权码加密存放）。之后找回密码会真实发信到备用邮箱。";
        }
        catch (Exception ex) when (ex is RemoteFaultException or InvalidOperationException)
        {
            MailHint.Text = "保存失败：" + ex.Message;
        }
    }

    private void AddAllowedSite_Click(object sender, RoutedEventArgs e)
    {
        if (DeskList.SelectedItem is not Desk desk)
        {
            return;
        }

        var normalized = Desk.NormalizeDomains([SiteAllowBox.Text]);
        if (normalized.Count == 0)
        {
            ParentHint.Text = "网址格式不对，例如 ke.qq.com。";
            return;
        }

        SiteAllowBox.Text = string.Empty;
        ParentHint.Text = string.Empty;
        SaveDesk(desk.WithAllowedSites(desk.AllowedSiteList.Append(normalized[0])));
    }

    private void RemoveAllowedSite_Click(object sender, RoutedEventArgs e)
    {
        if (DeskList.SelectedItem is not Desk desk)
        {
            return;
        }

        if ((sender as FrameworkElement)?.Tag is not string site)
        {
            return;
        }

        SaveDesk(desk.WithAllowedSites(desk.AllowedSiteList
            .Where(s => !string.Equals(s, site, StringComparison.OrdinalIgnoreCase))));
    }

    private void AddBlockedSite_Click(object sender, RoutedEventArgs e)
    {
        if (DeskList.SelectedItem is not Desk desk)
        {
            return;
        }

        var normalized = Desk.NormalizeDomains([SiteBlockBox.Text]);
        if (normalized.Count == 0)
        {
            ParentHint.Text = "网址格式不对，例如 youku.com。";
            return;
        }

        SiteBlockBox.Text = string.Empty;
        ParentHint.Text = string.Empty;
        SaveDesk(desk.WithBlockedSites(desk.BlockedSiteList.Append(normalized[0])));
    }

    private void RemoveBlockedSite_Click(object sender, RoutedEventArgs e)
    {
        if (DeskList.SelectedItem is not Desk desk)
        {
            return;
        }

        if ((sender as FrameworkElement)?.Tag is not string site)
        {
            return;
        }

        SaveDesk(desk.WithBlockedSites(desk.BlockedSiteList
            .Where(s => !string.Equals(s, site, StringComparison.OrdinalIgnoreCase))));
    }

    private void Category_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _refreshing || DeskList.SelectedItem is not Desk desk)
        {
            return;
        }

        var categories = new List<string>();
        if (CatVideo.IsChecked == true)
        {
            categories.Add("video");
        }

        if (CatGames.IsChecked == true)
        {
            categories.Add("games");
        }

        if (CatAdult.IsChecked == true)
        {
            categories.Add("adult");
        }

        SaveDesk(desk.WithBlockCategories(categories));
    }

    private void ChangePin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (PinHasher.NormalizePin(PinNewBox.Password) != PinHasher.NormalizePin(PinNewConfirmBox.Password))
            {
                PinChangeHint.Text = "两次新密码不一致。";
                return;
            }

            var saved = _host.ChangePin(PinOldBox.Password, PinNewBox.Password);
            _parentUnlocked = true;
            PinOldBox.Clear();
            PinNewBox.Clear();
            PinNewConfirmBox.Clear();
            // 改密码不动找回码；远程模式下服务不回传找回码，提醒家长沿用旧的那枚。
            RecoveryCodeText.Text = saved.RecoveryCode ?? "找回码不变（沿用首次设置时抄下的那枚）";
            PinChangeHint.Text = "密码已改。";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or RemoteFaultException)
        {
            PinChangeHint.Text = ex.Message;
        }
    }

    private void AddApps_Click(object sender, RoutedEventArgs e)
    {
        if (DeskList.SelectedItem is not Desk desk)
        {
            return;
        }

        var picker = new AppsWindow(desk.Apps) { Owner = this };
        if (picker.ShowDialog() == true && picker.Result is not null)
        {
            SaveDesk(desk.WithApps(picker.Result));
        }
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
        {
            return;
        }

        var parts = tag.Split('|');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var minutes))
        {
            return;
        }

        var desk = _host.Desks.FirstOrDefault(d =>
            string.Equals(d.Id, parts[0], StringComparison.OrdinalIgnoreCase));
        if (desk is null)
        {
            return;
        }

        DeskList.SelectedItem = desk;
        SetMainDuration(minutes);
        PersistFamilyIfConfigured();
    }

    private void SetMainDuration(int minutes)
    {
        switch (minutes)
        {
            case 30:
                Dur30.IsChecked = true;
                break;
            case 60:
                Dur60.IsChecked = true;
                break;
            case 90:
                Dur90.IsChecked = true;
                break;
            case 120:
                Dur120.IsChecked = true;
                break;
            default:
                DurCustom.IsChecked = true;
                CustomMinutesBox.Text = minutes.ToString();
                CommitCustomMinutes();
                break;
        }
    }

    private void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if (DeskList.SelectedItem is not Desk desk)
        {
            return;
        }

        if ((sender as FrameworkElement)?.Tag is not AllowedApp app)
        {
            return;
        }

        SaveDesk(desk.WithApps(desk.Apps.Where(a => a.Key != app.Key)));
    }

    private void SaveDesk(Desk desk)
    {
        if (!EnsureParentUnlocked())
        {
            return;
        }

        try
        {
            var saved = _host.SaveDesk(desk);
            ReloadDesks(saved.Id);
        }
        catch (Exception ex) when (ex is RemoteFaultException or UnauthorizedAccessException or IOException)
        {
            ParentHint.Text = "书桌没有保存：" + ex.Message;
        }

        PersistFamilyIfConfigured();
    }

    private void Spike_Click(object sender, RoutedEventArgs e)
    {
        if (_host.IsGuarding)
        {
            SpikeHintText.Text = "正在守护孩子，不能同时试拦截。先找家长暂停。";
            return;
        }

        try
        {
            var result = _host.Start(BuiltinDesks.SpikeId, TimeSpan.FromMinutes(1), pinned: false, pin: null);
            SpikeHintText.Text = EngineHint(null);
            Render(result.Snapshot, "试拦截：只留计算器。打开记事本应被关掉。");
        }
        catch (Exception ex) when (ex is ArgumentException or RemoteFaultException)
        {
            SpikeHintText.Text = ex.Message;
        }
    }

    private void Guard_Click(object sender, RoutedEventArgs e)
    {
        if (DeskList.SelectedItem is not Desk desk)
        {
            return;
        }

        if (desk.Apps.Count == 0)
        {
            ParentHint.Text = "先添加至少一款允许使用的软件。";
            return;
        }

        try
        {
            if (!_host.IsConfigured)
            {
                var pin = PinCreateBox.Password;
                var confirm = PinConfirmBox.Password;
                if (PinHasher.NormalizePin(pin) != PinHasher.NormalizePin(confirm))
                {
                    ParentHint.Text = "两次密码不一致。";
                    return;
                }

                var family = _host.SaveFamily(FamilySettings.Create(
                    pin, ActiveMinutes, desk.Id,
                    WeekdayMinutes: _weekdayMinutes, WeekendMinutes: _weekendMinutes) with
                {
                    StartWithWindows = StartWithWindowsBox.IsChecked == true,
                    Schedule = _schedule,
                });
                if (_host.IsRemote && _host.VerifyParentPin(pin))
                {
                    // 刚设置的密码立刻为这条连接解锁，后续调整不再重复询问。
                    _parentUnlocked = true;
                }

                StartupRegistration.Apply(family.StartWithWindows);
                System.Windows.MessageBox.Show(
                    $"家长密码已保存。找回码是：\n\n{family.RecoveryCode}\n\n请马上抄下来。忘记密码时要用，澄时不会再通过短信找回。",
                    "澄时");
            }
            else
            {
                PersistFamilyIfConfigured();
            }

            if (_host.Snapshot.Phase is not SessionPhase.Idle && !_host.Snapshot.Parental)
            {
                _host.Stop(null);
            }

            var result = _host.StartGuard();
            ParentHint.Text = EngineHint(null);
            Render(result.Snapshot, ChildHintFor(result.Snapshot));
        }
        catch (ArgumentException ex)
        {
            ParentHint.Text = ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            ParentHint.Text = ex.Message;
        }
        catch (RemoteFaultException ex)
        {
            ParentHint.Text = ex.Message;
        }
    }

    private void AskParent_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParentPin("输入家长密码后可以改每天时长、允许的软件，或暂时停下守护。", out var pin))
        {
            return;
        }

        if (_host.IsRemote)
        {
            try
            {
                _parentUnlocked = _host.VerifyParentPin(pin);
            }
            catch (RemoteFaultException)
            {
                _parentUnlocked = false;
            }
        }

        var result = _host.Stop(pin);
        ReloadDesks(_host.Family?.DeskId);
        LoadDurationFromFamily();
        RefreshParentForm();
        NavDashboard.IsChecked = true;
        Render(result.Snapshot);
    }

    private void AskMore_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ExtendTimeWindow(_host) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Granted)
        {
            Render(_host.Snapshot, "家长批了加时，继续吧。");
        }
    }

    private bool TryParentPin(string prompt, out string pin)
    {
        pin = string.Empty;
        if (!_host.IsConfigured)
        {
            return false;
        }

        var dialog = new PinWindow(prompt, _host) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return false;
        }

        pin = dialog.Pin;
        return true;
    }

    private void OnBlocked(BlockedMessage blocked)
    {
        Dispatcher.Invoke(() =>
        {
            var label = System.IO.Path.GetFileNameWithoutExtension(blocked.FileName);
            _blocked.Insert(0, label);
            if (_blocked.Count > 8)
            {
                _blocked.RemoveAt(_blocked.Count - 1);
            }

            var view = _blocked.ToList();
            BlockedList.ItemsSource = null;
            BlockedList.ItemsSource = view;
            DashboardBlockedList.ItemsSource = null;
            DashboardBlockedList.ItemsSource = view;
            DashboardBlockedEmpty.Visibility = _blocked.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        });
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true } nav)
        {
            return;
        }

        // XAML 加载期间 IsChecked 会先触发一次，此时页面还没建完。
        if (DashboardPage is null || SettingsPage is null || HelpPage is null)
        {
            return;
        }

        DashboardPage.Visibility = ReferenceEquals(nav, NavDashboard) ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = ReferenceEquals(nav, NavSettings) ? Visibility.Visible : Visibility.Collapsed;
        HelpPage.Visibility = ReferenceEquals(nav, NavHelp) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DashboardToSettings_Click(object sender, RoutedEventArgs e) => NavSettings.IsChecked = true;

    private void DashboardGuard_Click(object sender, RoutedEventArgs e)
    {
        if (!_host.IsConfigured)
        {
            NavSettings.IsChecked = true;
            ParentHint.Text = "先在这里设好家长密码和允许的软件，再回来开始守护。";
            return;
        }

        Guard_Click(sender, e);
    }

    private void UpdateEngineStatus()
    {
        var lines = new List<string>
        {
            _host.IsRemote
                ? "守护服务已连接：以系统权限执行，孩子关不掉。"
                : "本机守护模式：断网和防强杀没启用，安装并启动澄时服务后自动升级。",
        };
        if (!string.IsNullOrWhiteSpace(_host.EtwHint))
        {
            lines.Add(_host.EtwHint);
        }

        if (!string.IsNullOrWhiteSpace(_host.GuardHint))
        {
            lines.Add(_host.GuardHint);
        }

        var text = string.Join("\n", lines);
        SideEngineDetail.Text = text;
        EngineStatusText.Text = text;
        HelpEngineText.Text = text;
        SideEngineText.Text = _host.IsRemote ? "守护服务已连接" : "本机守护";
        EngineDot.Fill = _host.IsRemote
            ? (Brush)FindResource("AccentBrush")
            : new SolidColorBrush(Color.FromRgb(0xA3, 0x56, 0x2E));
    }

    private void UpdateStatusPill(SessionSnapshot snapshot)
    {
        if (snapshot.IsGuarding)
        {
            TitlePillText.Text = "守护中";
            TitlePillText.Foreground = (Brush)FindResource("AccentBrush");
            TitlePillDot.Fill = (Brush)FindResource("AccentBrush");
            TitlePillBorder.Background = (Brush)FindResource("AccentSoftBrush");
        }
        else if (_host.IsConfigured)
        {
            TitlePillText.Text = "已暂停";
            TitlePillText.Foreground = (Brush)FindResource("MutedBrush");
            TitlePillDot.Fill = (Brush)FindResource("MutedBrush");
            TitlePillBorder.Background = (Brush)FindResource("CanvasBrush");
        }
        else
        {
            TitlePillText.Text = "未设置";
            TitlePillText.Foreground = (Brush)FindResource("MutedBrush");
            TitlePillDot.Fill = (Brush)FindResource("MutedBrush");
            TitlePillBorder.Background = (Brush)FindResource("CanvasBrush");
        }
    }

    private static string FormatMinutes(TimeSpan span)
    {
        if (span.TotalHours >= 1)
        {
            var hours = (int)span.TotalHours;
            var minutes = span.Minutes;
            return minutes == 0 ? $"{hours} 小时" : $"{hours} 小时 {minutes} 分";
        }

        return $"{(int)span.TotalMinutes} 分钟";
    }

    private static void SetDonut(System.Windows.Shapes.Path path, double fraction)
    {
        if (fraction <= 0)
        {
            path.Data = null;
            return;
        }

        if (fraction > 1)
        {
            fraction = 1;
        }

        const double radius = 57.5;
        const double cx = 64;
        const double cy = 64;
        var start = new System.Windows.Point(cx, cy - radius);
        var radians = ((fraction * 360.0) - 90.0) * Math.PI / 180.0;
        var end = new System.Windows.Point(cx + (radius * Math.Cos(radians)), cy + (radius * Math.Sin(radians)));
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        path.Data = geometry;
    }

    private void UpdateDashboard(SessionSnapshot snapshot)
    {
        UpdateStatusPill(snapshot);
        UpdateEngineStatus();

        var budget = _host.Budget;
        var limit = budget.Limit;
        var used = budget.Used;
        var fraction = limit <= TimeSpan.Zero ? 0 : used.TotalMinutes / limit.TotalMinutes;
        SetDonut(DonutProgress, fraction);
        DonutRemainingText.Text = FormatRemaining(budget.Remaining);
        DonutUsedText.Text = $"已用 {FormatMinutes(used)} / 共 {FormatMinutes(limit)}";
        DonutHintText.Text = snapshot.IsGuarding
            ? "正在守护。时间用完会自动锁到系统桌面。"
            : _host.IsConfigured
                ? "现在没有守护。点右上角「开始守护」。"
                : "先完成家长设置，今天的时间额度才会生效。";

        var desk = snapshot.Phase == SessionPhase.InDesk && snapshot.DeskId is not null
            ? _host.FindDesk(snapshot.DeskId)
            : _host.FindDesk(_host.Family?.DeskId ?? string.Empty);
        if (desk is not null)
        {
            DashboardDeskName.Text = desk.Name;
            DashboardDeskSummary.Text = desk.Summary;
            DashboardDeskApps.ItemsSource = desk.Apps;
            DashboardDeskEmpty.Visibility = desk.Apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            DashboardDeskName.Text = "—";
            DashboardDeskSummary.Text = "还没有选书桌。";
            DashboardDeskApps.ItemsSource = null;
            DashboardDeskEmpty.Visibility = Visibility.Visible;
        }

        DashboardGuardButton.Content = !_host.IsConfigured
            ? "完成家长设置"
            : snapshot.IsGuarding
                ? "暂停守护"
                : "开始守护";
        DashboardGuardButton.IsEnabled = snapshot.Phase == SessionPhase.Idle || !snapshot.IsGuarding;
        RewardButton.Visibility = _host.IsConfigured ? Visibility.Visible : Visibility.Collapsed;

        GreetingText.Text = Greeting();
        HeroSummaryText.Text = HeroLine(snapshot, used, limit);
        RefreshDeskCards();
        var familyLimits = _host.Family;
        WeekdayLimitText.Text = $"周内每天 {DescribeMinutes(familyLimits?.WeekdayMinutes ?? familyLimits?.DailyMinutes ?? 60)}";
        WeekendLimitText.Text = $"周末每天 {DescribeMinutes(familyLimits?.WeekendMinutes ?? familyLimits?.DailyMinutes ?? 120)}";
        AnimateDashboardOnce();
        DashboardBlockedEmpty.Visibility = _blocked.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (DashboardBlockedList.ItemsSource is null)
        {
            DashboardBlockedList.ItemsSource = _blocked.ToList();
        }

        DashboardHintText.Text = _startupHint ?? string.Empty;
        RefreshWeekUsage();
        RefreshAppUsage();
    }

    /// <summary>仪表盘三张书桌模板卡的摘要：用已配置书桌的说明和软件数，没动过就回落到内置说明。</summary>
    private void RefreshDeskCards()
    {
        DeskCardHomeworkSummary.Text = DeskCardSummary(BuiltinDesks.HomeworkId, "文档 + 词典 + 计算器");
        DeskCardClassSummary.Text = DeskCardSummary(BuiltinDesks.ClassId, "浏览器 + 笔记");
        DeskCardCodeSummary.Text = DeskCardSummary(BuiltinDesks.CodeId, "IDE + 终端");
    }

    private string DeskCardSummary(string id, string fallback)
    {
        var desk = _host.FindDesk(id);
        // 书桌的 Summary 本身就带「…等 N 款」的信息，别再叠加一份软件数。
        return string.IsNullOrWhiteSpace(desk?.Summary) ? fallback : desk.Summary;
    }

    private static string Greeting()
    {
        var h = DateTime.Now.Hour;
        return h switch
        {
            < 6 => "夜深了",
            < 12 => "早上好",
            < 14 => "中午好",
            < 18 => "下午好",
            _ => "晚上好",
        };
    }

    private string HeroLine(SessionSnapshot snapshot, TimeSpan used, TimeSpan limit)
    {
        if (!_host.IsConfigured)
        {
            return "先完成家长设置，今天的时间额度才会生效。";
        }

        if (snapshot.IsGuarding)
        {
            var remaining = limit - used;
            if (remaining < TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
            }

            return $"守护进行中 · 孩子今天已用 {FormatMinutes(used)}，还剩 {FormatRemaining(remaining)}。";
        }

        return $"今天共 {FormatMinutes(limit)}，现在没在守护。点右侧按钮就能开始。";
    }

    private void AnimateDashboardOnce()
    {
        if (_dashboardAnimated)
        {
            return;
        }

        _dashboardAnimated = true;
        Fade(DashboardPage, 0, 1, null);
    }

    private sealed record WeekUsageRow(string Label, double BarHeight, string Summary);

    private sealed record AppUsageRow(
        string DisplayName, string Summary, double BarWidth, Brush BarBrush, Brush SummaryBrush);

    private sealed record AppLimitChoice(string Key, string DisplayName);

    private sealed record AppLimitRow(string Key, string DisplayName, string Summary);

    private sealed record DayLimitRow(DayOfWeek Day, string Label, int Minutes, bool IsCustom);

    private string _appUsageSignature = string.Empty;

    private static readonly Brush UsageBarBrush =
        BrushFromResource("AccentBrush", Color.FromRgb(0x2C, 0x45, 0x38));

    private static readonly Brush UsageBarOverBrush =
        BrushFromResource("DangerBrush", Color.FromRgb(0xA2, 0x45, 0x32));

    private static readonly Brush UsageTextBrush =
        BrushFromResource("MutedBrush", Color.FromRgb(0x6E, 0x67, 0x5E));

    private static Brush BrushFromResource(string key, Color fallback)
    {
        try
        {
            if (System.Windows.Application.Current?.TryFindResource(key) is Brush brush)
            {
                return brush;
            }
        }
        catch (Exception)
        {
            // 资源还没就绪时回落到写死的同色值。
        }

        var solid = new SolidColorBrush(fallback);
        solid.Freeze();
        return solid;
    }

    /// <summary>今天每个软件用了多久。用量每秒变化，但只有内容真的变了才重刷列表。</summary>
    private void RefreshAppUsage()
    {
        var rows = _host.AppUsage ?? [];
        var signature = string.Join("|", rows.Select(r => $"{r.Key}:{r.UsedMinutes}:{r.LimitMinutes}"));
        if (signature == _appUsageSignature)
        {
            return;
        }

        _appUsageSignature = signature;

        var tracked = rows.Where(r => r.UsedMinutes > 0 || r.HasLimit).ToList();
        if (tracked.Count == 0)
        {
            AppUsageList.ItemsSource = null;
            AppUsageEmpty.Visibility = Visibility.Visible;
            AppUsageHint.Text = string.Empty;
            return;
        }

        // 有限额的软件按自己的额度画条（看还剩多少），其余按彼此的相对用量画条（看谁用得多）。
        var maxUsed = Math.Max(1, tracked.Max(r => r.UsedMinutes));
        double Fraction(AppUsage row) => row.HasLimit
            ? row.Fraction
            : Math.Clamp((double)row.UsedMinutes / maxUsed, 0d, 1d);

        AppUsageList.ItemsSource = tracked
            .Select(row => new AppUsageRow(
                row.DisplayName,
                row.Summary,
                Math.Max(6, Math.Round(260 * Fraction(row))),
                row.Exhausted ? UsageBarOverBrush : UsageBarBrush,
                row.Exhausted ? UsageBarOverBrush : UsageTextBrush))
            .ToList();
        AppUsageEmpty.Visibility = Visibility.Collapsed;

        var usedCount = tracked.Count(r => r.UsedMinutes > 0);
        var overCount = tracked.Count(r => r.Exhausted);
        AppUsageHint.Text = overCount > 0
            ? $"{usedCount} 款在用 · {overCount} 款额度用完"
            : $"{usedCount} 款在用";
    }

    /// <summary>设置页里「给单个软件单独限时」的候选与已设限额。</summary>
    private void RefreshAppLimits()
    {
        var desk = DeskList.SelectedItem as Desk;
        var choices = (desk?.Apps ?? [])
            .GroupBy(a => a.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(a => new AppLimitChoice(a.Key, a.DisplayName))
            .ToList();
        LimitAppCombo.ItemsSource = choices;
        if (LimitAppCombo.SelectedItem is null && choices.Count > 0)
        {
            LimitAppCombo.SelectedIndex = 0;
        }

        var limits = (desk?.LimitedApps ?? [])
            .Where(a => a.DailyMinutes is > 0)
            .Select(a => new AppLimitRow(a.Key, a.DisplayName, $"每天 {a.DailyMinutes} 分钟"))
            .ToList();
        AppLimitList.ItemsSource = limits;
        AppLimitList.Visibility = limits.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        AppLimitEmpty.Visibility = limits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddAppLimit_Click(object sender, RoutedEventArgs e)
    {
        if (DeskList.SelectedItem is not Desk desk)
        {
            return;
        }

        if (LimitAppCombo.SelectedValue is not string key)
        {
            ParentHint.Text = "先从下拉框里挑一款软件。";
            return;
        }

        if (!int.TryParse(LimitMinutesBox.Text.Trim(), out var minutes))
        {
            ParentHint.Text = "分钟数要填数字，范围 5–600。";
            return;
        }

        var limited = desk.WithAppLimit(key, minutes);
        if (ReferenceEquals(limited, desk))
        {
            ParentHint.Text = "这条限额已经是这样了。";
            return;
        }

        ParentHint.Text = string.Empty;
        LimitMinutesBox.Text = string.Empty;
        SaveDesk(limited);
    }

    private void RemoveAppLimit_Click(object sender, RoutedEventArgs e)
    {
        if (DeskList.SelectedItem is not Desk desk)
        {
            return;
        }

        if ((sender as FrameworkElement)?.Tag is not string key)
        {
            return;
        }

        SaveDesk(desk.WithAppLimit(key, null));
    }

    private void CopyRecovery_Click(object sender, RoutedEventArgs e)
    {
        var code = _host.Family?.RecoveryCode;
        if (string.IsNullOrWhiteSpace(code))
        {
            RecoveryHint.Text = _host.IsRemote
                ? "守护服务不再显示找回码；请翻出首次设置时抄下的那枚。"
                : "还没有生成找回码，先完成家长设置。";
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(code);
            RecoveryHint.Text = "已复制到剪贴板，请粘贴到备忘或纸质本子上。";
        }
        catch (Exception)
        {
            RecoveryHint.Text = "复制失败，请手动抄下来。";
        }
    }

    private void RefreshWeekUsage()
    {
        if (DateTime.Now < _weekUsageNextRead)
        {
            return;
        }

        _weekUsageNextRead = DateTime.Now.AddSeconds(30);

        var history = _usageLog.ReadRecent(6);
        var usedToday = _host.IsConfigured ? _host.Budget.Used : TimeSpan.Zero;
        if (history.Count == 0 && usedToday <= TimeSpan.Zero)
        {
            WeekUsageList.ItemsSource = null;
            WeekUsageEmpty.Visibility = Visibility.Visible;
            return;
        }

        var maxMinutes = Math.Max(1, Math.Max(
            usedToday.TotalMinutes,
            history.Count == 0 ? 1 : history.Max(d => d.UsedMinutes)));
        double BarHeightFor(double minutes) => Math.Max(6, Math.Round(110 * minutes / maxMinutes));

        var rows = new List<WeekUsageRow>();
        if (usedToday > TimeSpan.Zero)
        {
            rows.Add(new WeekUsageRow(
                "今天",
                BarHeightFor(usedToday.TotalMinutes),
                $"已用 {FormatMinutes(usedToday)} / {FormatMinutes(_host.Budget.Limit)}"));
        }

        foreach (var day in history)
        {
            var summary = FormatMinutes(TimeSpan.FromMinutes(day.UsedMinutes))
                + (day.BlockedCount > 0 ? $" · 拦了 {day.BlockedCount} 次" : string.Empty);
            rows.Add(new WeekUsageRow(
                $"{day.Date.Month}/{day.Date.Day} {DayLabel(day.Date.DayOfWeek)}",
                BarHeightFor(day.UsedMinutes),
                summary));
        }

        WeekUsageList.ItemsSource = rows;
        WeekUsageEmpty.Visibility = Visibility.Collapsed;
    }

    private static string DayLabel(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日",
    };

    private void Render(SessionSnapshot snapshot, string? hint = null)
    {
        var child = snapshot.Phase is SessionPhase.InDesk or SessionPhase.TimeUp;
        ShowChild(child);
        Background = (Brush)FindResource(child ? "PaperDeepBrush" : "PaperBrush");
        AskParentButton.Visibility = snapshot.Parental ? Visibility.Visible : Visibility.Collapsed;
        AskParentNight.Visibility = AskParentButton.Visibility;

        // 时间用完切到夜晚锁屏面板（ui-03），其余状态留在书桌场景（ui-02）。
        var timeUp = snapshot.Phase == SessionPhase.TimeUp;
        DeskPanel.Visibility = timeUp ? Visibility.Collapsed : Visibility.Visible;
        TimeUpPanel.Visibility = timeUp ? Visibility.Visible : Visibility.Collapsed;

        // 守护中不给「×」：点了也不会退出，留着只会让孩子一直试。
        CloseButton.Visibility = snapshot.Parental && child ? Visibility.Collapsed : Visibility.Visible;

        if (!child)
        {
            FirstRunPinPanel.Visibility = _host.IsConfigured ? Visibility.Collapsed : Visibility.Visible;
            GuardButton.IsEnabled = DeskList.SelectedItem is Desk d && d.Apps.Count > 0;
            UpdateDashboard(snapshot);
            return;
        }

        UpdateStatusPill(snapshot);

        RemainingText.Text = FormatRemaining(snapshot.Remaining);
        AskMoreButton.Visibility = snapshot.Phase == SessionPhase.TimeUp && snapshot.Parental
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (snapshot.Phase == SessionPhase.TimeUp)
        {
            ChildCaption.Text = "今天的屏幕时间用完了";
            TimeUpHint.Text = string.IsNullOrWhiteSpace(hint)
                ? "明天早上自动恢复；家长可以加时，或输入密码结束守护。已锁屏的话，登录后会自动弹出此界面。"
                : hint;
            ChildAppChips.ItemsSource = null;
        }
        else if (!snapshot.Parental)
        {
            ChildCaption.Text = "试拦截还剩";
            ChildHint.Text = string.IsNullOrWhiteSpace(hint)
                ? "只留计算器。打开记事本应被关掉。"
                : hint;
            ChildAppChips.ItemsSource = BuiltinDesks.Spike().Apps;
        }
        else
        {
            ChildCaption.Text = "今天还剩";
            var desk = snapshot.DeskId is null ? null : _host.FindDesk(snapshot.DeskId);
            ChildAppChips.ItemsSource = desk?.Apps;
            ChildHint.Text = string.IsNullOrWhiteSpace(hint) ? ChildHintFor(snapshot) : hint;
        }
    }

    private string ChildHintFor(SessionSnapshot snapshot)
    {
        var desk = snapshot.DeskId is null ? null : _host.FindDesk(snapshot.DeskId);
        var names = desk?.Apps
            .Select(a => a.DisplayName)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Take(6)
            .ToArray() ?? [];
        return names.Length == 0
            ? "现在只能用系统桌面。其它软件会被关掉。"
            : $"只能用 {string.Join("、", names)}。其它软件会被关掉。";
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining.TotalHours >= 1)
        {
            return $"{(int)remaining.TotalHours}:{remaining.Minutes:D2}:{remaining.Seconds:D2}";
        }

        return remaining.ToString(@"mm\:ss");
    }

    private void ShowChild(bool on)
    {
        // 守护中隐藏侧边栏（连列宽一起归零），孩子视图独占整个窗口。
        Sidebar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        SidebarColumn.Width = on ? new GridLength(0) : new GridLength(232);

        if (on && ChildRoot.Visibility != Visibility.Visible)
        {
            Fade(ParentHost, 1, 0, () =>
            {
                ParentHost.Visibility = Visibility.Collapsed;
                ParentHost.Opacity = 1;
            });
            ChildRoot.Opacity = 0;
            ChildRoot.Visibility = Visibility.Visible;
            Fade(ChildRoot, 0, 1, null);
        }
        else if (!on && ParentHost.Visibility != Visibility.Visible)
        {
            Fade(ChildRoot, 1, 0, () =>
            {
                ChildRoot.Visibility = Visibility.Collapsed;
                ChildRoot.Opacity = 1;
            });
            ParentHost.Opacity = 0;
            ParentHost.Visibility = Visibility.Visible;
            Fade(ParentHost, 0, 1, null);
        }
    }

    private static void Fade(UIElement element, double from, double to, Action? done)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
        };
        animation.Completed += (_, _) => done?.Invoke();
        element.BeginAnimation(OpacityProperty, animation);
    }
}
