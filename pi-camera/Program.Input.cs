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
    private static string PinLabel(int pin) => pin >= 0 ? $"GPIO{pin}" : "off";

    private static void AddHardwareButton(
        List<GpioShutterService> buttons,
        HashSet<int> usedPins,
        int pin,
        string name,
        Func<Task> action)
    {
        if (pin < 0)
            return;

        if (!usedPins.Add(pin))
        {
            Console.WriteLine($"[GPIO {name}] GPIO{pin} is already in use, skipping");
            return;
        }

        var button = new GpioShutterService(pin, stablePressMs: 25, cooldownMs: 120, pollMs: 8);
        button.ShutterPressed += action;
        button.StatusChanged += msg => Console.WriteLine($"[GPIO {name}] " + msg);
        buttons.Add(button);
        button.Start();
    }

    private static Task HandleHardwareButtonAsync(string action, CameraPreviewService preview, FramebufferDisplay display, int width, int height, string outputDir)
    {
        if (DateTime.UtcNow < _ignoreTouchUntilUtc)
            return Task.CompletedTask;

        _ignoreTouchUntilUtc = DateTime.UtcNow.AddMilliseconds(90);

        try
        {
            switch (action)
            {
                case "tab":
                    SwitchToNextTab(preview, display, width, height, outputDir);
                    break;

                case "mode":
                    if (_tab == Tab.Mode)
                    {
                        _modeRow = (_modeRow + 1) % ModeRowCount(_modePage);
                        DrawMode(display, width, height);
                    }
                    else if (_tab == Tab.Network)
                    {
                        _networkRow = (_networkRow + 1) % NetworkRowCount();
                        DrawNetwork(display, width, height);
                    }
                    else
                    {
                        SwitchToTab(Tab.Mode, preview, display, width, height, outputDir);
                    }
                    break;

                case "gallery":
                    SwitchToTab(Tab.Gallery, preview, display, width, height, outputDir);
                    break;

                case "video":
                    _captureKind = CaptureKind.Video;
                    _captureRequested = true;
                    break;

                case "prev":
                    HandleHardwarePrevNext(-1, preview, display, width, height, outputDir);
                    break;

                case "next":
                    HandleHardwarePrevNext(1, preview, display, width, height, outputDir);
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[GPIO BUTTON] " + ex.Message);
        }

        return Task.CompletedTask;
    }

    private static void SwitchToNextTab(CameraPreviewService preview, FramebufferDisplay display, int width, int height, string outputDir)
    {
        var next = _tab switch
        {
            Tab.Preview => Tab.Mode,
            Tab.Mode => Tab.Gallery,
            Tab.Gallery => Tab.Network,
            _ => Tab.Preview
        };

        SwitchToTab(next, preview, display, width, height, outputDir);
    }

    private static void SwitchToTab(Tab tab, CameraPreviewService preview, FramebufferDisplay display, int width, int height, string outputDir)
    {
        _tab = tab;
        _previewReadyForTouch = false;

        if (tab == Tab.Preview)
        {
            StartPreviewSafe(preview);
            return;
        }

        preview.Stop();
        RedrawNonPreview(display, width, height, outputDir);
    }

    private static void HandleHardwarePrevNext(int dir, CameraPreviewService preview, FramebufferDisplay display, int width, int height, string outputDir)
    {
        if (_tab == Tab.Preview)
        {
            _paletteMode = NextPaletteMode(_paletteMode, dir);
            preview.UpdateSettings(_previewSettings);
            return;
        }

        if (_tab == Tab.Mode)
        {
            HandleModePrimaryButton(dir, display, width, height);
            preview.UpdateSettings(_previewSettings);
            return;
        }

        if (_tab == Tab.Gallery)
        {
            RefreshGallery(outputDir);
            if (_galleryFiles.Count > 0)
                _galleryIndex = Math.Clamp(_galleryIndex + dir, 0, _galleryFiles.Count - 1);
            DrawGallery(display, width, height, outputDir);
            return;
        }

        if (_tab == Tab.Network)
        {
            HandleNetworkPrimaryButton(dir, display, width, height);
            return;
        }
    }

    private static int ModePageCount() => 8;

    private static int ModeRowCount(int page) => page switch
    {
        0 => 4,
        1 => 5,
        2 => 5,
        3 => 5,
        4 => 6,
        5 => 6,
        _ => 6
    };

    private static void NextModePage(int dir = 1)
    {
        _modePage = (_modePage + dir + ModePageCount()) % ModePageCount();
        _modeRow = 0;
    }

    private static bool IsActiveModeRow(int row) => _modeRow == row;

    private static void HandleModePrimaryButton(int dir, FramebufferDisplay display, int width, int height)
    {
        var lastRow = ModeRowCount(_modePage) - 1;
        if (_modeRow == lastRow)
        {
            NextModePage(dir >= 0 ? 1 : -1);
            DrawMode(display, width, height);
            return;
        }

        switch (_modePage)
        {
            case 0:
                if (_modeRow == 0)
                {
                    _selectedColorAmount = NextColorAmount(_selectedColorAmount, dir);
                    SetPreviewColors(_selectedColorAmount);
                    _manualColorAmount = true;
                }
                else if (_modeRow == 1) _paletteMode = NextPaletteMode(_paletteMode, dir);
                else if (_modeRow == 2) _previewSettings.PreviewPixelSize = NextPixelSize(_previewSettings.PreviewPixelSize, dir);
                break;
            case 1:
                if (_modeRow == 0) _redScale = ClampRound(_redScale + dir * 0.1, 0.0, 2.0);
                else if (_modeRow == 1) _greenScale = ClampRound(_greenScale + dir * 0.1, 0.0, 2.0);
                else if (_modeRow == 2) _blueScale = ClampRound(_blueScale + dir * 0.1, 0.0, 2.0);
                else if (_modeRow == 3) { _redScale = 1.0; _greenScale = 1.0; _blueScale = 1.0; }
                break;
            case 2:
                if (_modeRow == 0)
                {
                    _photoSource = NextPhotoSource(_photoSource, dir);
                    if (_photoSource == PhotoSource.Preview && _previewSettings.PreviewPixelSize > 256)
                        _previewSettings.PreviewPixelSize = 256;
                }
                else if (_modeRow == 1) _photoFormat = NextValue(_photoFormat, new[] { "jpg", "png", "bmp", "raw", "rawjpg" }, dir);
                else if (_modeRow == 2) _photoWidth = Math.Clamp(_photoWidth + dir * 16, 320, 4056);
                else if (_modeRow == 3) _photoHeight = Math.Clamp(_photoHeight + dir * 16, 240, 3040);
                break;
            case 3:
                if (_modeRow == 0) _captureKind = NextCaptureKind(_captureKind, dir);
                else if (_modeRow == 1) _videoFormat = NextValue(_videoFormat, new[] { "mjpeg", "mp4" }, dir);
                else if (_modeRow == 2) _sensorMode = NextValue(_sensorMode, new[] { "full", "bin", "fast" }, dir);
                else if (_modeRow == 3) _photoEv = Math.Round(Math.Clamp(_photoEv + dir * 0.5, -8.0, 8.0), 1);
                break;
            case 4:
                if (_modeRow == 0) { _randomFrameMinFps = Math.Clamp(_randomFrameMinFps + dir, 1, 30); if (_randomFrameMinFps > _randomFrameMaxFps) _randomFrameMaxFps = _randomFrameMinFps; }
                else if (_modeRow == 1) { _randomFrameMaxFps = Math.Clamp(_randomFrameMaxFps + dir, 1, 30); if (_randomFrameMaxFps < _randomFrameMinFps) _randomFrameMinFps = _randomFrameMaxFps; }
                else if (_modeRow == 2) _randomFrameSeconds = Math.Clamp(_randomFrameSeconds + dir, 1, 15);
                else if (_modeRow == 3) _videoFormat = NextValue(_videoFormat, new[] { "mjpeg", "mp4" }, dir);
                else if (_modeRow == 4) _sensorMode = NextValue(_sensorMode, new[] { "full", "bin", "fast" }, dir);
                break;
            case 5:
                if (_modeRow == 0) _glitchStrength = Math.Clamp(_glitchStrength + dir, 1, 10);
                else if (_modeRow == 1) _glitchChangeMs = Math.Clamp(_glitchChangeMs + dir * 100, 100, 5000);
                else if (_modeRow == 2) _glitchPaletteEnabled = !_glitchPaletteEnabled;
                else if (_modeRow == 3) _glitchPixelsEnabled = !_glitchPixelsEnabled;
                else if (_modeRow == 4) _glitchRgbEnabled = !_glitchRgbEnabled;
                break;
            case 6:
                if (_modeRow == 0) _previewSettings.Ev = ClampRound(_previewSettings.Ev + dir * 0.1, -8.0, 8.0);
                else if (_modeRow == 1) _previewSettings.BlackLevel = Math.Clamp(_previewSettings.BlackLevel + dir * 5, 0, 240);
                else if (_modeRow == 2) _previewSettings.DarkLevel = ClampRound(_previewSettings.DarkLevel + dir * 0.05, 0.25, 2.0);
                else if (_modeRow == 3) _previewSettings.PreviewPixelSize = NextPixelSize(_previewSettings.PreviewPixelSize, dir);
                else if (_modeRow == 4) { _selectedColorAmount = NextColorAmount(_selectedColorAmount, dir); SetPreviewColors(_selectedColorAmount); _manualColorAmount = true; }
                break;
            case 7:
                if (_modeRow == 0) _previewSettings.Contrast = ClampRound(_previewSettings.Contrast + dir * 0.1, 0.0, 32.0);
                else if (_modeRow == 1) _previewSettings.Saturation = ClampRound(_previewSettings.Saturation + dir * 0.1, 0.0, 32.0);
                else if (_modeRow == 2) _previewSettings.Brightness = ClampRound(_previewSettings.Brightness + dir * 0.05, -1.0, 1.0);
                else if (_modeRow == 3) _previewSettings.Sharpness = ClampRound(_previewSettings.Sharpness + dir * 0.1, 0.0, 16.0);
                else if (_modeRow == 4) _jpgQuality = Math.Clamp(_jpgQuality + dir * 5, 70, 100);
                break;
        }

        DrawMode(display, width, height);
    }

    private static void HandleTouch(int x, int y, int width, int height, CameraPreviewService preview, FramebufferDisplay display, string outputDir)
    {
        if (DateTime.UtcNow < _ignoreTouchUntilUtc)
            return;

        if (HandleTabTouch(x, y, width, height, preview, display, outputDir))
            return;

        if (_tab == Tab.Preview)
        {
            if (_touchCaptureEnabled && _previewReadyForTouch && y >= 28 && y < height - 44)
                RequestPhotoCapture("touch");
            return;
        }

        if (_tab == Tab.Mode)
        {
            HandleModeTouch(x, y, width, height, display);
            return;
        }

        if (_tab == Tab.Gallery)
        {
            HandleGalleryTouch(x, y, width, height, display, outputDir);
            return;
        }

        if (_tab == Tab.Network)
        {
            HandleNetworkTouch(x, y, width, height, display);
            return;
        }
    }

    private static bool HandleTabTouch(int x, int y, int width, int height, CameraPreviewService preview, FramebufferDisplay display, string outputDir)
    {
        if (y < height - 44)
            return false;

        var part = width / 4;
        _previewReadyForTouch = false;
        _ignoreTouchUntilUtc = DateTime.UtcNow.AddMilliseconds(700);

        if (x < part)
        {
            _tab = Tab.Preview;
            StartPreviewSafe(preview);
        }
        else if (x < part * 2)
        {
            _tab = Tab.Mode;
            preview.Stop();
            DrawMode(display, width, height);
        }
        else if (x < part * 3)
        {
            _tab = Tab.Gallery;
            preview.Stop();
            DrawGallery(display, width, height, outputDir);
        }
        else
        {
            _tab = Tab.Network;
            preview.Stop();
            DrawNetwork(display, width, height);
        }

        return true;
    }

    private static void HandleModeTouch(int x, int y, int width, int height, FramebufferDisplay display)
    {
        if (y >= height - 44 - 38 && y < height - 44)
        {
            NextModePage();
            DrawMode(display, width, height);
            return;
        }

        var dir = x < width / 2 ? -1 : 1;
        var row = Math.Clamp((y - 58) / 34, 0, ModeRowCount(_modePage) - 2);
        _modeRow = row;
        HandleModePrimaryButton(dir, display, width, height);
    }

    private static void HandleGalleryTouch(int x, int y, int width, int height, FramebufferDisplay display, string outputDir)
    {
        if (y < 48)
        {
            if (x < 120)
                _galleryIndex = Math.Max(0, _galleryIndex - 1);
            else if (x > width - 120)
                _galleryIndex = Math.Min(Math.Max(0, _galleryFiles.Count - 1), _galleryIndex + 1);

            DrawGallery(display, width, height, outputDir);
        }
    }

    private static void HandleNetworkTouch(int x, int y, int width, int height, FramebufferDisplay display)
    {
        if (y >= height - 44 - 38 && y < height - 44)
        {
            _networkPage = (_networkPage + 1) % NetworkPageCount();
            _networkRow = 0;
            DrawNetwork(display, width, height);
            return;
        }

        var rowsWithoutPageButton = NetworkRowCount() - 1;
        var row = (y - 64) / 34;
        if (row < 0 || row >= rowsWithoutPageButton)
            return;

        _networkRow = row;
        HandleNetworkPrimaryButton(x < width / 2 ? -1 : 1, display, width, height);
    }

    private static void KeyboardLoop(CameraPreviewService preview, FramebufferDisplay display, int width, int height, string outputDir)
    {
        while (_running)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter || key.Key == ConsoleKey.Spacebar)
                RequestPhotoCapture("keyboard");

            if (key.Key == ConsoleKey.D1)
            {
                _tab = Tab.Preview;
                StartPreviewSafe(preview);
            }

            if (key.Key == ConsoleKey.D2)
            {
                _tab = Tab.Mode;
                preview.Stop();
                DrawMode(display, width, height);
            }

            if (key.Key == ConsoleKey.D3)
            {
                _tab = Tab.Gallery;
                preview.Stop();
                DrawGallery(display, width, height, outputDir);
            }

            if (key.Key == ConsoleKey.D4)
            {
                _tab = Tab.Network;
                preview.Stop();
                DrawNetwork(display, width, height);
            }

            if (key.Key == ConsoleKey.LeftArrow && _tab == Tab.Gallery)
            {
                _galleryIndex = Math.Max(0, _galleryIndex - 1);
                DrawGallery(display, width, height, outputDir);
            }

            if (key.Key == ConsoleKey.RightArrow && _tab == Tab.Gallery)
            {
                _galleryIndex = Math.Min(Math.Max(0, _galleryFiles.Count - 1), _galleryIndex + 1);
                DrawGallery(display, width, height, outputDir);
            }

            if (key.Key == ConsoleKey.Q || key.Key == ConsoleKey.Escape)
                _running = false;
        }
    }

}
