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
    private enum Tab
    {
        Preview,
        Mode,
        Gallery,
        Network
    }

    private enum CaptureKind
    {
        Photo,
        Video,
        RandomFrame
    }

    private enum PhotoSource
    {
        FullHq,
        Preview
    }

    private enum PaletteMode
    {
        Green565,
        Balanced,
        Green,
        Yellow,
        Blue,
        Red,
        Cyan,
        Magenta,
        Amber,
        Gray,
        Warm,
        Cold
    }

    private static volatile bool _running = true;
    private static volatile bool _captureRequested;
    private static volatile bool _isBusy;
    private static volatile bool _previewReadyForTouch;
    private static DateTime _ignoreTouchUntilUtc = DateTime.UtcNow.AddSeconds(2);
    private static DateTime _previewStartUtc = DateTime.UtcNow;

    private static Tab _tab = Tab.Preview;

    private static string _lookPreset = "LOW32";
    private static int _modePage;
    private static int _modeRow;
    private static int _galleryIndex;
    private static List<string> _galleryFiles = new();

    private static CaptureKind _captureKind = CaptureKind.Photo;
    private static string _photoFormat = "jpg";
    private static PhotoSource _photoSource = PhotoSource.FullHq;
    private static int _photoWidth = 4056;
    private static int _photoHeight = 3040;
    private static string _videoFormat = "mjpeg";
    private static string _sensorMode = "bin";
    private static int _jpgQuality = 95;
    private static double _photoEv = -1.0;
    private static int _videoSeconds = 0;
    private static int _previewFps = 20;
    private static int _randomFrameMinFps = 1;
    private static int _randomFrameMaxFps = 12;
    private static int _randomFrameSeconds = 10;
    private static readonly Random _randomFrameRandom = new();
    private static readonly int[] _colorChoices = new[] { 2, 4, 8, 16, 32, 64, 128, 256 };
    private static readonly int[] _pixelChoices = new[] { 1, 2, 4, 8, 16, 32, 64, 96, 128, 192, 256, 384, 512, 768, 1024, 1536, 2048 };
    private static int _selectedColorAmount = 32;
    private static bool _manualColorAmount;
    private static PaletteMode _paletteMode = PaletteMode.Green565;
    private static double _redScale = 1.0;
    private static double _greenScale = 1.0;
    private static double _blueScale = 1.0;
    private static double _lowSaveGamma = 0.82;
    private static int _lowGrayYellowFix = 20;
    private static bool _swapRedBlue;
    private static readonly object _lastPreviewLock = new();
    private static readonly object _settingsLock = new();
    private static byte[]? _lastPreviewRgb;
    private static int _lastPreviewWidth;
    private static int _lastPreviewHeight;
    private static string? _lastCapturedPath;
    private static DateTime _apiStartedUtc = DateTime.UtcNow;

    private static readonly object _previewRecordLock = new();
    private static FileStream? _previewRecordStream;
    private static string? _previewRecordPath;
    private static bool _previewRecording;
    private static bool _previewRandomRecording;
    private static DateTime _previewRecordStartedUtc;
    private static DateTime _previewLastFrameWrittenUtc;
    private static int _previewRandomSecond = -1;
    private static int _previewRandomFps = 1;
    private static int _recordEncodeBusy;
    private static int _recordFramesDropped;
    private static string? _previewRecordFinalPath;
    private static string _previewRecordFinalFormat = "mjpeg";
    private static int _backgroundVideoConversions;

    private static int _networkPage;
    private static int _networkRow;
    private static int _wifiConnectionIndex;
    private static List<string> _savedWifiConnections = new();
    private static string _networkStatus = "";
    private static DateTime _networkStatusUntilUtc = DateTime.MinValue;
    private static DateTime _lastNetworkRefreshUtc = DateTime.MinValue;
    private static string _batteryFile = "";
    private static string _batteryCommand = "";
    private static string _batteryText = "";
    private static DateTime _batteryLastReadUtc = DateTime.MinValue;
    private static bool _touchCaptureEnabled = false;
    private static int _livePreviewPixelMax = 256;
    private static DateTime _lastCaptureRequestUtc = DateTime.MinValue;
    private static string _wifiSsid = "";
    private static string _wifiPassword = "";
    private static string _hotspotSsid = "PiCamera";
    private static string _hotspotPassword = "picamera123";

    private static PreviewSettings _previewSettings = new()
    {
        Ev = -1.2,
        Sharpness = 0.1,
        Contrast = 0.75,
        Saturation = 0.85,
        Brightness = 0.0,
        BlackLevel = 35,
        DarkLevel = 0.85,
        PreviewPixelSize = 2048,
        PreviewColorLevels = 32,
        Denoise = "cdn_off"
    };

    public static async Task Main(string[] args)
    {
        try
        {
            Console.CursorVisible = false;
            Console.Write("\x1b[?25l");
            Console.Clear();
        }
        catch
        {
        }


        var framebufferPath = Arg(args, "--fb=", "/dev/fb0");
        var inputPath = Arg(args, "--touch=", "");
        var outputDir = Arg(args, "--out=", "/home/admin/Pictures/PiCamera");

        var width = IntArg(args, "--width=", 480);
        var height = IntArg(args, "--height=", 320);
        var rotate = IntArg(args, "--rotate=", 0);
        _swapRedBlue = BoolArg(args, "--swap-rb=", false);
        var fps = IntArg(args, "--fps=", 20);
        _previewFps = fps;
        var gpioPin = IntArg(args, "--gpio-pin=", -1);

        // Dodatkowe fizyczne przyciski GPIO.
        // - gpio-pin zostaje głównym spustem zdjęcia.
        // - resztę możesz dowolnie przepinać w systemd przez te argumenty.
        var buttonTabPin = IntArg(args, "--button-tab-pin=", -1);
        var buttonModePin = IntArg(args, "--button-mode-pin=", -1);
        var buttonPrevPin = IntArg(args, "--button-prev-pin=", -1);
        var buttonNextPin = IntArg(args, "--button-next-pin=", -1);
        var buttonGalleryPin = IntArg(args, "--button-gallery-pin=", -1);
        var buttonVideoPin = IntArg(args, "--button-video-pin=", -1);

        var invertX = BoolArg(args, "--invert-x=", false);
        var invertY = BoolArg(args, "--invert-y=", true);
        var apiEnabled = BoolArg(args, "--api=", true);
        var apiUrl = Arg(args, "--api-url=", "http://0.0.0.0:5000");
        _touchCaptureEnabled = BoolArg(args, "--touch-capture=", false);

        _previewSettings.Ev = DoubleArg(args, "--ev=", _previewSettings.Ev);
        _previewSettings.Sharpness = DoubleArg(args, "--sharpness=", _previewSettings.Sharpness);
        _previewSettings.Contrast = DoubleArg(args, "--contrast=", _previewSettings.Contrast);
        _previewSettings.Saturation = DoubleArg(args, "--saturation=", _previewSettings.Saturation);
        _previewSettings.Brightness = DoubleArg(args, "--brightness=", _previewSettings.Brightness);
        _previewSettings.BlackLevel = IntArg(args, "--black-level=", _previewSettings.BlackLevel);
        _previewSettings.DarkLevel = DoubleArg(args, "--dark-level=", _previewSettings.DarkLevel);
        _previewSettings.PreviewPixelSize = IntArg(args, "--preview-pixel=", _previewSettings.PreviewPixelSize);
        _previewSettings.PreviewColorLevels = IntArg(args, "--preview-colors=", _previewSettings.PreviewColorLevels);
        _previewSettings.Denoise = Arg(args, "--denoise=", _previewSettings.Denoise);
        _photoSource = ParsePhotoSource(Arg(args, "--photo-source=", "full"));
        _photoWidth = IntArg(args, "--photo-width=", _photoWidth);
        _photoHeight = IntArg(args, "--photo-height=", _photoHeight);


        _wifiSsid = Arg(args, "--wifi-ssid=", _wifiSsid);
        _wifiPassword = Arg(args, "--wifi-pass=", _wifiPassword);
        _hotspotSsid = Arg(args, "--hotspot-ssid=", _hotspotSsid);
        _hotspotPassword = Arg(args, "--hotspot-pass=", _hotspotPassword);
        _batteryFile = Arg(args, "--battery-file=", _batteryFile);
        _batteryCommand = Arg(args, "--battery-command=", _batteryCommand);
        _lowSaveGamma = DoubleArg(args, "--low-save-gamma=", _lowSaveGamma);
        _lowGrayYellowFix = IntArg(args, "--low-gray-yellow-fix=", _lowGrayYellowFix);

        _randomFrameMinFps = IntArg(args, "--random-min-fps=", _randomFrameMinFps);
        _randomFrameMaxFps = IntArg(args, "--random-max-fps=", _randomFrameMaxFps);
        _randomFrameSeconds = IntArg(args, "--random-seconds=", _randomFrameSeconds);
        if (_randomFrameMaxFps < _randomFrameMinFps)
            _randomFrameMaxFps = _randomFrameMinFps;


        _selectedColorAmount = ClosestColorChoice(IntArg(args, "--colors=", _selectedColorAmount));
        _manualColorAmount = args.Any(a => a.StartsWith("--colors="));

        _paletteMode = ParsePaletteMode(Arg(args, "--palette=", "green565"));

        _redScale = DoubleArg(args, "--red-scale=", _redScale);
        _greenScale = DoubleArg(args, "--green-scale=", _greenScale);
        _blueScale = DoubleArg(args, "--blue-scale=", _blueScale);

        _lookPreset = Arg(args, "--look=", _lookPreset).ToUpperInvariant();
        ApplyLookPreset(_lookPreset);
        if (_manualColorAmount)
            SetPreviewColors(_selectedColorAmount);
        else
            _selectedColorAmount = ClosestColorChoice(_previewSettings.PreviewColorLevels);

        Directory.CreateDirectory(outputDir);

        using var display = new FramebufferDisplay(framebufferPath, width, height, rotate, _swapRedBlue);
        using var preview = new CameraPreviewService(width, height, fps, _previewSettings);
        using var gpio = gpioPin >= 0 ? new GpioShutterService(pin: gpioPin) : null;
        using var touch = string.IsNullOrWhiteSpace(inputPath) ? null : new TouchInputService(inputPath, width, height, invertX, invertY);
        var extraGpioButtons = new List<GpioShutterService>();
        var usedGpioPins = new HashSet<int>();
        if (gpioPin >= 0)
            usedGpioPins.Add(gpioPin);

        Console.WriteLine("Pi Camera clean modes");
        Console.WriteLine($"Framebuffer: {framebufferPath}");
        Console.WriteLine($"Touch: {(string.IsNullOrWhiteSpace(inputPath) ? "off" : inputPath)} invertX={invertX} invertY={invertY} touchCapture={_touchCaptureEnabled}");
        Console.WriteLine($"GPIO shutter: {(gpioPin >= 0 ? $"GPIO{gpioPin}" : "off")}");
        Console.WriteLine($"GPIO buttons: tab={PinLabel(buttonTabPin)} mode={PinLabel(buttonModePin)} prev={PinLabel(buttonPrevPin)} next={PinLabel(buttonNextPin)} gallery={PinLabel(buttonGalleryPin)} video={PinLabel(buttonVideoPin)}");
        Console.WriteLine($"Output: {outputDir}");
        Console.WriteLine($"LOOK: {_lookPreset}");

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _running = false;
        };

        if (apiEnabled)
            _ = Task.Run(() => StartApiServerAsync(apiUrl, outputDir, preview));

        _ = Task.Run(() => KeyboardLoop(preview, display, width, height, outputDir));

        if (gpio is not null)
        {
            gpio.ShutterPressed += () =>
            {
                RequestPhotoCapture("gpio");
                return Task.CompletedTask;
            };
            gpio.StatusChanged += msg => Console.WriteLine("[GPIO SHUTTER] " + msg);
            gpio.Start();
        }

        AddHardwareButton(extraGpioButtons, usedGpioPins, buttonTabPin, "TAB", () => HandleHardwareButtonAsync("tab", preview, display, width, height, outputDir));
        AddHardwareButton(extraGpioButtons, usedGpioPins, buttonModePin, "MODE", () => HandleHardwareButtonAsync("mode", preview, display, width, height, outputDir));
        AddHardwareButton(extraGpioButtons, usedGpioPins, buttonPrevPin, "PREV", () => HandleHardwareButtonAsync("prev", preview, display, width, height, outputDir));
        AddHardwareButton(extraGpioButtons, usedGpioPins, buttonNextPin, "NEXT", () => HandleHardwareButtonAsync("next", preview, display, width, height, outputDir));
        AddHardwareButton(extraGpioButtons, usedGpioPins, buttonGalleryPin, "GALLERY", () => HandleHardwareButtonAsync("gallery", preview, display, width, height, outputDir));
        AddHardwareButton(extraGpioButtons, usedGpioPins, buttonVideoPin, "VIDEO", () => HandleHardwareButtonAsync("video", preview, display, width, height, outputDir));

        if (touch is not null)
        {
            touch.Touched += (x, y) => HandleTouch(x, y, width, height, preview, display, outputDir);
            touch.StatusChanged += msg => Console.WriteLine("[TOUCH] " + msg);
            touch.Start();
        }

        preview.StatusChanged += msg => Console.WriteLine("[PREVIEW] " + msg);
        preview.FrameReady += frame =>
        {
            if (_tab != Tab.Preview || _isBusy)
                return;

            try
            {
                _previewReadyForTouch = true;

                lock (_lastPreviewLock)
                {
                    _lastPreviewRgb = frame.Rgb.ToArray();
                    _lastPreviewWidth = frame.Width;
                    _lastPreviewHeight = frame.Height;
                }

                display.DrawRgbFrameAdjusted(
                    frame.Rgb,
                    frame.Width,
                    frame.Height,
                    0,
                    0,
                    _previewSettings.BlackLevel,
                    _previewSettings.DarkLevel,
                    EffectivePreviewPixelSize(frame.Width),
                    _previewSettings.PreviewColorLevels,
                    _redScale,
                    _greenScale,
                    _blueScale,
                    PaletteModeArg());

                DrawTopBar(display, width);
                DrawTabs(display, width, height);
                display.Flush();

                WritePreviewRecordingFrameIfNeeded(frame.Rgb, frame.Width, frame.Height);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[DISPLAY] " + ex.Message);
            }
        };

        display.Clear(0x0000);
        display.DrawCenteredTextScaled("PI CAMERA", height / 2 - 34, 0xFFFF, 2);
        display.DrawCenteredText("URUCHAMIAM KAMERE...", height / 2 - 2, 0x07E0);
        display.Flush();

        StartPreviewSafe(preview);

        while (_running)
        {
            if (_tab == Tab.Preview && !_previewReadyForTouch && !_isBusy)
            {
                var wait = (DateTime.UtcNow - _previewStartUtc).TotalSeconds;
                if (wait > 4)
                {
                    display.Clear(0x0000);
                    display.DrawCenteredTextScaled("KAMERA STARTUJE", height / 2 - 34, 0xFFE0, 2);
                    display.DrawCenteredText($"CZEKAM {wait:0}s", height / 2 - 2, 0xFFFF);
                    display.Flush();
                    await Task.Delay(500);
                }
            }

            if (_captureRequested && !_isBusy)
            {
                _captureRequested = false;

                if (_captureKind == CaptureKind.Video)
                {
                    await ToggleVideoAsync(outputDir, display, width, height);
                }
                else if (_captureKind == CaptureKind.RandomFrame)
                {
                    await RecordRandomFrameVideoAsync(outputDir, display, width, height);
                }
                else
                {
                    _isBusy = true;

                    try
                    {
                        var fullHq = _photoSource == PhotoSource.FullHq;

                        DrawBusy(display, width, height, fullHq ? "FOTO HQ..." : "ZDJECIE...");

                        if (fullHq)
                        {
                            preview.Stop();
                            await Task.Delay(500);
                        }

                        var path = await TakePhotoAsync(outputDir);
                        _lastCapturedPath = path;
                        DrawSaved(display, width, height, fullHq ? "FOTO HQ OK" : "FOTO PREVIEW OK");
                        await Task.Delay(350);
                    }
                    catch (Exception ex)
                    {
                        DrawError(display, width, height, ex.Message);
                        Console.WriteLine("[CAPTURE] " + ex);
                        await Task.Delay(1200);
                    }
                    finally
                    {
                        _isBusy = false;
                        if (_tab == Tab.Preview)
                            StartPreviewSafe(preview);
                        else
                            RedrawNonPreview(display, width, height, outputDir);
                    }
                }
            }

            await Task.Delay(25);
        }

        try
        {
            lock (_previewRecordLock)
            {
                _previewRecordStream?.Flush();
                _previewRecordStream?.Dispose();
                _previewRecordStream = null;
                _previewRecording = false;
            }
        }
        catch { }

        foreach (var button in extraGpioButtons)
        {
            try { button.Dispose(); }
            catch { }
        }

        preview.Stop();
        display.Clear(0x0000);
        display.Flush();
    }


    private static void RequestPhotoCapture(string source)
    {
        var now = DateTime.UtcNow;
        if (_isBusy)
            return;

        if ((now - _lastCaptureRequestUtc).TotalMilliseconds < 1500)
            return;

        _lastCaptureRequestUtc = now;
        _captureKind = CaptureKind.Photo;
        _captureRequested = true;
        Console.WriteLine($"[CAPTURE] request from {source}");
    }

    private static void StartPreviewSafe(CameraPreviewService preview)
    {
        _previewReadyForTouch = false;
        _previewStartUtc = DateTime.UtcNow;
        _ignoreTouchUntilUtc = DateTime.UtcNow.AddMilliseconds(700);
        preview.UpdateSettings(_previewSettings);
        preview.Start();
    }

}
