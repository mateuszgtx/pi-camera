using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace pi_camera.Services;

public static class ImageLoader
{
    public static byte[] LoadJpegRgb(string path, out int width, out int height)
    {
        using var image = Image.Load<Rgb24>(path);

        var localWidth = image.Width;
        var localHeight = image.Height;
        var rgb = new byte[localWidth * localHeight * 3];

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < localHeight; y++)
            {
                var row = accessor.GetRowSpan(y);
                var offset = y * localWidth * 3;

                for (var x = 0; x < localWidth; x++)
                {
                    rgb[offset + x * 3 + 0] = row[x].R;
                    rgb[offset + x * 3 + 1] = row[x].G;
                    rgb[offset + x * 3 + 2] = row[x].B;
                }
            }
        });

        width = localWidth;
        height = localHeight;
        return rgb;
    }
}
