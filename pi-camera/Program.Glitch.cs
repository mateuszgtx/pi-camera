using System;
using System.IO;
using System.Threading.Tasks;
using pi_camera.Services;

namespace pi_camera;

public static partial class Program
{
    private static readonly PaletteMode[] _glitchPalettes =
    [
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
    ];

    private static readonly int[] _glitchColorChoices = [2, 4, 8, 16, 32, 64, 128, 256];
    private static readonly int[] _glitchPixelChoices = [1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048];

    private static void SaveGlitchSettingsIfNeeded()
    {
        if (_glitchSavedPreviewSettings is not null)
            return;

        _glitchSavedPreviewSettings = _previewSettings.Clone();
        _glitchSavedPaletteMode = _paletteMode;
        _glitchSavedRedScale = _redScale;
        _glitchSavedGreenScale = _greenScale;
        _glitchSavedBlueScale = _blueScale;
        _glitchSavedSelectedColorAmount = _selectedColorAmount;
    }

    private static void RestoreGlitchSettings()
    {
        if (_glitchSavedPreviewSettings is null)
            return;

        _previewSettings = _glitchSavedPreviewSettings;
        _paletteMode = _glitchSavedPaletteMode;
        _redScale = _glitchSavedRedScale;
        _greenScale = _glitchSavedGreenScale;
        _blueScale = _glitchSavedBlueScale;
        _selectedColorAmount = _glitchSavedSelectedColorAmount;

        _glitchSavedPreviewSettings = null;
    }

    private static void ApplyGlitchOnce()
    {
        lock (_settingsLock)
        {
            SaveGlitchSettingsIfNeeded();
            ApplyRandomGlitchLocked();
        }
    }

    private static async Task ToggleGlitchVideoAsync(string outputDir, FramebufferDisplay display, int width, int height)
    {
        if (_previewRecording)
        {
            StopPreviewRecording(display, width, height, "GLITCH STOP");
            await Task.Delay(100);
            return;
        }

        Directory.CreateDirectory(outputDir);

        lock (_settingsLock)
        {
            SaveGlitchSettingsIfNeeded();
            _glitchVideoRecording = true;
            _glitchNextChangeUtc = DateTime.MinValue;
            ApplyRandomGlitchLocked();
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var basePath = Path.Combine(outputDir, $"GLITCH_{stamp}");

        StartPreviewRecording(basePath, random: false);

        DrawSaved(display, width, height, "GLITCH REC");
        await Task.Delay(100);
    }

    private static void StopGlitchVideoMode()
    {
        lock (_settingsLock)
        {
            _glitchVideoRecording = false;
            _glitchNextChangeUtc = DateTime.MinValue;
            RestoreGlitchSettings();
        }
    }

    private static void MaybeApplyGlitchVideoStep()
    {
        var now = DateTime.UtcNow;
        if (now < _glitchNextChangeUtc)
            return;

        lock (_settingsLock)
        {
            if (!_glitchVideoRecording)
                return;

            ApplyRandomGlitchLocked();
            _glitchNextChangeUtc = DateTime.UtcNow.AddMilliseconds(Math.Clamp(_glitchChangeMs, 100, 5000));
        }
    }

    private static void ApplyRandomGlitchLocked()
    {
        var strength = Math.Clamp(_glitchStrength, 1, 10);
        var t = strength / 10.0;

        if (_glitchPaletteEnabled)
            _paletteMode = _glitchPalettes[_randomFrameRandom.Next(_glitchPalettes.Length)];

        var maxColorIndex = Math.Clamp((int)Math.Round(2 + t * (_glitchColorChoices.Length - 1)), 2, _glitchColorChoices.Length);
        var colorIndex = _randomFrameRandom.Next(0, maxColorIndex);
        SetPreviewColors(_glitchColorChoices[colorIndex]);

        if (_glitchPixelsEnabled)
        {
            // Przy małej sile losujemy głównie wysoką jakość, przy dużej też ciężki pixel-art.
            var minIndex = Math.Clamp(_glitchPixelChoices.Length - 1 - (int)Math.Round(t * (_glitchPixelChoices.Length - 1)), 0, _glitchPixelChoices.Length - 1);
            var pixelIndex = _randomFrameRandom.Next(minIndex, _glitchPixelChoices.Length);
            _previewSettings.PreviewPixelSize = Math.Clamp(_glitchPixelChoices[pixelIndex], 1, MaxPixelSizeForCurrentSource());
        }

        _previewSettings.BlackLevel = Math.Clamp(_randomFrameRandom.Next(0, 15 + strength * 18), 0, 220);
        _previewSettings.DarkLevel = Math.Round(RandomDouble(0.55, 1.0 + strength * 0.15), 2);
        _previewSettings.Contrast = Math.Round(RandomDouble(Math.Max(0.25, 0.8 - strength * 0.05), 1.0 + strength * 0.35), 2);
        _previewSettings.Saturation = Math.Round(RandomDouble(0.0, 0.8 + strength * 0.4), 2);
        _previewSettings.Brightness = Math.Round(RandomDouble(-0.15 * t, 0.10 * t), 2);

        if (_glitchRgbEnabled)
        {
            _redScale = Math.Round(RandomDouble(Math.Max(0.0, 1.0 - 0.18 * strength), 1.0 + 0.22 * strength), 2);
            _greenScale = Math.Round(RandomDouble(Math.Max(0.0, 1.0 - 0.18 * strength), 1.0 + 0.22 * strength), 2);
            _blueScale = Math.Round(RandomDouble(Math.Max(0.0, 1.0 - 0.18 * strength), 1.0 + 0.22 * strength), 2);
        }

        Console.WriteLine($"[GLITCH] strength={strength} palette={_paletteMode} colors={_previewSettings.PreviewColorLevels} pix={_previewSettings.PreviewPixelSize} rgb={_redScale:0.00}/{_greenScale:0.00}/{_blueScale:0.00}");
    }

    private static double RandomDouble(double min, double max)
    {
        if (max < min)
            (min, max) = (max, min);

        return min + _randomFrameRandom.NextDouble() * (max - min);
    }
}
