using System.Windows;
using System.Windows.Input;
using Chengshi.Engine;

namespace Chengshi.App;

/// <summary>孩子申请加时、家长当场批准的小窗。批准成功后 DialogResult=true。</summary>
public partial class ExtendTimeWindow : Window
{
    private readonly ISessionControl _host;

    public ExtendTimeWindow(ISessionControl host)
    {
        _host = host;
        InitializeComponent();
        AppIcon.Apply(this);
        Loaded += (_, _) => Keyboard.Focus(PinBox);
    }

    public bool Granted { get; private set; }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var minutes = new[] { Extra15, Extra30, Extra60 }
            .Where(b => b.IsChecked == true)
            .Select(b => int.Parse((string)b.Tag))
            .FirstOrDefault(30);
        ErrorText.Text = string.Empty;
        try
        {
            var result = _host.GrantExtra(PinBox.Password, minutes);
            if (result.Ok)
            {
                Granted = true;
                DialogResult = true;
                return;
            }

            ErrorText.Text = result.Hint;
            PinBox.SelectAll();
            Keyboard.Focus(PinBox);
        }
        catch (Exception ex) when (ex is InvalidOperationException or RemoteFaultException)
        {
            ErrorText.Text = ex.Message;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
