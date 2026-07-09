using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using pi_camera.Services;

using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp;
namespace pi_camera;


public static partial class Program
{
    private static bool ShouldSavePreviewLook()
    {
        if (_lookPreset != "NORMAL")
            return true;

        if (_previewSettings.PreviewPixelSize > 1)
            return true;

        if (_previewSettings.PreviewColorLevels < 256)
            return true;

        if (_previewSettings.Saturation <= 0.01)
            return true;

        return false;
    }



    private static int FullHqPixelBlockSize()
    {
        // Od teraz PIKSELE działa jak jakość/szczegół:
        // 2048 = najmniejsze bloki / najlepsza jakość,
        // 1 = największe bloki / najmocniejszy pixel-art.
        var detail = Math.Clamp(_previewSettings.PreviewPixelSize, 1, 2048);
        return Math.Clamp((int)Math.Round(2048.0 / detail), 1, 2048);
    }

    private static int PreviewPixelBlockSize()
    {
        var detail = Math.Clamp(_previewSettings.PreviewPixelSize, 1, 256);
        return Math.Clamp((int)Math.Round(256.0 / detail), 1, 256);
    }


    private static int EffectivePreviewPixelSize(int srcW)
    {
        // PIKSELE / JAKOŚĆ działa odwrotnie niż rozmiar bloku:
        // 2048 = najlepsza jakość, 1 = największe bloki.
        // Dla podglądu liczymy rozmiar bloku po przeskalowaniu z Full HQ.
        if (_photoSource == PhotoSource.FullHq)
        {
            var photoW = Math.Max(1, _photoWidth);
            var fullBlock = FullHqPixelBlockSize(); // np. 2048 -> 1, 1024 -> 2, 256 -> 8
            var scaled = Math.Max(1, (int)Math.Round(fullBlock * (srcW / (double)photoW)));
            return Math.Clamp(scaled, 1, _livePreviewPixelMax);
        }

        return PreviewPixelBlockSize();
    }


    private static (int R, int G, int B) ApplyColorScale(int r, int g, int b)
    {
        r = Math.Clamp((int)Math.Round(r * _redScale), 0, 255);
        g = Math.Clamp((int)Math.Round(g * _greenScale), 0, 255);
        b = Math.Clamp((int)Math.Round(b * _blueScale), 0, 255);
        return (r, g, b);
    }

    private static bool IsLowPaletteLook()
    {
        return _lookPreset == "LOW32" || _lookPreset == "LOW16";
    }

    private static (int R, int G, int B) ApplyLowSaveCorrection(int r, int g, int b)
    {
        if (!IsLowPaletteLook())
            return (r, g, b);

        r = ApplyGamma(r, _lowSaveGamma);
        g = ApplyGamma(g, _lowSaveGamma);
        b = ApplyGamma(b, _lowSaveGamma);

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));

        if (max - min < 42)
        {
            var avg = (r + g + b) / 3;
            r = Math.Clamp((r + avg) / 2, 0, 255);
            g = Math.Clamp(((g + avg) / 2) - _lowGrayYellowFix / 3, 0, 255);
            b = Math.Clamp(((b + avg) / 2) + _lowGrayYellowFix, 0, 255);
        }

        return (r, g, b);
    }

    private static int ApplyGamma(int value, double gamma)
    {
        value = Math.Clamp(value, 0, 255);
        gamma = Math.Clamp(gamma, 0.35, 2.5);
        return Math.Clamp((int)(Math.Pow(value / 255.0, gamma) * 255.0), 0, 255);
    }


    private static void ApplyFullPhotoLook(Image<Rgb24> image)
    {
        var pixel = FullHqPixelBlockSize();
        var colors = Math.Clamp(_previewSettings.PreviewColorLevels, 2, 256);
        var black = Math.Clamp(_previewSettings.BlackLevel, 0, 240);
        var dark = Math.Clamp(_previewSettings.DarkLevel, 0.25, 2.0);
        var denom = Math.Max(1, 255 - black);

        var block = Math.Clamp(pixel, 1, Math.Max(image.Width, image.Height));

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < image.Height; y += block)
            {
                for (var x = 0; x < image.Width; x += block)
                {
                    var sampleX = Math.Min(image.Width - 1, x + block / 2);
                    var sampleY = Math.Min(image.Height - 1, y + block / 2);
                    var sample = accessor.GetRowSpan(sampleY)[sampleX];

                    var r0 = ApplyBlackDarkSaved(sample.R, black, denom, dark);
                    var g0 = ApplyBlackDarkSaved(sample.G, black, denom, dark);
                    var b0 = ApplyBlackDarkSaved(sample.B, black, denom, dark);

                    (r0, g0, b0) = ApplyColorScale(r0, g0, b0);

                    var (r, g, b) = QuantizeSavedPalette(r0, g0, b0, colors);
                    (r, g, b) = ApplyLowSaveCorrection(r, g, b);

                    if (_previewSettings.Saturation <= 0.01)
                    {
                        var gray = Math.Clamp((r * 30 + g * 59 + b * 11) / 100, 0, 255);
                        r = gray;
                        g = gray;
                        b = gray;
                    }

                    var color = new Rgb24((byte)r, (byte)g, (byte)b);
                    var maxY = Math.Min(image.Height, y + block);
                    var maxX = Math.Min(image.Width, x + block);

                    for (var yy = y; yy < maxY; yy++)
                    {
                        var row = accessor.GetRowSpan(yy);
                        for (var xx = x; xx < maxX; xx++)
                            row[xx] = color;
                    }
                }
            }
        });

        ApplyVhsEffectToImage(image);
    }

    private static bool TrySaveCurrentPreviewFrame(string outputPath, string format)
    {
        byte[]? rgb;
        int srcW;
        int srcH;

        lock (_lastPreviewLock)
        {
            if (_lastPreviewRgb is null || _lastPreviewWidth <= 0 || _lastPreviewHeight <= 0)
                return false;

            rgb = _lastPreviewRgb.ToArray();
            srcW = _lastPreviewWidth;
            srcH = _lastPreviewHeight;
        }

        using var image = new Image<Rgb24>(srcW, srcH);

        var pixel = EffectivePreviewPixelSize(srcW);
        var colors = Math.Clamp(_previewSettings.PreviewColorLevels, 2, 256);
        var black = Math.Clamp(_previewSettings.BlackLevel, 0, 240);
        var dark = Math.Clamp(_previewSettings.DarkLevel, 0.25, 2.0);
        var denom = Math.Max(1, 255 - black);

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < srcH; y += pixel)
            {
                for (var x = 0; x < srcW; x += pixel)
                {
                    var sampleX = Math.Min(srcW - 1, x + pixel / 2);
                    var sampleY = Math.Min(srcH - 1, y + pixel / 2);
                    var i = (sampleY * srcW + sampleX) * 3;

                    var r0 = ApplyBlackDarkSaved(rgb[i], black, denom, dark);
                    var g0 = ApplyBlackDarkSaved(rgb[i + 1], black, denom, dark);
                    var b0 = ApplyBlackDarkSaved(rgb[i + 2], black, denom, dark);

                    (r0, g0, b0) = ApplyColorScale(r0, g0, b0);
                    var (r, g, b) = QuantizeSavedPalette(r0, g0, b0, colors);
                    (r, g, b) = ApplyLowSaveCorrection(r, g, b);

                    if (_previewSettings.Saturation <= 0.01)
                    {
                        var gray = Math.Clamp((r * 30 + g * 59 + b * 11) / 100, 0, 255);
                        r = gray;
                        g = gray;
                        b = gray;
                    }

                    var color = new Rgb24((byte)r, (byte)g, (byte)b);
                    var maxY = Math.Min(srcH, y + pixel);
                    var maxX = Math.Min(srcW, x + pixel);

                    for (var yy = y; yy < maxY; yy++)
                    {
                        var row = accessor.GetRowSpan(yy);
                        for (var xx = x; xx < maxX; xx++)
                            row[xx] = color;
                    }
                }
            }
        });

        ApplyVhsEffectToImage(image);

        if (format == "png")
            image.SaveAsPng(outputPath);
        else if (format == "bmp")
            image.SaveAsBmp(outputPath);
        else
            image.SaveAsJpeg(outputPath, new JpegEncoder { Quality = _jpgQuality });

        return true;
    }

    private static void ApplySavedLookEffect(string inputPath, string outputPath, string format, PreviewSettings settings, int jpgQuality)
    {
        using var image = Image.Load<Rgb24>(inputPath);

        var pixel = Math.Clamp((int)Math.Round(2048.0 / Math.Clamp(settings.PreviewPixelSize, 1, 2048)), 1, 2048);
        var colors = Math.Clamp(settings.PreviewColorLevels, 2, 256);
        var black = Math.Clamp(settings.BlackLevel, 0, 240);
        var dark = Math.Clamp(settings.DarkLevel, 0.25, 2.0);
        var denom = Math.Max(1, 255 - black);

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < image.Height; y += pixel)
            {
                for (var x = 0; x < image.Width; x += pixel)
                {
                    var sampleX = Math.Min(image.Width - 1, x + pixel / 2);
                    var sampleY = Math.Min(image.Height - 1, y + pixel / 2);
                    var sampleRow = accessor.GetRowSpan(sampleY);
                    var sample = sampleRow[sampleX];

                    var r0 = ApplyBlackDarkSaved(sample.R, black, denom, dark);
                    var g0 = ApplyBlackDarkSaved(sample.G, black, denom, dark);
                    var b0 = ApplyBlackDarkSaved(sample.B, black, denom, dark);

                    (r0, g0, b0) = ApplyColorScale(r0, g0, b0);
                    var (r, g, b) = QuantizeSavedPalette(r0, g0, b0, colors);
                    (r, g, b) = ApplyLowSaveCorrection(r, g, b);

                    if (settings.Saturation <= 0.01)
                    {
                        var gray = Math.Clamp((r * 30 + g * 59 + b * 11) / 100, 0, 255);
                        r = gray;
                        g = gray;
                        b = gray;
                    }

                    var color = new Rgb24((byte)r, (byte)g, (byte)b);

                    var maxY = Math.Min(image.Height, y + pixel);
                    var maxX = Math.Min(image.Width, x + pixel);

                    for (var yy = y; yy < maxY; yy++)
                    {
                        var row = accessor.GetRowSpan(yy);
                        for (var xx = x; xx < maxX; xx++)
                            row[xx] = color;
                    }
                }
            }
        });

        ApplyVhsEffectToImage(image);

        if (format == "png")
            image.SaveAsPng(outputPath);
        else if (format == "bmp")
            image.SaveAsBmp(outputPath);
        else
            image.SaveAsJpeg(outputPath, new JpegEncoder { Quality = jpgQuality });
    }



    private static int ApplyBlackDarkSaved(byte value, int blackLevel, int denom, double darkLevel)
    {
        var v = value - blackLevel;
        if (v <= 0)
            return 0;

        return Math.Clamp((int)((v * 255 / denom) * darkLevel), 0, 255);
    }

    private static (int R, int G, int B) QuantizeSavedPalette(int r, int g, int b, int palette)
    {
        if (palette >= 256 && !IsMonoPaletteMode(_paletteMode))
            return (r, g, b);

        if (IsMonoPaletteMode(_paletteMode))
            return ApplyMonoPalette(r, g, b, palette, _paletteMode);

        if (_paletteMode == PaletteMode.Gray)
        {
            var gray = (r * 30 + g * 59 + b * 11) / 100;
            var levels = palette <= 16 ? Math.Max(2, palette) : 16;
            var q = QuantizeSavedChannel(gray, levels);
            return (q, q, q);
        }

        if (_paletteMode == PaletteMode.Balanced)
        {
            r = Math.Clamp((int)Math.Round(r * 1.08), 0, 255);
            g = Math.Clamp((int)Math.Round(g * 0.88), 0, 255);
            b = Math.Clamp((int)Math.Round(b * 1.08), 0, 255);
        }

        if (_paletteMode == PaletteMode.Warm)
        {
            r = Math.Clamp((int)(r * 1.15), 0, 255);
            b = Math.Clamp((int)(b * 0.82), 0, 255);
        }
        else if (_paletteMode == PaletteMode.Cold)
        {
            r = Math.Clamp((int)(r * 0.82), 0, 255);
            b = Math.Clamp((int)(b * 1.18), 0, 255);
        }

        if (_paletteMode == PaletteMode.Green565)
        {
            if (palette <= 4)
            {
                var gray = (r * 30 + g * 59 + b * 11) / 100;
                var q = QuantizeSavedChannel(gray, palette);
                return (q, q, q);
            }
            if (palette <= 8) return (QuantizeSavedChannel(r, 2), QuantizeSavedChannel(g, 2), QuantizeSavedChannel(b, 2));
            if (palette <= 16) return (QuantizeSavedChannel(r, 2), QuantizeSavedChannel(g, 4), QuantizeSavedChannel(b, 2));
            if (palette <= 32) return (QuantizeSavedChannel(r, 4), QuantizeSavedChannel(g, 4), QuantizeSavedChannel(b, 2));
            if (palette <= 64) return (QuantizeSavedChannel(r, 4), QuantizeSavedChannel(g, 4), QuantizeSavedChannel(b, 4));
            return (QuantizeSavedChannel(r, 6), QuantizeSavedChannel(g, 7), QuantizeSavedChannel(b, 6));
        }

        if (palette <= 4)
        {
            var gray = (r * 30 + g * 59 + b * 11) / 100;
            var q = QuantizeSavedChannel(gray, palette);
            return (q, q, q);
        }
        if (palette <= 8) return (QuantizeSavedChannel(r, 2), QuantizeSavedChannel(g, 2), QuantizeSavedChannel(b, 2));
        if (palette <= 16) return (QuantizeSavedChannel(r, 2), QuantizeSavedChannel(g, 2), QuantizeSavedChannel(b, 4));
        if (palette <= 32) return (QuantizeSavedChannel(r, 4), QuantizeSavedChannel(g, 2), QuantizeSavedChannel(b, 4));
        if (palette <= 64) return (QuantizeSavedChannel(r, 4), QuantizeSavedChannel(g, 4), QuantizeSavedChannel(b, 4));
        return (QuantizeSavedChannel(r, 5), QuantizeSavedChannel(g, 5), QuantizeSavedChannel(b, 5));
    }





    private static int QuantizeSavedChannel(int value, int levels)
    {
        if (levels >= 256)
            return value;

        if (levels <= 2)
            return value < 128 ? 0 : 255;

        var step = 255.0 / (levels - 1);
        return Math.Clamp((int)(Math.Round(value / step) * step), 0, 255);
    }



    private static byte[] EncodePreviewFrameJpeg(byte[] rgb, int srcW, int srcH)
    {
        using var image = new Image<Rgb24>(srcW, srcH);

        var pixel = EffectivePreviewPixelSize(srcW);
        var colors = Math.Clamp(_previewSettings.PreviewColorLevels, 2, 256);
        var black = Math.Clamp(_previewSettings.BlackLevel, 0, 240);
        var dark = Math.Clamp(_previewSettings.DarkLevel, 0.25, 2.0);
        var denom = Math.Max(1, 255 - black);

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < srcH; y += pixel)
            {
                for (var x = 0; x < srcW; x += pixel)
                {
                    var sampleX = Math.Min(srcW - 1, x + pixel / 2);
                    var sampleY = Math.Min(srcH - 1, y + pixel / 2);
                    var i = (sampleY * srcW + sampleX) * 3;

                    var r0 = ApplyBlackDarkSaved(rgb[i], black, denom, dark);
                    var g0 = ApplyBlackDarkSaved(rgb[i + 1], black, denom, dark);
                    var b0 = ApplyBlackDarkSaved(rgb[i + 2], black, denom, dark);

                    (r0, g0, b0) = ApplyColorScale(r0, g0, b0);

                    var (r, g, b) = QuantizeSavedPalette(r0, g0, b0, colors);
                    (r, g, b) = ApplyLowSaveCorrection(r, g, b);

                    if (_previewSettings.Saturation <= 0.01)
                    {
                        var gray = Math.Clamp((r * 30 + g * 59 + b * 11) / 100, 0, 255);
                        r = gray;
                        g = gray;
                        b = gray;
                    }

                    var color = new Rgb24((byte)r, (byte)g, (byte)b);
                    var maxY = Math.Min(srcH, y + pixel);
                    var maxX = Math.Min(srcW, x + pixel);

                    for (var yy = y; yy < maxY; yy++)
                    {
                        var row = accessor.GetRowSpan(yy);
                        for (var xx = x; xx < maxX; xx++)
                            row[xx] = color;
                    }
                }
            }
        });

        ApplyVhsEffectToImage(image);

        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms, new JpegEncoder { Quality = Math.Clamp(_jpgQuality, 50, 95) });
        return ms.ToArray();
    }


    private static byte[]? CreatePreviewJpeg(bool raw, int quality)
    {
        byte[]? rgb;
        int srcW;
        int srcH;

        lock (_lastPreviewLock)
        {
            if (_lastPreviewRgb is null || _lastPreviewWidth <= 0 || _lastPreviewHeight <= 0)
                return null;

            rgb = _lastPreviewRgb.ToArray();
            srcW = _lastPreviewWidth;
            srcH = _lastPreviewHeight;
        }

        using var image = new Image<Rgb24>(srcW, srcH);

        if (raw)
        {
            image.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < srcH; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    var offset = y * srcW * 3;
                    for (var x = 0; x < srcW; x++)
                        row[x] = new Rgb24(rgb[offset + x * 3], rgb[offset + x * 3 + 1], rgb[offset + x * 3 + 2]);
                }
            });
        }
        else
        {
            FillImageWithCurrentLook(image, rgb, srcW, srcH);
        }

        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms, new JpegEncoder { Quality = Math.Clamp(quality, 35, 95) });
        return ms.ToArray();
    }

    private static void FillImageWithCurrentLook(Image<Rgb24> image, byte[] rgb, int srcW, int srcH)
    {
        var pixel = EffectivePreviewPixelSize(srcW);
        var colors = Math.Clamp(_previewSettings.PreviewColorLevels, 2, 256);
        var black = Math.Clamp(_previewSettings.BlackLevel, 0, 240);
        var dark = Math.Clamp(_previewSettings.DarkLevel, 0.25, 2.0);
        var denom = Math.Max(1, 255 - black);

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < srcH; y += pixel)
            {
                for (var x = 0; x < srcW; x += pixel)
                {
                    var sampleX = Math.Min(srcW - 1, x + pixel / 2);
                    var sampleY = Math.Min(srcH - 1, y + pixel / 2);
                    var i = (sampleY * srcW + sampleX) * 3;

                    var r0 = ApplyBlackDarkSaved(rgb[i], black, denom, dark);
                    var g0 = ApplyBlackDarkSaved(rgb[i + 1], black, denom, dark);
                    var b0 = ApplyBlackDarkSaved(rgb[i + 2], black, denom, dark);

                    (r0, g0, b0) = ApplyColorScale(r0, g0, b0);
                    var (r, g, b) = QuantizeSavedPalette(r0, g0, b0, colors);
                    (r, g, b) = ApplyLowSaveCorrection(r, g, b);

                    if (_previewSettings.Saturation <= 0.01)
                    {
                        var gray = Math.Clamp((r * 30 + g * 59 + b * 11) / 100, 0, 255);
                        r = gray;
                        g = gray;
                        b = gray;
                    }

                    var color = new Rgb24((byte)r, (byte)g, (byte)b);
                    var maxY = Math.Min(srcH, y + pixel);
                    var maxX = Math.Min(srcW, x + pixel);

                    for (var yy = y; yy < maxY; yy++)
                    {
                        var row = accessor.GetRowSpan(yy);
                        for (var xx = x; xx < maxX; xx++)
                            row[xx] = color;
                    }
                }
            }
        });

        ApplyVhsEffectToImage(image);
    }

}
