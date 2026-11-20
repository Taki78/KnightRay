using SkiaSharp;

namespace v2rayN.Desktop.Common;

internal static class ProxyTrayIcon
{
    private static readonly Dictionary<string, WindowIcon> _icons = new();

    public static WindowIcon Get(ESysProxyType mode, WindowIcon appIcon)
    {
        if (!Global.IsTakiDesktop || File.Exists(AvaUtils.GetCustomIconPath(mode)))
        {
            return appIcon;
        }

        var color = mode switch
        {
            ESysProxyType.ForcedClear => "blue",
            ESysProxyType.ForcedChange or ESysProxyType.Pac => "red",
            _ => "purple",
        };
        if (!_icons.TryGetValue(color, out var icon))
        {
            icon = Load(color);
            _icons.Add(color, icon);
        }
        return icon;
    }

    private static WindowIcon Load(string color)
    {
        using var source = AssetLoader.Open(new Uri($"{Global.AvaAssets}Tray/{color}-helmet-icon.png"));
        using var bitmap = SKBitmap.Decode(source);
        var pixels = bitmap.Pixels;
        var bounds = new SKRectI(bitmap.Width, bitmap.Height, 0, 0);
        for (var i = 0; i < pixels.Length; i++)
        {
            var pixel = pixels[i];
            if (pixel.Alpha == 0 || (pixel.Red >= 220 && pixel.Green >= 220 && pixel.Blue >= 220))
            {
                // The blue and purple artwork has a near-white background.
                pixels[i] = SKColors.Transparent;
                continue;
            }

            var x = i % bitmap.Width;
            var y = i / bitmap.Width;
            bounds.Left = Math.Min(bounds.Left, x);
            bounds.Top = Math.Min(bounds.Top, y);
            bounds.Right = Math.Max(bounds.Right, x + 1);
            bounds.Bottom = Math.Max(bounds.Bottom, y + 1);
        }
        bitmap.Pixels = pixels;

        // The system controls the tray size. Fill most of its icon area with the helmet.
        const int size = 128;
        const int padding = 3;
        var scale = (float)(size - padding * 2) / Math.Max(bounds.Width, bounds.Height);
        var width = bounds.Width * scale;
        var height = bounds.Height * scale;
        var destination = SKRect.Create((size - width) / 2, (size - height) / 2, width, height);
        using var surface = SKSurface.Create(new SKImageInfo(size, size));
        using var image = SKImage.FromBitmap(bitmap);
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawImage(image, bounds, destination, new SKSamplingOptions(SKFilterMode.Linear));
        using var rendered = surface.Snapshot();
        using var png = rendered.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = png.AsStream();
        using var trayBitmap = new Bitmap(stream);
        return new WindowIcon(trayBitmap);
    }
}
