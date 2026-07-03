using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace pi_camera.Services;

public static class ImageLoader
{
    public static byte[] LoadJpegRgb(string path, out int width, out int height)
    {
        return LoadImageRgb(path, out width, out height);
    }

    public static byte[] LoadImageRgb(string path, out int width, out int height)
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

    public static async Task SaveJpegPreviewAsync(string sourcePath, string destinationPath, int maxSide = 1600, int quality = 82)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");

        using var image = await Image.LoadAsync<Rgb24>(sourcePath);

        var longest = Math.Max(image.Width, image.Height);
        if (longest > maxSide)
        {
            var scale = maxSide / (double)longest;
            var newWidth = Math.Max(1, (int)Math.Round(image.Width * scale));
            var newHeight = Math.Max(1, (int)Math.Round(image.Height * scale));
            image.Mutate(x => x.Resize(newWidth, newHeight));
        }

        await image.SaveAsJpegAsync(destinationPath, new JpegEncoder { Quality = Math.Clamp(quality, 60, 95) });
    }
}

