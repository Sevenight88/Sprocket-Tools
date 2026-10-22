namespace SprocketTools;

/// 照片尺寸：保持源画面比例；RGB 缩放不会把渲染 alpha 再合成一次。
internal static class PhotoOutput
{
    internal static readonly string[] Resolutions = { "屏幕", "2K", "4K", "6K", "8K" };
    internal static readonly string[] Methods = { "渲染", "放大" };
    internal readonly record struct Size(int Width, int Height);

    internal static Size SizeFor(int width, int height, string? resolution)
    {
        if (width <= 0 || height <= 0 || width > 32768 || height > 32768) throw new ArgumentOutOfRangeException(nameof(width));
        int edge = resolution switch { "2K" => 2560, "4K" => 3840, "6K" => 5760, "8K" => 7680, _ => 0 };
        if (edge == 0) return new(width, height);
        double scale = edge / (double)Math.Max(width, height);
        return new(Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    internal static bool FitsRenderTarget(Size size, int maximum) => size.Width <= maximum && size.Height <= maximum;

    internal static byte[] ResizeRgb(byte[] source, int width, int height, Size destination)
    {
        if (width <= 0 || height <= 0 || destination.Width <= 0 || destination.Height <= 0
            || source.Length != checked(width * height * 3)) throw new ArgumentException("照片尺寸不合法。");
        if (width == destination.Width && height == destination.Height) return source;
        var output = new byte[checked(destination.Width * destination.Height * 3)];
        double sx = width / (double)destination.Width, sy = height / (double)destination.Height;
        for (int y = 0; y < destination.Height; y++)
        {
            double fy = Math.Clamp((y + .5) * sy - .5, 0, height - 1);
            int y0 = (int)fy, y1 = Math.Min(y0 + 1, height - 1); double ty = fy - y0;
            for (int x = 0; x < destination.Width; x++)
            {
                double fx = Math.Clamp((x + .5) * sx - .5, 0, width - 1);
                int x0 = (int)fx, x1 = Math.Min(x0 + 1, width - 1); double tx = fx - x0;
                for (int c = 0; c < 3; c++)
                {
                    double a = source[(y0 * width + x0) * 3 + c] * (1 - tx) + source[(y0 * width + x1) * 3 + c] * tx;
                    double b = source[(y1 * width + x0) * 3 + c] * (1 - tx) + source[(y1 * width + x1) * 3 + c] * tx;
                    output[(y * destination.Width + x) * 3 + c] = (byte)Math.Clamp((int)Math.Round(a * (1 - ty) + b * ty), 0, 255);
                }
            }
        }
        return output;
    }
}
