using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Chengshi.Core;
using Chengshi.Engine;
using Chengshi.Ipc;

namespace Chengshi.App;

/// <summary>首次打开时的引导式设置：欢迎 → 设家长密码 → 选书桌和时长 → 完成并开始守护。</summary>
public partial class OnboardingWindow : Window
{
    private readonly ISessionControl _host;
    private bool _ready;
    private int _step = 1;
    private string _pin = string.Empty;
    private string _deskId = string.Empty;
    private int _minutes = 60;
    private string? _recovery;

    public OnboardingWindow(ISessionControl host)
    {
        _host = host;
        InitializeComponent();
        AppIcon.Apply(this);
        LoadDesks();
        ApplyStep();
        _ready = true;
    }

    private void LoadDesks()
    {
        var desks = _host.Desks
            .Where(d => d.Id is not BuiltinDesks.SpikeId and not BuiltinDesks.LockdownId)
            .ToList();
        OnboardDeskList.ItemsSource = desks;
        OnboardDeskList.SelectedIndex = 0;
        if (desks.FirstOrDefault() is { } first)
        {
            _deskId = first.Id;
        }
    }

    private void ApplyStep()
    {
        Step1.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Step4.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;

        StepIndicator.Text = $"第 {_step} / 4 步";
        BackButton.Visibility = _step > 1 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = _step < 4 ? Visibility.Visible : Visibility.Collapsed;
        FinishButton.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;
        SkipButton.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;

        SetDot(StepDot1, 1);
        SetDot(StepDot2, 2);
        SetDot(StepDot3, 3);
        SetDot(StepDot4, 4);
    }

    private void SetDot(UIElement dot, int step)
    {
        if (dot is not StackPanel panel) return;
        var ellipse = (Ellipse)panel.Children[0];
        var text = (TextBlock)panel.Children[1];
        var done = step <= _step;
        ellipse.Fill = done
            ? (Brush)FindResource("AccentOnBrush")
            : (Brush)FindResource("SideLineBrush");
        text.Foreground = done
            ? (Brush)FindResource("SideActiveBrush")
            : (Brush)FindResource("SideMutedBrush");
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step == 1)
        {
            Go(2);
        }
        else if (_step == 2)
        {
            if (!ValidatePin())
            {
                return;
            }

            Go(3);
        }
        else if (_step == 3)
        {
            if (!ValidateDesk())
            {
                return;
            }

            BuildSummary();
            Go(4);
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step > 1)
        {
            Go(_step - 1);
        }
    }

    private void Go(int step)
    {
        _step = step;
        ApplyStep();
    }

    private bool ValidatePin()
    {
        var pin = PinCreateBox.Password;
        var confirm = PinConfirmBox.Password;
        if (PinHasher.NormalizePin(pin).Length < 4)
        {
            PinError.Text = "家长密码至少 4 位。";
            return false;
        }

        if (PinHasher.NormalizePin(pin) != PinHasher.NormalizePin(confirm))
        {
            PinError.Text = "两次输入的密码不一致。";
            return false;
        }

        _pin = pin;
        PinError.Text = string.Empty;
        return true;
    }

    private bool ValidateDesk()
    {
        if (OnboardDeskList.SelectedItem is not Desk desk)
        {
            return false;
        }

        _deskId = desk.Id;
        if (OnboardDurCustom.IsChecked == true)
        {
            if (!int.TryParse(OnboardCustomBox.Text.Trim(), out var minutes) || minutes is < 5 or > 600)
            {
                return false;
            }

            _minutes = minutes;
        }

        return true;
    }

    private void OnboardDeskList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OnboardDeskList.SelectedItem is Desk desk)
        {
            _deskId = desk.Id;
        }
    }

    private void OnboardDuration_Checked(object sender, RoutedEventArgs e)
    {
        // InitializeComponent 期间默认选中的时长按钮就会触发本事件，
        // 此时同 XAML 里更靠后的控件还没解析出来，必须先等构造完成。
        if (!_ready || sender is not RadioButton { Tag: string tag })
        {
            return;
        }

        if (tag == "custom")
        {
            OnboardCustomPanel.Visibility = Visibility.Visible;
            OnboardCustomBox.Focus();
            OnboardCustomBox.SelectAll();
            return;
        }

        OnboardCustomPanel.Visibility = Visibility.Collapsed;
        if (int.TryParse(tag, out var minutes))
        {
            _minutes = minutes;
        }
    }

    private void ApplyPreset_Click(object sender, RoutedEventArgs e)
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

        var deskId = parts[0];
        foreach (Desk desk in OnboardDeskList.Items)
        {
            if (string.Equals(desk.Id, deskId, StringComparison.OrdinalIgnoreCase))
            {
                OnboardDeskList.SelectedItem = desk;
                _deskId = desk.Id;
                break;
            }
        }

        SetOnboardDuration(minutes);
    }

    private void SetOnboardDuration(int minutes)
    {
        RadioButton? match = minutes switch
        {
            30 => OnboardDur30,
            60 => OnboardDur60,
            90 => OnboardDur90,
            _ => null,
        };

        if (match is not null)
        {
            match.IsChecked = true;
            OnboardCustomPanel.Visibility = Visibility.Collapsed;
            _minutes = minutes;
        }
        else
        {
            OnboardDurCustom.IsChecked = true;
            OnboardCustomBox.Text = minutes.ToString();
            OnboardCustomPanel.Visibility = Visibility.Visible;
            _minutes = minutes;
        }
    }

    private void BuildSummary()
    {
        var desk = OnboardDeskList.SelectedItem as Desk;
        SummaryDesk.Text = desk?.Name ?? "—";
        SummaryMinutes.Text = _minutes switch
        {
            30 => "30 分钟",
            60 => "1 小时",
            90 => "90 分钟",
            120 => "2 小时",
            _ => $"{_minutes} 分钟",
        };
        _recovery = FamilySettings.NewRecoveryCode();
        RecoveryCodeText.Text = _recovery ?? "————";
        FinishError.Text = string.Empty;
    }

    private void CopyRecovery_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_recovery))
        {
            return;
        }

        try
        {
            Clipboard.SetText(_recovery);
        }
        catch (Exception)
        {
            // 剪贴板不可用时忽略，用户可手动抄写。
        }
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => FinishCore(guard: false);

    private void Finish_Click(object sender, RoutedEventArgs e) => FinishCore(guard: true);

    private void FinishCore(bool guard)
    {
        try
        {
            var family = _host.SaveFamily(FamilySettings.Create(
                _pin,
                _minutes,
                _deskId,
                recoveryCode: _recovery));
            _recovery = family.RecoveryCode;
            RecoveryCodeText.Text = _recovery ?? RecoveryCodeText.Text;

            if (guard && _host.Family is { } saved && saved.DeskId == _deskId)
            {
                try
                {
                    _host.StartGuard();
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or RemoteFaultException)
                {
                    FinishError.Text = "设置已保存，但没能自动开始守护：" + ex.Message + " 关掉这个窗口后手动点「开始守护」即可。";
                    return;
                }
            }

            DialogResult = true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or RemoteFaultException)
        {
            FinishError.Text = ex.Message;
        }
    }
}
