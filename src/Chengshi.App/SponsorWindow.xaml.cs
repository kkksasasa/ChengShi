using System.Windows;
using System.Windows.Input;

namespace Chengshi.App;

/// <summary>「支持澄时」小窗：展示支付宝 / 微信收款码。</summary>
public partial class SponsorWindow : Window
{
    public SponsorWindow()
    {
        InitializeComponent();
        AppIcon.Apply(this);
        Loaded += (_, _) => Keyboard.Focus(CloseButton);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
