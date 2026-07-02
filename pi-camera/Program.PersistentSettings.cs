using System.Text.Json;
using pi_camera.Services;

namespace pi_camera;

public static partial class Program
{
    private static string DefaultSettingsFilePath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            home = AppContext.BaseDirectory;

        return Path.Combine(home, ".config", "pi-camera", "settings.json");
    }

    private static void LoadPersistentSettingsFromDisk()
    {
        if (string.IsNullOrWhiteSpace(_settingsFilePath) || !File.Exists(_settingsFilePath))
            return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_settingsFilePath));
            ApplyApiSettings(doc.RootElement, preview: null);
            if (TryGetString(doc.RootElement, "webPasswordHash", out var webPasswordHash))
                SetWebPasswordHashFromDisk(webPasswordHash);
            Console.WriteLine($"[SETTINGS] loaded {_settingsFilePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SETTINGS] load failed: {ex.Message}");
        }
    }

    private static void SavePersistentSettingsToDisk()
    {
        if (string.IsNullOrWhiteSpace(_settingsFilePath))
            return;

        try
        {
            var dir = Path.GetDirectoryName(_settingsFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(PersistentSettingsSnapshot(), new JsonSerializerOptions
            {
                WriteIndented = true
            });

            var tmp = _settingsFilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _settingsFilePath, overwrite: true);
            Console.WriteLine($"[SETTINGS] saved {_settingsFilePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SETTINGS] save failed: {ex.Message}");
        }
    }

    private static object PersistentSettingsSnapshot()
    {
        lock (_settingsLock)
        {
            return new
            {
                captureKind = _captureKind.ToString(),
                lookPreset = _lookPreset,
                photoFormat = _photoFormat,
                photoSource = _photoSource.ToString(),
                photoWidth = _photoWidth,
                photoHeight = _photoHeight,
                jpgQuality = _jpgQuality,
                photoEv = _photoEv,
                videoFormat = _videoFormat,
                videoSeconds = _videoSeconds,
                streamUrl = _streamUrl,
                streamOutputFormat = _streamOutputFormat,
                streamFps = _streamFps,
                streamBitrateKbps = _streamBitrateKbps,
                streamJpegQuality = _streamJpegQuality,
                streamUseRaw = _streamUseRaw,
                audioEnabled = _audioEnabled,
                audioInputMode = _audioInputMode.ToString(),
                audioInputFormat = _audioInputFormat,
                audioDevice = _audioDevice,
                audioSampleRate = _audioSampleRate,
                audioBitrateKbps = _audioBitrateKbps,
                previewFps = _previewFps,
                randomFrameMinFps = _randomFrameMinFps,
                randomFrameMaxFps = _randomFrameMaxFps,
                randomFrameSeconds = _randomFrameSeconds,
                glitchStrength = _glitchStrength,
                glitchChangeMs = _glitchChangeMs,
                glitchPaletteEnabled = _glitchPaletteEnabled,
                glitchPixelsEnabled = _glitchPixelsEnabled,
                glitchRgbEnabled = _glitchRgbEnabled,
                glitchPhotoCount = _glitchPhotoCount,
                sensorMode = _sensorMode,
                selectedColorAmount = _selectedColorAmount,
                paletteMode = _paletteMode.ToString(),
                redScale = _redScale,
                greenScale = _greenScale,
                blueScale = _blueScale,
                lowSaveGamma = _lowSaveGamma,
                lowGrayYellowFix = _lowGrayYellowFix,
                webPasswordHash = CurrentWebPasswordHashForSnapshot(),
                preview = new
                {
                    ev = _previewSettings.Ev,
                    sharpness = _previewSettings.Sharpness,
                    contrast = _previewSettings.Contrast,
                    saturation = _previewSettings.Saturation,
                    brightness = _previewSettings.Brightness,
                    blackLevel = _previewSettings.BlackLevel,
                    darkLevel = _previewSettings.DarkLevel,
                    previewPixelSize = _previewSettings.PreviewPixelSize,
                    previewColorLevels = _previewSettings.PreviewColorLevels,
                    denoise = _previewSettings.Denoise
                }
            };
        }
    }

    private static void ResetSettingsToDefaults(CameraPreviewService? preview)
    {
        lock (_settingsLock)
        {
            _captureKind = CaptureKind.Photo;
            _lookPreset = "LOW32";
            _photoFormat = "jpg";
            _photoSource = PhotoSource.FullHq;
            _photoWidth = 4056;
            _photoHeight = 3040;
            _jpgQuality = 95;
            _photoEv = -1.0;
            _videoFormat = "mjpeg";
            _videoSeconds = 0;
            _streamUrl = "";
            _streamOutputFormat = "auto";
            _streamFps = 15;
            _streamBitrateKbps = 2500;
            _streamJpegQuality = 75;
            _streamUseRaw = false;
            _audioEnabled = true;
            _audioInputMode = AudioInputMode.Auto;
            _audioInputFormat = "auto";
            _audioDevice = "";
            _audioSampleRate = 48000;
            _audioBitrateKbps = 128;
            _previewFps = 20;
            _randomFrameMinFps = 1;
            _randomFrameMaxFps = 12;
            _randomFrameSeconds = 10;
            _glitchStrength = 5;
            _glitchChangeMs = 700;
            _glitchPaletteEnabled = true;
            _glitchPixelsEnabled = true;
            _glitchRgbEnabled = true;
            _glitchPhotoCount = 4;
            _manualColorAmount = false;
            _selectedColorAmount = 32;
            _paletteMode = PaletteMode.Green565;
            _redScale = 1.0;
            _greenScale = 1.0;
            _blueScale = 1.0;
            _lowSaveGamma = 0.82;
            _lowGrayYellowFix = 20;

            _previewSettings = new PreviewSettings
            {
                Ev = -1.2,
                Sharpness = 0.1,
                Contrast = 0.80,
                Saturation = 0.85,
                Brightness = 0.0,
                BlackLevel = 25,
                DarkLevel = 0.95,
                PreviewPixelSize = 2048,
                PreviewColorLevels = 32,
                Denoise = "cdn_off"
            };

            _sensorMode = "bin";
        }

        preview?.UpdateSettings(_previewSettings);
    }
}
