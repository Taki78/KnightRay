using Avalonia.Input.Platform;

namespace v2rayN.Desktop.Common;

internal class AvaUtils
{
    public static async Task<string?> GetClipboardData(Window owner)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(owner)?.Clipboard;
            if (clipboard == null)
            {
                return null;
            }

            return await clipboard.TryGetTextAsync();
        }
        catch
        {
            return null;
        }
    }

    public static async Task SetClipboardData(Visual? visual, string strData)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(visual)?.Clipboard;
            if (clipboard == null)
            {
                return;
            }

            await clipboard.SetTextAsync(strData);
        }
        catch
        {
        }
    }

    public static WindowIcon GetAppIcon(ESysProxyType sysProxyType)
    {
        var index = (int)sysProxyType + 1;
        var fileName = GetCustomIconPath(sysProxyType);
        if (File.Exists(fileName))
        {
            return new(fileName);
        }

        var icon = Global.IsTakiDesktop ? "v2rayTaki.png" : $"NotifyIcon{index}.ico";
        var uri = new Uri(Global.AvaAssets + icon);
        using var stream = AssetLoader.Open(uri);
        using var bitmap = new Bitmap(stream);
        return new(bitmap);
    }

    public static string GetCustomIconPath(ESysProxyType type) => Utils.GetPath($"NotifyIcon{(int)type + 1}.ico");
}
