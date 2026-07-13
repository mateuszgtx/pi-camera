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
        RandomFrame,
        GlitchPhoto,
        GlitchVideo,
        Stream
    }

    private enum PhotoSource
    {
        FullHq,
        Preview
    }

    private enum AudioInputMode
    {
        Auto,
        Off,
        Aux,
        Bluetooth,
        Manual
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
    private static DateTime _lastPreviewAutoRestartUtc = DateTime.MinValue;
    private static int _previewRestartAttempts;

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

    private static int _glitchStrength = 5;
    private static int _glitchChangeMs = 700;
    private static bool _glitchPaletteEnabled = true;
    private static bool _glitchPixelsEnabled = true;
    private static bool _glitchRgbEnabled = true;
    private static int _glitchPhotoCount = 4;
    private static int _vhsGlitchFrequency = 2;
    private static int _vhsQuality = 6;
    private static int _vhsScanlines = 6;
    private static int _vhsNoise = 4;
    private static int _vhsWobble = 4;
    private static bool _glitchVideoRecording;
    private static DateTime _glitchNextChangeUtc = DateTime.MinValue;
    private static PreviewSettings? _glitchSavedPreviewSettings;
    private static PaletteMode _glitchSavedPaletteMode;
    private static double _glitchSavedRedScale;
    private static double _glitchSavedGreenScale;
    private static double _glitchSavedBlueScale;
    private static int _glitchSavedSelectedColorAmount;
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
    private static readonly object _displayLock = new();
    private static int _previewDisplayBusy;
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

    private static readonly object _streamLock = new();
    private static Process? _streamProcess;
    private static Stream? _streamInput;
    private static bool _streaming;
    private static DateTime _streamStartedUtc;
    private static DateTime _streamLastFrameWrittenUtc;
    private static int _streamEncodeBusy;
    private static int _streamFramesDropped;
    private static int _streamRequestedAction; // 0 = toggle, 1 = start, 2 = stop
    private static string _streamUrl = "";
    private static string _streamOutputFormat = "auto";
    private static int _streamFps = 15;
    private static int _streamBitrateKbps = 2500;
    private static int _streamJpegQuality = 75;
    private static bool _streamUseRaw;

    private static readonly object _audioLock = new();
    private static bool _audioEnabled = true;
    private static AudioInputMode _audioInputMode = AudioInputMode.Auto;
    private static string _audioInputFormat = "auto";
    private static string _audioDevice = "";
    private static int _audioSampleRate = 48000;
    private static int _audioBitrateKbps = 128;
    private static Process? _audioRecordProcess;
    private static string? _audioRecordPath;
    private static string? _audioRecordDeviceLabel;
    private static DateTime _lastBluetoothInputProfileAttemptUtc = DateTime.MinValue;
    private static string _lastAudioInputMessage = "";
    private static DateTime _lastAudioInputMessageUtc = DateTime.MinValue;

    private static int _networkPage;
    private static int _networkRow;
    private static int _wifiConnectionIndex;
    private static List<string> _savedWifiConnections = new();
    private static int _bluetoothDeviceIndex;
    private static List<BluetoothDeviceInfo> _bluetoothDevices = new();
    private static List<BluetoothDeviceInfo> _bluetoothAllDevices = new();
    private static readonly Dictionary<string, string> _bluetoothScanNames = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _lastBluetoothRefreshUtc = DateTime.MinValue;
    private static DateTime _lastBluetoothScanUtc = DateTime.MinValue;
    private static string _bluetoothLastScanLog = "";
    private static readonly object _bluetoothActionLock = new();
    private static bool _bluetoothActionBusy;
    private static string _bluetoothAction = "";
    private static string _bluetoothActionMessage = "";
    private static bool _bluetoothActionOk = true;
    private static DateTime _bluetoothActionUtc = DateTime.MinValue;
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
    private static string _settingsFilePath = "";

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
        _settingsFilePath = Arg(args, "--settings-file=", DefaultSettingsFilePath());

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
        _glitchStrength = Math.Clamp(IntArg(args, "--glitch-strength=", _glitchStrength), 1, 10);
        _glitchChangeMs = Math.Clamp(IntArg(args, "--glitch-change-ms=", _glitchChangeMs), 100, 5000);
        _glitchPaletteEnabled = BoolArg(args, "--glitch-palette=", _glitchPaletteEnabled);
        _glitchPixelsEnabled = BoolArg(args, "--glitch-pixels=", _glitchPixelsEnabled);
        _glitchRgbEnabled = BoolArg(args, "--glitch-rgb=", _glitchRgbEnabled);
        _glitchPhotoCount = Math.Clamp(IntArg(args, "--glitch-photo-count=", _glitchPhotoCount), 1, 12);
        _vhsGlitchFrequency = Math.Clamp(IntArg(args, "--vhs-glitch-frequency=", _vhsGlitchFrequency), 0, 10);
        _vhsQuality = Math.Clamp(IntArg(args, "--vhs-quality=", _vhsQuality), 0, 10);
        _vhsScanlines = Math.Clamp(IntArg(args, "--vhs-scanlines=", _vhsScanlines), 0, 10);
        _vhsNoise = Math.Clamp(IntArg(args, "--vhs-noise=", _vhsNoise), 0, 10);
        _vhsWobble = Math.Clamp(IntArg(args, "--vhs-wobble=", _vhsWobble), 0, 10);
        _streamUrl = Arg(args, "--stream-url=", _streamUrl);
        _streamOutputFormat = NormalizeStreamOutputFormat(Arg(args, "--stream-format=", _streamOutputFormat));
        _streamFps = Math.Clamp(IntArg(args, "--stream-fps=", _streamFps), 1, 30);
        _streamBitrateKbps = Math.Clamp(IntArg(args, "--stream-bitrate=", _streamBitrateKbps), 256, 20000);
        _streamJpegQuality = Math.Clamp(IntArg(args, "--stream-jpeg-quality=", _streamJpegQuality), 35, 95);
        _streamUseRaw = BoolArg(args, "--stream-raw=", _streamUseRaw);
        _audioEnabled = BoolArg(args, "--audio=", _audioEnabled);
        _audioInputMode = ParseAudioInputMode(Arg(args, "--audio-mode=", _audioInputMode.ToString()));
        _audioInputFormat = NormalizeAudioInputFormat(Arg(args, "--audio-format=", _audioInputFormat));
        _audioDevice = Arg(args, "--audio-device=", _audioDevice);
        _audioSampleRate = Math.Clamp(IntArg(args, "--audio-sample-rate=", _audioSampleRate), 8000, 96000);
        _audioBitrateKbps = Math.Clamp(IntArg(args, "--audio-bitrate=", _audioBitrateKbps), 32, 512);
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

        LoadPersistentSettingsFromDisk();

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
        Console.WriteLine($"VHS glitch frequency: {_vhsGlitchFrequency}/10");
        Console.WriteLine($"VHS quality: {_vhsQuality}/10 scanlines={_vhsScanlines}/10 noise={_vhsNoise}/10 wobble={_vhsWobble}/10");

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
        _ = Task.Run(() => HardwarePasswordResetLoopAsync(extraGpioButtons));

        if (touch is not null)
        {
            touch.Touched += (x, y) => HandleTouch(x, y, width, height, preview, display, outputDir);
            touch.StatusChanged += msg => Console.WriteLine("[TOUCH] " + msg);
            touch.Start();
        }

        preview.StatusChanged += msg => Console.WriteLine("[PREVIEW] " + msg);
        preview.FrameReady += frame =>
        {
            if (!_running || _tab != Tab.Preview || _isBusy)
                return;

            // Keep the newest frame for captures/API, but do not copy the whole
            // RGB buffer. PreviewFrame owns this array and never mutates it.
            _previewReadyForTouch = true;
            _previewRestartAttempts = 0;

            lock (_lastPreviewLock)
            {
                _lastPreviewRgb = frame.Rgb;
                _lastPreviewWidth = frame.Width;
                _lastPreviewHeight = frame.Height;
            }

            // Never queue display work. If VHS/TFT rendering is still busy,
            // discard this display frame and accept a fresh one next time.
            // This trades some FPS for very low latency instead of building a
            // 2-3 second backlog in the rpicam-vid stdout pipe.
            if (Interlocked.CompareExchange(ref _previewDisplayBusy, 1, 0) != 0)
                return;

            _ = Task.Run(() =>
            {
                try
                {
                    if (!_running || _tab != Tab.Preview || _isBusy)
                        return;

                    int blackLevel;
                    double darkLevel;
                    int pixelSize;
                    int colorLevels;
                    double redScale;
                    double greenScale;
                    double blueScale;
                    string paletteMode;
                    string lookPreset;
                    int vhsGlitchFrequency;
                    int vhsQuality;
                    int vhsScanlines;
                    int vhsNoise;
                    int vhsWobble;

                    lock (_settingsLock)
                    {
                        blackLevel = _previewSettings.BlackLevel;
                        darkLevel = _previewSettings.DarkLevel;
                        pixelSize = EffectivePreviewPixelSize(frame.Width);
                        colorLevels = _previewSettings.PreviewColorLevels;
                        redScale = _redScale;
                        greenScale = _greenScale;
                        blueScale = _blueScale;
                        paletteMode = PaletteModeArg();
                        lookPreset = _lookPreset;
                        vhsGlitchFrequency = _vhsGlitchFrequency;
                        vhsQuality = _vhsQuality;
                        vhsScanlines = _vhsScanlines;
                        vhsNoise = _vhsNoise;
                        vhsWobble = _vhsWobble;
                    }

                    var displayRgb = IsVhsLook(lookPreset)
                        ? BuildVhsFrame(frame.Rgb, frame.Width, frame.Height, NextVhsSeed(), vhsGlitchFrequency, vhsQuality, vhsScanlines, vhsNoise, vhsWobble)
                        : frame.Rgb;

                    lock (_displayLock)
                    {
                        if (!_running || _tab != Tab.Preview || _isBusy)
                            return;

                        display.DrawRgbFrameAdjusted(
                            displayRgb,
                            frame.Width,
                            frame.Height,
                            0,
                            0,
                            blackLevel,
                            darkLevel,
                            pixelSize,
                            colorLevels,
                            redScale,
                            greenScale,
                            blueScale,
                            paletteMode);

                        DrawTopBar(display, width);
                        DrawTabs(display, width, height);
                        display.Flush();
                    }

                    // Keep recording/stream behavior tied to accepted preview
                    // frames, as before, so the latency fix does not increase
                    // CPU usage while recording.
                    WritePreviewRecordingFrameIfNeeded(frame.Rgb, frame.Width, frame.Height);
                    WriteStreamFrameIfNeeded(frame.Rgb, frame.Width, frame.Height);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[DISPLAY] " + ex.Message);
                }
                finally
                {
                    Interlocked.Exchange(ref _previewDisplayBusy, 0);
                }
            });
        };

        display.Clear(0x0000);
        display.DrawCenteredTextScaled("PI CAM", height / 2 - 34, 0xFFFF, 2);
        display.DrawCenteredText("STARTING CAMERA...", height / 2 - 2, 0x07E0);
        display.Flush();

        StartPreviewSafe(preview);

        while (_running)
        {
            if (_tab == Tab.Preview && !_previewReadyForTouch && !_isBusy)
            {
                var now = DateTime.UtcNow;
                var wait = (now - _previewStartUtc).TotalSeconds;

                if (wait > 8 && (now - _lastPreviewAutoRestartUtc).TotalSeconds > 6)
                {
                    _lastPreviewAutoRestartUtc = now;
                    _previewRestartAttempts++;

                    Console.WriteLine($"[PREVIEW] no frames for {wait:0}s, restarting preview (attempt {_previewRestartAttempts})");

                    display.Clear(0x0000);
                    display.DrawCenteredTextScaled("CAMERA RETRY", height / 2 - 34, 0xFFE0, 2);
                    display.DrawCenteredText($"RESTART {_previewRestartAttempts}", height / 2 - 2, 0xFFFF);
                    display.Flush();

                    try { preview.Stop(); }
                    catch { }

                    await Task.Delay(400);
                    StartPreviewSafe(preview, resetRetryCounter: false);
                }
                else if (wait > 4)
                {
                    display.Clear(0x0000);
                    display.DrawCenteredTextScaled("CAMERA STARTING", height / 2 - 34, 0xFFE0, 2);
                    display.DrawCenteredText($"WAIT {wait:0}s", height / 2 - 2, 0xFFFF);
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
                else if (_captureKind == CaptureKind.GlitchVideo)
                {
                    await ToggleGlitchVideoAsync(outputDir, display, width, height);
                }
                else if (_captureKind == CaptureKind.Stream)
                {
                    var streamAction = Interlocked.Exchange(ref _streamRequestedAction, 0);
                    await ToggleStreamAsync(display, width, height, streamAction);
                }
                else
                {
                    _isBusy = true;

                    try
                    {
                        var fullHq = _photoSource == PhotoSource.FullHq;
                        var glitchPhoto = _captureKind == CaptureKind.GlitchPhoto;
                        var glitchCount = glitchPhoto ? Math.Clamp(_glitchPhotoCount, 1, 12) : 1;

                        DrawBusy(display, width, height, glitchPhoto
                            ? (glitchCount > 1 ? $"GLITCH x{glitchCount}..." : "GLITCH PHOTO...")
                            : (fullHq ? "PHOTO HQ..." : "PHOTO..."));

                        if (fullHq)
                        {
                            preview.Stop();
                            await Task.Delay(500);
                        }

                        string? lastPath = null;

                        for (var i = 0; i < glitchCount; i++)
                        {
                            if (glitchPhoto)
                            {
                                ApplyGlitchOnce();

                                if (_photoSource == PhotoSource.Preview)
                                    await Task.Delay(120);

                                if (glitchCount > 1)
                                    DrawBusy(display, width, height, $"GLITCH {i + 1}/{glitchCount}...");
                            }

                            var suffix = glitchPhoto && glitchCount > 1 ? $"_G{i + 1:00}" : null;
                            lastPath = await TakePhotoAsync(outputDir, suffix);

                            if (glitchPhoto && glitchCount > 1 && i < glitchCount - 1)
                                await Task.Delay(90);
                        }

                        _lastCapturedPath = lastPath;
                        DrawSaved(display, width, height, glitchPhoto
                            ? (glitchCount > 1 ? $"GLITCH x{glitchCount} OK" : "GLITCH OK")
                            : (fullHq ? "PHOTO HQ OK" : "PHOTO PREVIEW OK"));
                        await Task.Delay(glitchPhoto && glitchCount > 1 ? 650 : 350);
                    }
                    catch (Exception ex)
                    {
                        DrawError(display, width, height, ex.Message);
                        Console.WriteLine("[CAPTURE] " + ex);
                        await Task.Delay(1200);
                    }
                    finally
                    {
                        if (_captureKind == CaptureKind.GlitchPhoto)
                            RestoreGlitchSettings();

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

            StopAudioRecordingForVideo();
            StopStreamCore(logOnly: true);
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
        _captureRequested = true;
        Console.WriteLine($"[CAPTURE] request from {source}, mode={_captureKind}");
    }

    private static void StartPreviewSafe(CameraPreviewService preview, bool resetRetryCounter = true)
    {
        if (resetRetryCounter)
            _previewRestartAttempts = 0;

        _previewReadyForTouch = false;
        _previewStartUtc = DateTime.UtcNow;
        _ignoreTouchUntilUtc = DateTime.UtcNow.AddMilliseconds(700);
        preview.UpdateSettings(_previewSettings);
        preview.Start();
    }

}
