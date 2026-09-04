using System.Windows;
using System.Windows.Input;
using Chengshi.Core;
using Chengshi.Engine;

namespace Chengshi.App;

public partial class PinWindow : Window
{
    private readonly ISessionControl _host;
    private readonly string _verifyPrompt;
    private bool _forgot;
    private bool _emailMode;

    public PinWindow(string prompt, ISessionControl host)
    {
        _host = host;
        _verifyPrompt = prompt;
        InitializeComponent();
        AppIcon.Apply(this);
        PromptText.Text = prompt;
        Loaded += (_, _) =>
        {
            Activate();
            Keyboard.Focus(PinBox);
        };
        Activated += (_, _) =>
        {
            if (!_forgot && !_emailMode)
            {
                Keyboard.Focus(ShowPinBox.IsChecked == true ? PinPlainBox : PinBox);
            }
        };
    }

    public string Pin { get; private set; } = string.Empty;

    private void ShowPin_Changed(object sender, RoutedEventArgs e)
    {
        if (ShowPinBox.IsChecked == true)
        {
            PinPlainBox.Text = PinBox.Password;
            PinBox.Visibility = Visibility.Collapsed;
            PinPlainBox.Visibility = Visibility.Visible;
            Keyboard.Focus(PinPlainBox);
        }
        else
        {
            PinBox.Password = PinPlainBox.Text;
            PinPlainBox.Visibility = Visibility.Collapsed;
            PinBox.Visibility = Visibility.Visible;
            Keyboard.Focus(PinBox);
        }
    }

    private void Forgot_Click(object sender, RoutedEventArgs e)
    {
        _forgot = true;
        TitleText.Text = "重置家长密码";
        PromptText.Text = "输入设置时抄下的找回码，再设新密码。找回码丢了可以用下面的邮箱找回。";
        VerifyPanel.Visibility = Visibility.Collapsed;
        ForgotPanel.Visibility = Visibility.Visible;
        OkButton.Content = "重置";
        Keyboard.Focus(RecoveryBox);
    }

    private void EmailForgot_Click(object sender, RoutedEventArgs e)
    {
        _emailMode = true;
        TitleText.Text = "用邮箱找回密码";
        PromptText.Text = "输入设置里预留的备用邮箱，守护服务会把验证码发到那个邮箱。";
        VerifyPanel.Visibility = Visibility.Collapsed;
        ForgotPanel.Visibility = Visibility.Collapsed;
        EmailRecoveryPanel.Visibility = Visibility.Visible;
        OkCancelPanel.Visibility = Visibility.Collapsed;

        var reserved = _host.Family?.RecoveryEmail;
        if (!string.IsNullOrWhiteSpace(reserved))
        {
            EmailBox.Text = reserved;
        }

        Keyboard.Focus(EmailBox);
    }

    private async void EmailSend_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        EmailHint.Text = string.Empty;
        var email = EmailBox.Text.Trim();
        var reserved = _host.Family?.RecoveryEmail;

        if (string.IsNullOrWhiteSpace(email))
        {
            ErrorText.Text = "请先填写备用邮箱。";
            return;
        }

        if (string.IsNullOrWhiteSpace(reserved) || !email.Equals(reserved, StringComparison.OrdinalIgnoreCase))
        {
            ErrorText.Text = "这个邮箱和设置里预留的备用邮箱不一致。";
            return;
        }

        try
        {
            // 发码和校验都在守护服务端完成，验证码不经过界面进程。
            await _host.SendEmailRecoveryCodeAsync(email);
            EmailHint.Text = $"验证码已发到 {reserved}，请查收（10 分钟内有效）。";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or RemoteFaultException)
        {
            ErrorText.Text = ex.Message;
        }
    }

    private async void EmailReset_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        var email = EmailBox.Text.Trim();
        var reserved = _host.Family?.RecoveryEmail;

        if (string.IsNullOrWhiteSpace(reserved) || !email.Equals(reserved, StringComparison.OrdinalIgnoreCase))
        {
            ErrorText.Text = "邮箱与预留的备用邮箱不一致。";
            return;
        }

        var neu = EmailNewPinBox.Password;
        if (neu.Length < 4)
        {
            ErrorText.Text = "新密码至少 4 位。";
            return;
        }

        if (neu != EmailNewPinConfirmBox.Password)
        {
            ErrorText.Text = "两次新密码不一致。";
            return;
        }

        try
        {
            // 服务端校验邮箱验证码；重置会生成新找回码，只在这次弹窗里给家长抄。
            var result = await _host.RecoverPinWithEmailAsync(email, EmailCodeBox.Text, neu);
            Pin = neu;
            System.Windows.MessageBox.Show(
                $"密码已重置。新的找回码：{result.NewRecoveryCode}\n请马上记下来。", "澄时");
            DialogResult = true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or RemoteFaultException)
        {
            ErrorText.Text = ex.Message;
        }
    }

    private void EmailBack_Click(object sender, RoutedEventArgs e)
    {
        _emailMode = false;
        TitleText.Text = "家长密码";
        PromptText.Text = _verifyPrompt;
        EmailRecoveryPanel.Visibility = Visibility.Collapsed;
        VerifyPanel.Visibility = Visibility.Visible;
        OkCancelPanel.Visibility = Visibility.Visible;
        ErrorText.Text = string.Empty;
        Keyboard.Focus(ShowPinBox.IsChecked == true ? PinPlainBox : PinBox);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        try
        {
            if (_forgot)
            {
                var neu = NewPinBox.Password;
                if (neu != NewPinConfirmBox.Password)
                {
                    ErrorText.Text = "两次新密码不一致。";
                    return;
                }

                var saved = _host.RecoverPin(RecoveryBox.Text, neu);
                Pin = neu;
                // 找回码正确时服务保留原码；本地模式会返回原码，远程模式返回空。
                var code = saved.RecoveryCode;
                System.Windows.MessageBox.Show(
                    string.IsNullOrWhiteSpace(code)
                        ? "密码已重置。找回码不变，还是你抄下的那枚。"
                        : $"密码已重置。找回码：{code}\n请马上记下来。",
                    "澄时");
                DialogResult = true;
                return;
            }

            var pin = ShowPinBox.IsChecked == true ? PinPlainBox.Text : PinBox.Password;
            if (string.IsNullOrWhiteSpace(pin))
            {
                ErrorText.Text = "请输入家长密码。";
                Keyboard.Focus(PinBox);
                return;
            }

            if (!_host.VerifyParentPin(pin))
            {
                ErrorText.Text = "密码不对。可点忘记密码重置。";
                PinBox.Clear();
                PinPlainBox.Clear();
                Keyboard.Focus(PinBox);
                return;
            }

            Pin = pin;
            DialogResult = true;
        }
        catch (ArgumentException ex)
        {
            ErrorText.Text = ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            ErrorText.Text = ex.Message;
        }
        catch (RemoteFaultException ex)
        {
            ErrorText.Text = ex.Message;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
