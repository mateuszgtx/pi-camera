using System.Diagnostics;
using pi_camera.Services;

using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp;
namespace pi_camera;

public static class Program
{
    private enum Tab
    {
        Preview,
        Mode,
        Gallery,
        Network,
        Info
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
    private static volatile bool _videoToggleRequested;
    private static volatile bool _isBusy;
    private static volatile bool _previewReadyForTouch;
    private static DateTime _ignoreTouchUntilUtc = DateTime.UtcNow.AddSeconds(2);
    private static DateTime _previewStartUtc = DateTime.UtcNow;

    private static Tab _tab = Tab.Preview;

    private static string _lookPreset = "LOW32";
    private static int _modePage;
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
    private static int _previewFps = 10;
    private static int _randomFrameMinFps = 1;
    private static int _randomFrameMaxFps = 12;
    private static int _randomFrameSeconds = 10;
    private static readonly Random _randomFrameRandom = new();
    private static bool _recording;
    private static int _backgroundSaveJobs;
    private static readonly int[] _colorChoices = new[] { 2, 4, 8, 16, 32, 64, 128, 256 };
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
    private static byte[]? _lastPreviewRgb;
    private static int _lastPreviewWidth;
    private static int _lastPreviewHeight;

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

    private static Process? _recordProcess;

    private static int _networkPage;
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
        PreviewPixelSize = 4,
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
            // ignore when no real console is attached
        }


        var framebufferPath = Arg(args, "--fb=", "/dev/fb0");
        var inputPath = Arg(args, "--touch=", "");
        var outputDir = Arg(args, "--out=", "/home/admin/Pictures/PiCamera");

        var width = IntArg(args, "--width=", 480);
        var height = IntArg(args, "--height=", 320);
        var rotate = IntArg(args, "--rotate=", 0);
        _swapRedBlue = BoolArg(args, "--swap-rb=", false);
        var fps = IntArg(args, "--fps=", 10);
        _previewFps = fps;
        var gpioPin = IntArg(args, "--gpio-pin=", -1);
        var invertX = BoolArg(args, "--invert-x=", false);
        var invertY = BoolArg(args, "--invert-y=", true);

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

        Console.WriteLine("Pi Camera clean modes");
        Console.WriteLine($"Framebuffer: {framebufferPath}");
        Console.WriteLine($"Touch: {(string.IsNullOrWhiteSpace(inputPath) ? "off" : inputPath)} invertX={invertX} invertY={invertY}");
        Console.WriteLine($"GPIO shutter: {(gpioPin >= 0 ? $"GPIO{gpioPin}" : "off")}");
        Console.WriteLine($"Output: {outputDir}");
        Console.WriteLine($"LOOK: {_lookPreset}");

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _running = false;
        };

        _ = Task.Run(() => KeyboardLoop(preview, display, width, height, outputDir));

        if (gpio is not null)
        {
            gpio.ShutterPressed += () =>
            {
                _captureRequested = true;
                return Task.CompletedTask;
            };
            gpio.StatusChanged += msg => Console.WriteLine("[GPIO] " + msg);
            gpio.Start();
        }

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
                    _previewSettings.PreviewPixelSize,
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

                // VIDEO/RANDOM nie zatrzymuje preview i nie odpala drugiego rpicam-vid.
                // Zapisuje klatki z aktualnego podglądu, więc kamera nie jest zajęta podwójnie.
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

                        // FULL HQ używa osobnego rpicam-still, więc preview musi na chwilę zwolnić kamerę.
                        // PREVIEW source nadal zapisuje aktualną klatkę bez zatrzymywania kamery.
                        if (fullHq)
                        {
                            preview.Stop();
                            await Task.Delay(500);
                        }

                        var path = await TakePhotoAsync(outputDir);
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

        try
        {
            _recordProcess?.Kill();
        }
        catch { }

        preview.Stop();
        display.Clear(0x0000);
        display.Flush();
    }

    private static void StartPreviewSafe(CameraPreviewService preview)
    {
        _previewReadyForTouch = false;
        _previewStartUtc = DateTime.UtcNow;
        _ignoreTouchUntilUtc = DateTime.UtcNow.AddMilliseconds(700);
        preview.UpdateSettings(_previewSettings);
        preview.Start();
    }

    private static async Task<string> TakePhotoAsync(string outputDir)
    {
        Directory.CreateDirectory(outputDir);

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var isRaw = _photoFormat.Equals("raw", StringComparison.OrdinalIgnoreCase) ||
                    _photoFormat.Equals("rawjpg", StringComparison.OrdinalIgnoreCase);

        var finalExt = _photoFormat.ToLowerInvariant() switch
        {
            "png" => "png",
            "bmp" => "bmp",
            "raw" => "dng",
            "rawjpg" => "jpg",
            _ => "jpg"
        };

        var finalPath = Path.Combine(outputDir, $"IMG_{stamp}.{finalExt}");

        // PREVIEW = dokładnie klatka z ekranu, niska rozdzielczość.
        if (_photoSource == PhotoSource.Preview && !isRaw)
        {
            if (TrySaveCurrentPreviewFrame(finalPath, _photoFormat))
                return finalPath;
        }

        // FULL HQ = pełna rozdzielczość sensora, potem nakładamy efekt/look.
        if (!isRaw)
        {
            return await TakeFullHqPhotoAsync(outputDir, finalPath);
        }

        // RAW/RAWJPG zostaje pełnym przechwyceniem z rpicam-still.
        await CaptureRawPhotoAsync(outputDir, finalPath, stamp);
        return finalPath;
    }











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


    private static async Task<string> TakeFullHqPhotoAsync(string outputDir, string finalPath)
    {
        var tempPath = Path.Combine(outputDir, $"TMP_HQ_{DateTime.Now:yyyyMMdd_HHmmssfff}.jpg");

        try
        {
            await CaptureStillJpegAsync(tempPath, _photoWidth, _photoHeight);

            using var image = await Image.LoadAsync<Rgb24>(tempPath);
            ApplyFullPhotoLook(image);

            await SaveImageByFormatAsync(image, finalPath, _photoFormat);
            return finalPath;
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static async Task CaptureStillJpegAsync(string outputPath, int width, int height)
    {
        var args = new List<string>
        {
            "--nopreview",
            "--immediate",
            "--width", width.ToString(),
            "--height", height.ToString(),
            "--ev", _photoEv.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--sharpness", _previewSettings.Sharpness.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--contrast", _previewSettings.Contrast.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--saturation", _previewSettings.Saturation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--brightness", _previewSettings.Brightness.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--denoise", _previewSettings.Denoise,
            "-o", outputPath
        };

        AddSensorArgs(args, true);

        var psi = new ProcessStartInfo
        {
            FileName = "rpicam-still",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Nie można uruchomić rpicam-still");

        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        await p.WaitForExitAsync();

        var stderr = await stderrTask;
        var stdout = await stdoutTask;

        if (p.ExitCode != 0 || !File.Exists(outputPath))
        {
            var msg = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;

            if (msg.Contains("in use by another process", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("failed to acquire camera", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("Device or resource busy", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(1200);

                TryDelete(outputPath);

                using var retry = Process.Start(psi) ?? throw new InvalidOperationException("Nie można uruchomić rpicam-still retry");
                var retryOutTask = retry.StandardOutput.ReadToEndAsync();
                var retryErrTask = retry.StandardError.ReadToEndAsync();

                await retry.WaitForExitAsync();

                var retryErr = await retryErrTask;
                var retryOut = await retryOutTask;

                if (retry.ExitCode == 0 && File.Exists(outputPath))
                    return;

                var retryMsg = string.IsNullOrWhiteSpace(retryErr) ? retryOut : retryErr;
                throw new Exception("rpicam-still failed after retry: " + retryMsg.Trim());
            }

            throw new Exception("rpicam-still failed: " + msg.Trim());
        }
    }

    private static async Task CaptureRawPhotoAsync(string outputDir, string finalPath, string stamp)
    {
        var isRawJpg = _photoFormat.Equals("rawjpg", StringComparison.OrdinalIgnoreCase);
        var rawPath = isRawJpg ? Path.Combine(outputDir, $"IMG_{stamp}.dng") : finalPath;

        var args = new List<string>
        {
            "--nopreview",
            "--immediate",
            "--width", _photoWidth.ToString(),
            "--height", _photoHeight.ToString(),
            "--raw",
            "--ev", _photoEv.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--sharpness", _previewSettings.Sharpness.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--contrast", _previewSettings.Contrast.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--saturation", _previewSettings.Saturation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--brightness", _previewSettings.Brightness.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--denoise", _previewSettings.Denoise,
            "-o", isRawJpg ? finalPath : rawPath
        };

        AddSensorArgs(args, true);

        var psi = new ProcessStartInfo
        {
            FileName = "rpicam-still",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Nie można uruchomić rpicam-still");

        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        await p.WaitForExitAsync();

        var stderr = await stderrTask;
        var stdout = await stdoutTask;

        if (p.ExitCode != 0)
        {
            var msg = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new Exception("rpicam-still RAW failed: " + msg.Trim());
        }
    }

    private static void ApplyFullPhotoLook(Image<Rgb24> image)
    {
        var pixel = Math.Clamp(_previewSettings.PreviewPixelSize, 1, 64);
        var colors = Math.Clamp(_previewSettings.PreviewColorLevels, 2, 256);
        var black = Math.Clamp(_previewSettings.BlackLevel, 0, 240);
        var dark = Math.Clamp(_previewSettings.DarkLevel, 0.25, 2.0);
        var denom = Math.Max(1, 255 - black);

        // Przy pełnym zdjęciu efekt pixel ma być podobny wizualnie do podglądu, ale proporcjonalny do rozdzielczości.
        var previewBase = 480.0;
        var scale = Math.Max(1.0, image.Width / previewBase);
        var block = Math.Clamp((int)Math.Round(pixel * scale), 1, 256);

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
    }

    private static async Task SaveImageByFormatAsync(Image<Rgb24> image, string path, string format)
    {
        switch (format.ToLowerInvariant())
        {
            case "png":
                await image.SaveAsPngAsync(path);
                break;
            case "bmp":
                await image.SaveAsBmpAsync(path);
                break;
            default:
                await image.SaveAsJpegAsync(path, new JpegEncoder { Quality = Math.Clamp(_jpgQuality, 70, 100) });
                break;
        }
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

        var pixel = Math.Clamp(_previewSettings.PreviewPixelSize, 1, 32);
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

        var pixel = Math.Clamp(settings.PreviewPixelSize, 1, 32);
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



    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }


    private static async Task RecordRandomFrameVideoAsync(string outputDir, FramebufferDisplay display, int width, int height)
    {
        if (_previewRecording)
        {
            StopPreviewRecording(display, width, height, "REC STOP");
            await Task.Delay(100);
            return;
        }

        Directory.CreateDirectory(outputDir);

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var basePath = Path.Combine(outputDir, $"RANDOM_{stamp}");

        StartPreviewRecording(basePath, random: true);

        DrawSaved(display, width, height, "RANDOM REC");
        await Task.Delay(100);
    }



    private static async Task ToggleVideoAsync(string outputDir, FramebufferDisplay display, int width, int height)
    {
        if (_previewRecording)
        {
            StopPreviewRecording(display, width, height, "VIDEO STOP");
            await Task.Delay(100);
            return;
        }

        Directory.CreateDirectory(outputDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var basePath = Path.Combine(outputDir, $"VID_{stamp}");

        StartPreviewRecording(basePath, random: false);

        DrawSaved(display, width, height, "REC START");
        await Task.Delay(100);
    }




    private static void StartPreviewRecording(string basePath, bool random)
    {
        var finalFormat = NormalizeVideoFormat(_videoFormat);
        var tempPath = basePath + ".rawmjpeg";
        var finalPath = basePath + (finalFormat == "mp4" ? ".mp4" : ".avi");

        lock (_previewRecordLock)
        {
            _previewRecordStream?.Dispose();
            _previewRecordStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            _previewRecordPath = tempPath;
            _previewRecordFinalPath = finalPath;
            _previewRecordFinalFormat = finalFormat;
            _previewRecording = true;
            _previewRandomRecording = random;
            _previewRecordStartedUtc = DateTime.UtcNow;
            _previewLastFrameWrittenUtc = DateTime.MinValue;
            _previewRandomSecond = -1;
            _previewRandomFps = Math.Clamp(_randomFrameMinFps, 1, 30);
        }

        Console.WriteLine(random
            ? $"[REC] random preview source MJPEG: {tempPath} -> {finalPath}"
            : $"[REC] preview source MJPEG: {tempPath} -> {finalPath}");
    }

    private static void StopPreviewRecording(FramebufferDisplay display, int width, int height, string message)
    {
        var result = ClosePreviewRecordingCore();

        if (result.sourcePath is not null && result.finalPath is not null)
        {
            DrawSaved(display, width, height, result.finalFormat == "mp4" ? "KONWERSJA MP4" : "KONWERSJA AVI");
            QueueVideoConversion(result.sourcePath, result.finalPath, result.finalFormat, _previewFps);
        }
        else
        {
            DrawSaved(display, width, height, result.displayName ?? message);
        }
    }

    private static (string? sourcePath, string? finalPath, string finalFormat, string? displayName) ClosePreviewRecordingCore()
    {
        string? sourcePath;
        string? finalPath;
        string finalFormat;

        var waitUntil = DateTime.UtcNow.AddMilliseconds(1500);
        while (Interlocked.CompareExchange(ref _recordEncodeBusy, 0, 0) != 0 && DateTime.UtcNow < waitUntil)
            Thread.Sleep(20);

        lock (_previewRecordLock)
        {
            sourcePath = _previewRecordPath;
            finalPath = _previewRecordFinalPath;
            finalFormat = _previewRecordFinalFormat;

            try
            {
                _previewRecordStream?.Flush();
                _previewRecordStream?.Dispose();
            }
            catch
            {
                // ignore
            }

            _previewRecordStream = null;
            _previewRecordPath = null;
            _previewRecordFinalPath = null;
            _previewRecordFinalFormat = "mjpeg";
            _previewRecording = false;
            _previewRandomRecording = false;
        }

        var dropped = Interlocked.Exchange(ref _recordFramesDropped, 0);
        if (dropped > 0)
            Console.WriteLine($"[REC] dropped frames while encoding: {dropped}");

        var displayName = finalPath is null ? null : Path.GetFileName(finalPath);

        return (sourcePath, finalPath, finalFormat, displayName);
    }

    private static string NormalizeVideoFormat(string format)
    {
        return string.Equals(format, "mp4", StringComparison.OrdinalIgnoreCase) ? "mp4" : "mjpeg";
    }

    private static void QueueVideoConversion(string sourcePath, string finalPath, string finalFormat, int framerate)
    {
        _ = Task.Run(async () =>
        {
            Interlocked.Increment(ref _backgroundVideoConversions);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath) ?? ".");
                var ok = await ConvertRawMjpegAsync(sourcePath, finalPath, finalFormat, framerate);
                if (ok)
                {
                    Console.WriteLine($"[REC] video ready: {finalPath}");
                    TryDelete(sourcePath);
                }
                else
                {
                    Console.WriteLine($"[REC] video conversion failed, source left: {sourcePath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[REC] convert error: " + ex.Message);
            }
            finally
            {
                Interlocked.Decrement(ref _backgroundVideoConversions);
            }
        });
    }

    private static async Task<bool> ConvertRawMjpegAsync(string sourcePath, string finalPath, string finalFormat, int framerate)
    {
        framerate = Math.Clamp(framerate, 1, 60);

        var args = finalFormat == "mp4"
            ? $"-y -f mjpeg -framerate {framerate} -i \"{sourcePath}\" -c:v libx264 -pix_fmt yuv420p -movflags +faststart \"{finalPath}\""
            : $"-y -f mjpeg -framerate {framerate} -i \"{sourcePath}\" -c:v mjpeg -q:v 3 \"{finalPath}\"";

        var psi = new ProcessStartInfo("ffmpeg", args)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var stderr = await stderrTask;
        var stdout = await stdoutTask;

        if (process.ExitCode != 0)
        {
            if (!string.IsNullOrWhiteSpace(stderr))
                Console.WriteLine("[FFMPEG] " + stderr.Trim());
            else if (!string.IsNullOrWhiteSpace(stdout))
                Console.WriteLine("[FFMPEG] " + stdout.Trim());
            return false;
        }

        return File.Exists(finalPath);
    }

    private static void WritePreviewRecordingFrameIfNeeded(byte[] rgb, int width, int height)
    {
        bool random;
        DateTime started;
        int targetFps;

        lock (_previewRecordLock)
        {
            if (!_previewRecording || _previewRecordStream is null)
                return;

            random = _previewRandomRecording;
            started = _previewRecordStartedUtc;
            targetFps = _previewFps;
        }

        var now = DateTime.UtcNow;
        var elapsed = now - started;

        if (random)
        {
            if (elapsed.TotalSeconds >= _randomFrameSeconds)
            {
                var result = ClosePreviewRecordingCore();
                if (result.sourcePath is not null && result.finalPath is not null)
                {
                    QueueVideoConversion(result.sourcePath, result.finalPath, result.finalFormat, _previewFps);
                    Console.WriteLine("[REC] random preview conversion started");
                }
                else
                {
                    Console.WriteLine("[REC] random preview finished");
                }
                return;
            }

            var second = (int)Math.Floor(elapsed.TotalSeconds);
            lock (_previewRecordLock)
            {
                if (second != _previewRandomSecond)
                {
                    _previewRandomSecond = second;
                    _previewRandomFps = _randomFrameRandom.Next(_randomFrameMinFps, _randomFrameMaxFps + 1);
                    Console.WriteLine($"[REC] random second {second + 1}/{_randomFrameSeconds}, fps {_previewRandomFps}");
                }

                targetFps = _previewRandomFps;
            }
        }

        targetFps = Math.Clamp(targetFps, 1, 30);
        var minGapMs = 1000.0 / targetFps;

        lock (_previewRecordLock)
        {
            if ((now - _previewLastFrameWrittenUtc).TotalMilliseconds < minGapMs)
                return;

            _previewLastFrameWrittenUtc = now;
        }

        // Nie kodujemy JPG synchronicznie w pętli podglądu, bo to robi glitche.
        // Jeżeli poprzednia klatka jeszcze się koduje, tę pomijamy zamiast psuć stream.
        if (Interlocked.CompareExchange(ref _recordEncodeBusy, 1, 0) != 0)
        {
            Interlocked.Increment(ref _recordFramesDropped);
            return;
        }

        var copy = rgb.ToArray();

        _ = Task.Run(() =>
        {
            try
            {
                var jpeg = EncodePreviewFrameJpeg(copy, width, height);

                lock (_previewRecordLock)
                {
                    if (_previewRecording && _previewRecordStream is not null)
                        _previewRecordStream.Write(jpeg, 0, jpeg.Length);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[REC] frame write error: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _recordEncodeBusy, 0);
            }
        });
    }



    private static byte[] EncodePreviewFrameJpeg(byte[] rgb, int srcW, int srcH)
    {
        using var image = new Image<Rgb24>(srcW, srcH);

        var pixel = Math.Clamp(_previewSettings.PreviewPixelSize, 1, 32);
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

        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms, new JpegEncoder { Quality = Math.Clamp(_jpgQuality, 50, 95) });
        return ms.ToArray();
    }

    private static void AddSensorArgs(List<string> args, bool still)
    {
        // HQ Camera IMX477 common modes. rpicam may ignore if unsupported.
        if (_sensorMode == "full")
            args.AddRange(new[] { "--mode", "4056:3040" });
        else if (_sensorMode == "bin")
            args.AddRange(new[] { "--mode", "2028:1520" });
        else if (_sensorMode == "fast")
            args.AddRange(new[] { "--mode", "1280:960" });
    }

    private static async Task RunProcessAsync(string file, List<string> args, int timeoutMs)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = file,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            }
        };

        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        process.Start();

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"{file} timeout");
        }

        if (process.ExitCode != 0)
        {
            var err = await process.StandardError.ReadToEndAsync();
            throw new Exception($"{file} exit {process.ExitCode}: {err}");
        }
    }

    private static void HandleTouch(int x, int y, int width, int height, CameraPreviewService preview, FramebufferDisplay display, string outputDir)
    {
        if (DateTime.UtcNow < _ignoreTouchUntilUtc)
            return;

        if (HandleTabTouch(x, y, width, height, preview, display, outputDir))
            return;

        if (_tab == Tab.Preview)
        {
            if (_previewReadyForTouch && y >= 28 && y < height - 44)
                _captureRequested = true;
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

        var part = width / 5;
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
        else if (x < part * 4)
        {
            _tab = Tab.Network;
            preview.Stop();
            DrawNetwork(display, width, height);
        }
        else
        {
            _tab = Tab.Info;
            preview.Stop();
            DrawInfo(display, width, height, outputDir);
        }

        return true;
    }

    private static void HandleModeTouch(int x, int y, int width, int height, FramebufferDisplay display)
    {
        if (y >= height - 44 - 38 && y < height - 44)
        {
            _modePage = (_modePage + 1) % 8;
            DrawMode(display, width, height);
            return;
        }

        var dir = x < width / 2 ? -1 : 1;

        if (_modePage == 0)
        {
            _lookPreset = NextLookPreset(_lookPreset, dir);
            ApplyLookPreset(_lookPreset);
            _selectedColorAmount = ClosestColorChoice(_previewSettings.PreviewColorLevels);
            _manualColorAmount = false;
        }
        else if (_modePage == 1)
        {
            // Bezpieczniejsza obsługa dla małego rezystancyjnego TFT.
            // Góra zmienia ilość kolorów, dół zmienia tryb palety.
            if (y < 108)
            {
                _selectedColorAmount = NextColorAmount(_selectedColorAmount, dir);
                SetPreviewColors(_selectedColorAmount);
                _manualColorAmount = true;
            }
            else
            {
                _paletteMode = NextPaletteMode(_paletteMode, dir);
            }
        }
        else if (_modePage == 2)
        {
            var row = (y - 58) / 40;
            switch (row)
            {
                case 0:
                    _redScale = ClampRound(_redScale + dir * 0.1, 0.0, 2.0);
                    break;
                case 1:
                    _greenScale = ClampRound(_greenScale + dir * 0.1, 0.0, 2.0);
                    break;
                case 2:
                    _blueScale = ClampRound(_blueScale + dir * 0.1, 0.0, 2.0);
                    break;
                case 3:
                    _redScale = 1.0;
                    _greenScale = 1.0;
                    _blueScale = 1.0;
                    break;
            }
        }
        else if (_modePage == 3)
        {
            var row = (y - 58) / 40;
            switch (row)
            {
                case 0:
                    _photoSource = NextPhotoSource(_photoSource, dir);
                    break;
                case 1:
                    _photoFormat = NextValue(_photoFormat, new[] { "jpg", "png", "bmp", "raw", "rawjpg" }, dir);
                    break;
                case 2:
                    _photoWidth = Math.Clamp(_photoWidth + dir * 16, 320, 4056);
                    break;
                case 3:
                    _photoHeight = Math.Clamp(_photoHeight + dir * 16, 240, 3040);
                    break;
            }
        }
        else if (_modePage == 4)
        {
            var row = (y - 60) / 40;
            switch (row)
            {
                case 0:
                    _captureKind = NextCaptureKind(_captureKind, dir);
                    break;
                case 1:
                    _videoFormat = NextValue(_videoFormat, new[] { "mjpeg", "mp4" }, dir);
                    break;
                case 2:
                    _sensorMode = NextValue(_sensorMode, new[] { "full", "bin", "fast" }, dir);
                    break;
                case 3:
                    _photoEv = Math.Round(Math.Clamp(_photoEv + dir * 0.5, -4.0, 2.0), 1);
                    break;
            }
        }
        else if (_modePage == 5)
        {
            var row = (y - 58) / 34;
            switch (row)
            {
                case 0:
                    _randomFrameMinFps = Math.Clamp(_randomFrameMinFps + dir, 1, 30);
                    if (_randomFrameMinFps > _randomFrameMaxFps)
                        _randomFrameMaxFps = _randomFrameMinFps;
                    break;
                case 1:
                    _randomFrameMaxFps = Math.Clamp(_randomFrameMaxFps + dir, 1, 30);
                    if (_randomFrameMaxFps < _randomFrameMinFps)
                        _randomFrameMinFps = _randomFrameMaxFps;
                    break;
                case 2:
                    _randomFrameSeconds = Math.Clamp(_randomFrameSeconds + dir, 1, 120);
                    break;
                case 3:
                    _videoFormat = NextValue(_videoFormat, new[] { "mjpeg", "mp4" }, dir);
                    break;
                case 4:
                    _sensorMode = NextValue(_sensorMode, new[] { "full", "bin", "fast" }, dir);
                    break;
            }
        }
        else if (_modePage == 6)
        {
            _lookPreset = "CUSTOM";
            var row = (y - 58) / 34;
            switch (row)
            {
                case 0:
                    _previewSettings.Ev = ClampRound(_previewSettings.Ev + dir * 0.1, -4.0, 2.0);
                    break;
                case 1:
                    _previewSettings.BlackLevel = Math.Clamp(_previewSettings.BlackLevel + dir * 5, 0, 180);
                    break;
                case 2:
                    _previewSettings.DarkLevel = ClampRound(_previewSettings.DarkLevel + dir * 0.05, 0.3, 1.5);
                    break;
                case 3:
                    _previewSettings.PreviewPixelSize = Math.Clamp(_previewSettings.PreviewPixelSize + dir, 1, 16);
                    break;
                case 4:
                    _selectedColorAmount = NextColorAmount(_selectedColorAmount, dir);
                    SetPreviewColors(_selectedColorAmount);
                    _manualColorAmount = true;
                    break;
            }
        }
        else if (_modePage == 7)
        {
            _lookPreset = "CUSTOM";
            var row = (y - 58) / 34;
            switch (row)
            {
                case 0:
                    _previewSettings.Contrast = ClampRound(_previewSettings.Contrast + dir * 0.1, 0.0, 2.0);
                    break;
                case 1:
                    _previewSettings.Saturation = ClampRound(_previewSettings.Saturation + dir * 0.1, 0.0, 2.0);
                    break;
                case 2:
                    _previewSettings.Brightness = ClampRound(_previewSettings.Brightness + dir * 0.1, -1.0, 1.0);
                    break;
                case 3:
                    _previewSettings.Sharpness = ClampRound(_previewSettings.Sharpness + dir * 0.1, -1.0, 2.0);
                    break;
                case 4:
                    _jpgQuality = Math.Clamp(_jpgQuality + dir * 5, 70, 100);
                    break;
            }
        }

        DrawMode(display, width, height);
    }









    private static void DrawMode(FramebufferDisplay display, int width, int height)
    {
        display.Clear(0x0000);

        if (_modePage == 0)
        {
            display.DrawTextScaled("LOOK", 8, 8, 0xFFFF, 2);
            display.DrawCenteredTextScaled(_lookPreset, 72, 0xFFE0, 3);
            display.DrawText("LEWA/PRAWA STRONA ZMIENIA", 16, 124, 0xFFFF);
            display.DrawText("NORMAL LOW32 LOW16 RETRO8 MONO4", 12, 146, 0xC618);
            display.DrawText("CUSTOM = RECZNE USTAWIENIA", 40, 162, 0xC618);
            DrawBigMinusPlus(display, width, 178);
        }
        else if (_modePage == 1)
        {
            display.DrawTextScaled("PALETA", 8, 8, 0xFFFF, 2);
            DrawSettingRow(display, 64, width, "KOLORY", _selectedColorAmount.ToString());
            DrawSettingRow(display, 110, width, "TRYB", PaletteModeLabel(_paletteMode));
            display.DrawText("DZIALA DLA LOW16 I LOW32", 52, 154, 0xFFFF);
            display.DrawText("GREEN/YELLOW=mono odcienie", 42, 176, 0xC618);
            display.DrawText("BALANCED=mniej zielonego", 62, 194, 0xC618);
        }
        else if (_modePage == 2)
        {
            display.DrawTextScaled("SKALA KOLOROW", 8, 8, 0xFFFF, 2);
            display.DrawText("LEWO/PRAWO = -/+ 0.1", 28, 32, 0xC618);
            DrawSettingRow(display, 66, width, "RED", _redScale.ToString("0.0"));
            DrawSettingRow(display, 106, width, "GREEN", _greenScale.ToString("0.0"));
            DrawSettingRow(display, 146, width, "BLUE", _blueScale.ToString("0.0"));
            DrawSettingRow(display, 186, width, "RESET", "1.0");
        }
        else if (_modePage == 3)
        {
            display.DrawTextScaled("FOTO", 8, 8, 0xFFFF, 2);
            var y = 58;
            DrawSettingRow(display, y, width, "SOURCE", PhotoSourceLabel()); y += 40;
            DrawSettingRow(display, y, width, "FORMAT", _photoFormat.ToUpperInvariant()); y += 40;
            DrawSettingRow(display, y, width, "WIDTH", _photoWidth.ToString()); y += 40;
            DrawSettingRow(display, y, width, "HEIGHT", _photoHeight.ToString());
            display.DrawText("FULL HQ domyslnie 4056x3040", 20, 222, 0xC618);
        }
        else if (_modePage == 4)
        {
            display.DrawTextScaled("VIDEO/SENSOR", 8, 8, 0xFFFF, 2);
            DrawSettingRow(display, 60, width, "TRYB", CaptureKindLabel(_captureKind));
            DrawSettingRow(display, 100, width, "VIDEO", VideoFormatLabel());
            DrawSettingRow(display, 140, width, "SENSOR", SensorLabel(_sensorMode));
            DrawSettingRow(display, 180, width, "FOTO EV", _photoEv.ToString("0.0"));
            display.DrawText("MJPEG=AVI / MP4 po STOP", 54, 220, 0xC618);
        }
        else if (_modePage == 5)
        {
            display.DrawTextScaled("RANDOM FRAME", 8, 8, 0xFFFF, 2);
            display.DrawText("LOSOWY FPS CO 1 SEKUNDE", 8, 31, 0xC618);
            var y = 58;
            DrawSettingRow(display, y, width, "MIN FPS", _randomFrameMinFps.ToString()); y += 34;
            DrawSettingRow(display, y, width, "MAX FPS", _randomFrameMaxFps.ToString()); y += 34;
            DrawSettingRow(display, y, width, "SEK", _randomFrameSeconds.ToString()); y += 34;
            DrawSettingRow(display, y, width, "VIDEO", VideoFormatLabel()); y += 34;
            DrawSettingRow(display, y, width, "SENSOR", SensorLabel(_sensorMode));
            display.DrawText("MJPEG=AVI / MP4 po STOP", 54, 228, 0xC618);
        }
        else if (_modePage == 6)
        {
            display.DrawTextScaled("CUSTOM 1/2", 8, 8, 0xFFFF, 2);
            display.DrawText("LIVE PREVIEW", 8, 31, 0xC618);
            var y = 58;
            DrawSettingRow(display, y, width, "EV", _previewSettings.Ev.ToString("0.0")); y += 34;
            DrawSettingRow(display, y, width, "BLACK", _previewSettings.BlackLevel.ToString()); y += 34;
            DrawSettingRow(display, y, width, "DARK", _previewSettings.DarkLevel.ToString("0.00")); y += 34;
            DrawSettingRow(display, y, width, "PIXEL", _previewSettings.PreviewPixelSize.ToString()); y += 34;
            DrawSettingRow(display, y, width, "COLORS", _previewSettings.PreviewColorLevels.ToString());
        }
        else
        {
            display.DrawTextScaled("CUSTOM 2/2", 8, 8, 0xFFFF, 2);
            display.DrawText("KOLOR/JAKOSC", 8, 31, 0xC618);
            var y = 58;
            DrawSettingRow(display, y, width, "CONTR", _previewSettings.Contrast.ToString("0.0")); y += 34;
            DrawSettingRow(display, y, width, "SAT", _previewSettings.Saturation.ToString("0.0")); y += 34;
            DrawSettingRow(display, y, width, "BRIGHT", _previewSettings.Brightness.ToString("0.0")); y += 34;
            DrawSettingRow(display, y, width, "SHARP", _previewSettings.Sharpness.ToString("0.0")); y += 34;
            DrawSettingRow(display, y, width, "JPG Q", _jpgQuality.ToString());
        }

        DrawModePageButton(display, width, height);
        DrawTabs(display, width, height);
        display.Flush();
    }









    private static void DrawModePageButton(FramebufferDisplay display, int width, int height)
    {
        var y = height - 44 - 38;
        display.FillRect(8, y, width - 16, 34, 0x2104);
        var label = _modePage == 0 ? "DALEJ: ILOSC KOLOROW" :
                    _modePage == 1 ? "DALEJ: SKALA KOLOROW" :
                    _modePage == 2 ? "DALEJ: FORMAT FOTO" :
                    _modePage == 3 ? "DALEJ: VIDEO/SENSOR" :
                    _modePage == 4 ? "DALEJ: RANDOM FRAME" :
                    _modePage == 5 ? "DALEJ: CUSTOM 1/2" :
                    _modePage == 6 ? "DALEJ: CUSTOM 2/2" :
                    "WROC: LOOK";
        display.DrawCenteredText(label, y + 12, 0xFFFF);
    }









    private static void DrawBigMinusPlus(FramebufferDisplay display, int width, int y)
    {
        display.FillRect(28, y, 120, 50, 0x4208);
        display.DrawTextScaled("-", 76, y + 15, 0xFFFF, 3);
        display.FillRect(width - 148, y, 120, 50, 0x4208);
        display.DrawTextScaled("+", width - 100, y + 15, 0xFFFF, 3);
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

    private static void RefreshGallery(string outputDir)
    {
        _galleryFiles = Directory.Exists(outputDir)
            ? Directory.GetFiles(outputDir)
                .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".dng", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTime)
                .ToList()
            : new List<string>();

        if (_galleryFiles.Count == 0)
            _galleryIndex = 0;
        else
            _galleryIndex = Math.Clamp(_galleryIndex, 0, _galleryFiles.Count - 1);
    }

    private static void DrawGallery(FramebufferDisplay display, int width, int height, string outputDir)
    {
        RefreshGallery(outputDir);
        display.Clear(0x0000);
        display.FillRect(0, 0, width, 44, 0x2104);
        display.FillRect(0, 0, 112, 44, 0x4208);
        display.FillRect(width - 112, 0, 112, 44, 0x4208);
        display.DrawTextScaled("<", 44, 15, 0xFFFF, 2);
        display.DrawTextScaled(">", width - 68, 15, 0xFFFF, 2);

        if (_galleryFiles.Count == 0)
        {
            display.DrawCenteredTextScaled("BRAK ZDJEC", height / 2 - 20, 0xFFFF, 2);
            DrawTabs(display, width, height);
            display.Flush();
            return;
        }

        var file = _galleryFiles[_galleryIndex];
        display.DrawText($"{_galleryIndex + 1}/{_galleryFiles.Count}", 210, 16, 0xFFFF);

        if (file.EndsWith(".dng", StringComparison.OrdinalIgnoreCase))
        {
            display.DrawCenteredTextScaled("RAW/DNG", 100, 0xFFE0, 2);
            display.DrawCenteredText("PODGLAD RAW NIEOBSUGIWANY", 135, 0xFFFF);
        }
        else
        {
            try
            {
                var rgb = ImageLoader.LoadJpegRgb(file, out var imgW, out var imgH);
                display.DrawRgbFrameScaledKeepAspect(rgb, imgW, imgH, 0, 46, width, height - 46 - 44);
            }
            catch (Exception ex)
            {
                display.DrawCenteredTextScaled("BLAD PLIKU", 90, 0xF800, 2);
                display.DrawWrappedText(ex.Message, 8, 130, 0xFFFF);
            }
        }

        display.FillRect(0, height - 64, width, 20, 0x0000);
        display.DrawText(Path.GetFileName(file), 8, height - 58, 0xFFFF);
        DrawTabs(display, width, height);
        display.Flush();
    }

    private static void DrawNetwork(FramebufferDisplay display, int width, int height)
    {
        display.Clear(0x0000);

        if (_networkPage == 0)
        {
            display.DrawTextScaled("SIEC 1/3", 8, 8, 0xFFFF, 2);
            display.DrawText("WIFI KLIENT", 8, 42, 0xFFE0);
            display.DrawText("SSID:", 8, 74, 0xC618);
            display.DrawText(string.IsNullOrWhiteSpace(_wifiSsid) ? "(BRAK)" : _wifiSsid, 80, 74, 0xFFFF);
            display.DrawText("HASLO:", 8, 98, 0xC618);
            display.DrawText(string.IsNullOrWhiteSpace(_wifiPassword) ? "(EXECSTART)" : "********", 80, 98, 0xFFFF);
            display.DrawText("KONFIGURUJ PRZEZ NMCLI", 8, 140, 0xC618);
        }
        else if (_networkPage == 1)
        {
            display.DrawTextScaled("SIEC 2/3", 8, 8, 0xFFFF, 2);
            display.DrawText("HOTSPOT", 8, 42, 0xFFE0);
            display.DrawText("SSID:", 8, 74, 0xC618);
            display.DrawText(_hotspotSsid, 80, 74, 0xFFFF);
            display.DrawText("HASLO:", 8, 98, 0xC618);
            display.DrawText(_hotspotPassword, 80, 98, 0xFFFF);
            display.DrawText("NMCLI HOTSPOT W README", 8, 140, 0xC618);
        }
        else
        {
            display.DrawTextScaled("SIEC 3/3", 8, 8, 0xFFFF, 2);
            display.DrawText("ETHERNET", 8, 42, 0xFFE0);
            display.DrawText("DHCP ZWYKLE AUTO", 8, 76, 0xFFFF);
            display.DrawText("SPRAWDZ: ip addr show eth0", 8, 112, 0xC618);
        }

        var y = height - 44 - 38;
        display.FillRect(8, y, width - 16, 34, 0x2104);
        display.DrawCenteredText(_networkPage == 0 ? "DALEJ: HOTSPOT" :
                                 _networkPage == 1 ? "DALEJ: ETH" :
                                 "WROC: WIFI", y + 12, 0xFFFF);
        DrawTabs(display, width, height);
        display.Flush();
    }

    private static void HandleNetworkTouch(int x, int y, int width, int height, FramebufferDisplay display)
    {
        if (y >= height - 44 - 42 && y < height - 44)
        {
            _networkPage = (_networkPage + 1) % 3;
            DrawNetwork(display, width, height);
        }
    }

    private static void DrawInfo(FramebufferDisplay display, int width, int height, string outputDir)
    {
        display.Clear(0x0000);
        display.DrawTextScaled("INFO", 8, 8, 0xFFFF, 2);
        display.DrawText("POD: DOTKNIJ OBRAZU = FOTO/REC", 8, 44, 0xFFFF);
        display.DrawText("TRYB: LOOK / RAW / VIDEO / RANDOM", 8, 66, 0xFFFF);
        display.DrawText("KOLOR LCD: --swap-rb=true gdy jasne wpada w czerwien", 8, 84, 0xC618);
        display.DrawText("SIEC: WIFI HOTSPOT ETH", 8, 88, 0xFFFF);
        display.DrawText("1 POD 2 TRYB 3 GAL 4 SIEC 5 INFO", 8, 110, 0xC618);
        display.DrawText("ZDJECIA:", 8, 150, 0xFFE0);
        display.DrawText(outputDir.ToUpperInvariant(), 8, 170, 0xFFFF);
        DrawTabs(display, width, height);
        display.Flush();
    }

    private static void RedrawNonPreview(FramebufferDisplay display, int width, int height, string outputDir)
    {
        if (_tab == Tab.Mode)
            DrawMode(display, width, height);
        else if (_tab == Tab.Gallery)
            DrawGallery(display, width, height, outputDir);
        else if (_tab == Tab.Network)
            DrawNetwork(display, width, height);
        else if (_tab == Tab.Info)
            DrawInfo(display, width, height, outputDir);
    }

    private static void DrawTopBar(FramebufferDisplay display, int width)
    {
        display.FillRect(0, 0, width, 28, 0x0000);
        display.DrawTextScaled(_recording ? "REC" : "LIVE", 8, 8, _recording ? (ushort)0xF800 : (ushort)0x07E0, 2);
        display.DrawText(CaptureKindLabel(_captureKind), 70, 10, 0xFFFF);
        display.DrawText(_lookPreset, 130, 10, 0xFFE0);
        if (_previewRecording)
            display.DrawText(_previewRandomRecording ? "RND" : "REC", 220, 10, 0xF800);
        else if (_backgroundSaveJobs > 0)
            display.DrawText("SAVE", 220, 10, 0xFFE0);
        display.DrawText(DateTime.Now.ToString("HH:mm"), width - 38, 10, 0xFFFF);
    }

    private static void DrawTabs(FramebufferDisplay display, int width, int height)
    {
        var y = height - 44;
        var w = width / 5;
        DrawTab(display, 0, y, w, 44, "POD", _tab == Tab.Preview);
        DrawTab(display, w, y, w, 44, "TRYB", _tab == Tab.Mode);
        DrawTab(display, w * 2, y, w, 44, "GAL", _tab == Tab.Gallery);
        DrawTab(display, w * 3, y, w, 44, "SIEC", _tab == Tab.Network);
        DrawTab(display, w * 4, y, width - w * 4, 44, "INFO", _tab == Tab.Info);
    }

    private static void DrawTab(FramebufferDisplay display, int x, int y, int w, int h, string text, bool active)
    {
        display.FillRect(x, y, w, h, active ? (ushort)0x03E0 : (ushort)0x2104);
        display.DrawText(text, x + 10, y + 16, 0xFFFF);
    }

    private static void DrawSettingRow(FramebufferDisplay display, int y, int width, string name, string value)
    {
        display.FillRect(8, y - 4, width - 16, 30, 0x1082);
        display.FillRect(8, y - 4, 50, 30, 0x4208);
        display.DrawTextScaled("-", 25, y + 3, 0xFFFF, 2);
        display.DrawTextScaled(name, 70, y + 3, 0xFFE0, 1);
        display.DrawTextScaled(value, 220, y + 3, 0xFFFF, 1);
        display.FillRect(width - 60, y - 4, 52, 30, 0x4208);
        display.DrawTextScaled("+", width - 43, y + 3, 0xFFFF, 2);
    }

    private static void DrawBusy(FramebufferDisplay display, int width, int height, string text)
    {
        display.Clear(0x0000);
        display.DrawCenteredTextScaled(text, height / 2 - 16, 0xFFE0, 2);
        display.Flush();
    }

    private static void DrawSaved(FramebufferDisplay display, int width, int height, string text)
    {
        display.Clear(0x0000);
        display.DrawCenteredTextScaled("OK", height / 2 - 30, 0x07E0, 2);
        display.DrawCenteredText(text, height / 2 + 4, 0xFFFF);
        display.Flush();
    }

    private static void DrawError(FramebufferDisplay display, int width, int height, string error)
    {
        display.Clear(0x0000);
        display.DrawCenteredTextScaled("BLAD", 35, 0xF800, 2);
        display.DrawWrappedText(error, 8, 80, 0xFFFF);
        DrawTabs(display, width, height);
        display.Flush();
    }

    private static void KeyboardLoop(CameraPreviewService preview, FramebufferDisplay display, int width, int height, string outputDir)
    {
        while (_running)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter || key.Key == ConsoleKey.Spacebar)
                _captureRequested = true;

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

            if (key.Key == ConsoleKey.D5)
            {
                _tab = Tab.Info;
                preview.Stop();
                DrawInfo(display, width, height, outputDir);
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
                _previewSettings.PreviewPixelSize = 1;
                _previewSettings.PreviewColorLevels = 256;
                _previewSettings.Contrast = 0.75;
                _previewSettings.Saturation = 0.85;
                break;
            case "LOW32":
                _sensorMode = "bin";
                _previewSettings.Ev = -1.2;
                _previewSettings.BlackLevel = 25;
                _previewSettings.DarkLevel = 0.95;
                _previewSettings.PreviewPixelSize = 4;
                _previewSettings.PreviewColorLevels = 32;
                _previewSettings.Contrast = 0.80;
                _previewSettings.Saturation = 0.85;
                break;
            case "LOW16":
                _sensorMode = "fast";
                _previewSettings.Ev = -1.4;
                _previewSettings.BlackLevel = 28;
                _previewSettings.DarkLevel = 0.92;
                _previewSettings.PreviewPixelSize = 6;
                _previewSettings.PreviewColorLevels = 16;
                _previewSettings.Contrast = 0.85;
                _previewSettings.Saturation = 0.80;
                break;
            case "RETRO8":
                _sensorMode = "fast";
                _previewSettings.Ev = -1.6;
                _previewSettings.BlackLevel = 60;
                _previewSettings.DarkLevel = 0.72;
                _previewSettings.PreviewPixelSize = 8;
                _previewSettings.PreviewColorLevels = 8;
                _previewSettings.Contrast = 0.90;
                _previewSettings.Saturation = 0.75;
                break;
            case "MONO4":
                _sensorMode = "fast";
                _previewSettings.Ev = -1.6;
                _previewSettings.BlackLevel = 65;
                _previewSettings.DarkLevel = 0.70;
                _previewSettings.PreviewPixelSize = 8;
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
        var values = new[] { CaptureKind.Photo, CaptureKind.Video, CaptureKind.RandomFrame };
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
    }
}
