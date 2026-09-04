using System.Windows;
using System.Windows.Media.Imaging;

namespace Chengshi.App;

internal static class AppIcon
{
    public static void Apply(Window window)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "chengshi.ico");
            if (!File.Exists(path))
            {
                path = Path.Combine(AppContext.BaseDirectory, "Assets", "chengshi.ico");
            }

            if (!File.Exists(path))
            {
                return;
            }

            using var stream = File.OpenRead(path);
            var decoder = new IconBitmapDecoder(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            window.Icon = decoder.Frames[0];
        }
        catch (Exception)
        {
            // 没有图标也不挡窗口显示。
        }
    }
}
