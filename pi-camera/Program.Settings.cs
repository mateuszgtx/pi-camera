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
    private static double ClampRound(double value, double min, double max)
        => Math.Round(Math.Clamp(value, min, max), 2);



    private static string PaletteModeArg()
    {
        return _paletteMode switch
        {
            PaletteMode.Balanced => "balanced",
            PaletteMode.Green => "green",
            PaletteMode.Yellow => "yellow",
            PaletteMode.Blue => "blue",
            PaletteMode.Red => "red",
            PaletteMode.Cyan => "cyan",
            PaletteMode.Magenta => "magenta",
            PaletteMode.Amber => "amber",
            PaletteMode.Gray => "gray",
            PaletteMode.Warm => "warm",
            PaletteMode.Cold => "cold",
            _ => "green565"
        };
    }

    private static PaletteMode ParsePaletteMode(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "balanced" or "bal" or "even" or "neutral" or "rozproszony" => PaletteMode.Balanced,
            "green" or "zielony" => PaletteMode.Green,
            "yellow" or "zolty" or "żółty" => PaletteMode.Yellow,
            "blue" or "niebieski" => PaletteMode.Blue,
            "red" or "czerwony" => PaletteMode.Red,
            "cyan" => PaletteMode.Cyan,
            "magenta" or "pink" or "rozowy" or "różowy" => PaletteMode.Magenta,
            "amber" or "orange" or "pomarancz" or "pomarańcz" => PaletteMode.Amber,
            "gray" or "grey" or "mono" => PaletteMode.Gray,
            "warm" or "cieply" => PaletteMode.Warm,
            "cold" or "cool" or "zimny" => PaletteMode.Cold,
            _ => PaletteMode.Green565
        };
    }

    private static PaletteMode NextPaletteMode(PaletteMode current, int dir)
    {
        var values = new[]
        {
            PaletteMode.Green565,
            PaletteMode.Balanced,
            PaletteMode.Green,
            PaletteMode.Yellow,
            PaletteMode.Blue,
            PaletteMode.Red,
            PaletteMode.Cyan,
            PaletteMode.Magenta,
            PaletteMode.Amber,
            PaletteMode.Gray,
            PaletteMode.Warm,
            PaletteMode.Cold
        };

        var i = Array.IndexOf(values, current);
        if (i < 0)
            i = 0;

        return values[(i + dir + values.Length) % values.Length];
    }

    private static string PaletteModeLabel(PaletteMode mode)
    {
        return mode switch
        {
            PaletteMode.Green565 => "GREEN565",
            PaletteMode.Balanced => "BALANCED",
            PaletteMode.Green => "GREEN",
            PaletteMode.Yellow => "YELLOW",
            PaletteMode.Blue => "BLUE",
            PaletteMode.Red => "RED",
            PaletteMode.Cyan => "CYAN",
            PaletteMode.Magenta => "MAGENTA",
            PaletteMode.Amber => "AMBER",
            PaletteMode.Gray => "GRAY",
            PaletteMode.Warm => "WARM",
            PaletteMode.Cold => "COLD",
            _ => mode.ToString().ToUpperInvariant()
        };
    }

    private static bool IsMonoPaletteMode(PaletteMode mode)
    {
        return mode is PaletteMode.Green or PaletteMode.Yellow or PaletteMode.Blue or PaletteMode.Red
            or PaletteMode.Cyan or PaletteMode.Magenta or PaletteMode.Amber;
    }

    private static (int R, int G, int B) MonoTint(PaletteMode mode)
    {
        return mode switch
        {
            PaletteMode.Green => (55, 255, 75),
            PaletteMode.Yellow => (255, 225, 45),
            PaletteMode.Blue => (65, 145, 255),
            PaletteMode.Red => (255, 65, 55),
            PaletteMode.Cyan => (55, 245, 255),
            PaletteMode.Magenta => (255, 65, 235),
            PaletteMode.Amber => (255, 145, 25),
            _ => (255, 255, 255)
        };
    }

    private static (int R, int G, int B) ApplyMonoPalette(int r, int g, int b, int palette, PaletteMode mode)
    {
        palette = Math.Clamp(palette, 2, 256);
        var luma = Math.Clamp((r * 30 + g * 59 + b * 11) / 100, 0, 255);
        var idx = (int)Math.Round((luma / 255.0) * (palette - 1));
        var t = idx / (double)(palette - 1);
        t = 0.025 + 0.975 * Math.Pow(t, 0.92);
        var tint = MonoTint(mode);
        return (
            Math.Clamp((int)Math.Round(tint.R * t), 0, 255),
            Math.Clamp((int)Math.Round(tint.G * t), 0, 255),
            Math.Clamp((int)Math.Round(tint.B * t), 0, 255)
        );
    }

    private static int NextColorAmount(int current, int dir)
    {
        current = ClosestColorChoice(current);
        var i = Array.IndexOf(_colorChoices, current);
        if (i < 0)
            i = Array.IndexOf(_colorChoices, 32);

        i = Math.Clamp(i + dir, 0, _colorChoices.Length - 1);
        return _colorChoices[i];
    }

    private static int ClosestColorChoice(int value)
    {
        var best = _colorChoices[0];
        var bestDiff = Math.Abs(value - best);

        foreach (var candidate in _colorChoices)
        {
            var diff = Math.Abs(value - candidate);
            if (diff < bestDiff)
            {
                best = candidate;
                bestDiff = diff;
            }
        }

        return best;
    }

    private static int MaxPixelSizeForCurrentSource()
    {
        return _photoSource == PhotoSource.Preview ? 256 : 2048;
    }

    private static int[] PixelChoicesForCurrentSource()
    {
        var max = MaxPixelSizeForCurrentSource();
        return _pixelChoices.Where(v => v <= max).ToArray();
    }

    private static int NextPixelSize(int current, int dir)
    {
        var choices = PixelChoicesForCurrentSource();
        current = ClosestPixelChoice(current);
        var i = Array.IndexOf(choices, current);
        if (i < 0)
            i = choices.Length - 1;

        i = Math.Clamp(i + dir, 0, choices.Length - 1);
        return choices[i];
    }

    private static int ClosestPixelChoice(int value)
    {
        var choices = PixelChoicesForCurrentSource();
        var best = choices[0];
        var bestDiff = Math.Abs(value - best);

        foreach (var candidate in choices)
        {
            var diff = Math.Abs(value - candidate);
            if (diff < bestDiff)
            {
                best = candidate;
                bestDiff = diff;
            }
        }

        return best;
    }

    private static void SetPreviewColors(int colors)
    {
        _selectedColorAmount = ClosestColorChoice(colors);
        _previewSettings.PreviewColorLevels = _selectedColorAmount;
    }

    private static int NextColorLevel(int current, int dir)
    {
        return NextColorAmount(current, dir);
    }



    private static string NextLookPreset(string current, int dir)
    {
        var values = new[] { "NORMAL", "LOW32", "LOW16", "RETRO8", "MONO4" };
        var i = Array.IndexOf(values, current?.ToUpperInvariant() ?? "LOW32");
        if (i < 0) i = 1;
        return values[(i + dir + values.Length) % values.Length];
    }

    private static void ApplyLookPreset(string preset)
    {
        _lookPreset = (preset ?? "LOW32").ToUpperInvariant();

        switch (_lookPreset)
        {
            case "NORMAL":
                _sensorMode = "full";
                _previewSettings.Ev = -1.2;
                _previewSettings.BlackLevel = 35;
                _previewSettings.DarkLevel = 0.85;
                _previewSettings.PreviewPixelSize = 2048;
                _previewSettings.PreviewColorLevels = 256;
                _previewSettings.Contrast = 0.75;
                _previewSettings.Saturation = 0.85;
                break;
            case "LOW32":
                _sensorMode = "bin";
                _previewSettings.Ev = -1.2;
                _previewSettings.BlackLevel = 25;
                _previewSettings.DarkLevel = 0.95;
                _previewSettings.PreviewPixelSize = 2048;
                _previewSettings.PreviewColorLevels = 32;
                _previewSettings.Contrast = 0.80;
                _previewSettings.Saturation = 0.85;
                break;
            case "LOW16":
                _sensorMode = "fast";
                _previewSettings.Ev = -1.4;
                _previewSettings.BlackLevel = 28;
                _previewSettings.DarkLevel = 0.92;
                _previewSettings.PreviewPixelSize = 2048;
                _previewSettings.PreviewColorLevels = 16;
                _previewSettings.Contrast = 0.85;
                _previewSettings.Saturation = 0.80;
                break;
            case "RETRO8":
                _sensorMode = "fast";
                _previewSettings.Ev = -1.6;
                _previewSettings.BlackLevel = 60;
                _previewSettings.DarkLevel = 0.72;
                _previewSettings.PreviewPixelSize = 2048;
                _previewSettings.PreviewColorLevels = 8;
                _previewSettings.Contrast = 0.90;
                _previewSettings.Saturation = 0.75;
                break;
            case "MONO4":
                _sensorMode = "fast";
                _previewSettings.Ev = -1.6;
                _previewSettings.BlackLevel = 65;
                _previewSettings.DarkLevel = 0.70;
                _previewSettings.PreviewPixelSize = 2048;
                _previewSettings.PreviewColorLevels = 4;
                _previewSettings.Contrast = 0.95;
                _previewSettings.Saturation = 0.0;
                break;
            default:
                _lookPreset = "LOW32";
                ApplyLookPreset(_lookPreset);
                break;
        }
    }


    private static CaptureKind NextCaptureKind(CaptureKind current, int dir)
    {
        var values = new[] { CaptureKind.Photo, CaptureKind.Video, CaptureKind.RandomFrame, CaptureKind.GlitchPhoto, CaptureKind.GlitchVideo, CaptureKind.Stream };
        var i = Array.IndexOf(values, current);
        if (i < 0)
            i = 0;

        return values[(i + dir + values.Length) % values.Length];
    }


    private static string VideoFormatLabel()
    {
        return NormalizeVideoFormat(_videoFormat) == "mp4" ? "MP4" : "MJPEG/AVI";
    }


    private static PhotoSource ParsePhotoSource(string value)
    {
        return value.Equals("preview", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("screen", StringComparison.OrdinalIgnoreCase)
            ? PhotoSource.Preview
            : PhotoSource.FullHq;
    }

    private static string PhotoSourceLabel()
    {
        return _photoSource == PhotoSource.Preview ? "PREVIEW" : "FULL HQ";
    }

    private static PhotoSource NextPhotoSource(PhotoSource current, int dir)
    {
        return current == PhotoSource.FullHq ? PhotoSource.Preview : PhotoSource.FullHq;
    }

    private static string CaptureKindLabel(CaptureKind kind)
    {
        return kind switch
        {
            CaptureKind.Photo => "PHOTO",
            CaptureKind.Video => "VIDEO",
            CaptureKind.RandomFrame => "RANDOM",
            CaptureKind.GlitchPhoto => "GLITCH PHOTO",
            CaptureKind.GlitchVideo => "GLITCH VIDEO",
            CaptureKind.Stream => "STREAM",
            _ => kind.ToString().ToUpperInvariant()
        };
    }

    private static string NextValue(string current, string[] values, int dir)
    {
        var i = Array.IndexOf(values, current);
        if (i < 0) i = 0;
        return values[(i + dir + values.Length) % values.Length];
    }

    private static string SensorLabel(string sensor)
    {
        return sensor switch
        {
            "full" => "FULL 12MP",
            "bin" => "BIN 3MP",
            "fast" => "FAST 1MP",
            _ => sensor.ToUpperInvariant()
        };
    }

    private static string Arg(string[] args, string prefix, string fallback)
        => args.FirstOrDefault(a => a.StartsWith(prefix))?.Split("=", 2)[1] ?? fallback;

    private static int IntArg(string[] args, string prefix, int fallback)
        => int.TryParse(args.FirstOrDefault(a => a.StartsWith(prefix))?.Split("=", 2)[1], out var value) ? value : fallback;

    private static double DoubleArg(string[] args, string prefix, double fallback)
        => double.TryParse(
            args.FirstOrDefault(a => a.StartsWith(prefix))?.Split("=", 2)[1],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
            ? value
            : fallback;

    private static bool BoolArg(string[] args, string prefix, bool fallback)
    {
        var raw = args.FirstOrDefault(a => a.StartsWith(prefix))?.Split("=", 2)[1];

        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        raw = raw.Trim().ToLowerInvariant();

        return raw switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => fallback
        };
    }}
