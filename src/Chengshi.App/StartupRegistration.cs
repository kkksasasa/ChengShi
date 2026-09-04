using System.IO;
using Microsoft.Win32;

namespace Chengshi.App;

internal static class StartupRegistration
{
    private const string ValueName = "Chengshi";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (key is null)
        {
            return;
        }

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var vbs = Path.Combine(AppContext.BaseDirectory, "startup.vbs");
        var exe = Path.Combine(AppContext.BaseDirectory, "Chengshi.App.exe");
        var command = File.Exists(vbs)
            ? $"wscript.exe \"{vbs}\""
            : $"\"{exe}\" --tray";
        key.SetValue(ValueName, command);
    }
}
