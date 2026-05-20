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
    private static int _previewFps = 20;
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
        var invertX = BoolArg(args, "--invert-x=", false);
        var invertY = BoolArg(args, "--invert-y=", true);
        var apiEnabled = BoolArg(args, "--api=", true);
        var apiUrl = Arg(args, "--api-url=", "http://0.0.0.0:5000");

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

        if (apiEnabled)
            _ = Task.Run(() => StartApiServerAsync(apiUrl, outputDir, preview));

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

        if (_photoSource == PhotoSource.Preview && !isRaw)
        {
            if (TrySaveCurrentPreviewFrame(finalPath, _photoFormat))
                return finalPath;
        }

        if (!isRaw)
        {
            return await TakeFullHqPhotoAsync(outputDir, finalPath);
        }

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


    private static async Task StartApiServerAsync(string apiUrl, string outputDir, CameraPreviewService preview)
    {
        _apiStartedUtc = DateTime.UtcNow;

        try
        {
            var builder = WebApplication.CreateBuilder(Array.Empty<string>());
            builder.WebHost.UseUrls(apiUrl);

            var app = builder.Build();

            app.MapGet("/", () => Results.Text(ApiHomeHtml(), "text/html; charset=utf-8"));

            app.MapGet("/api/status", () => Results.Ok(new
            {
                ok = true,
                running = _running,
                busy = _isBusy,
                recording = _previewRecording,
                randomRecording = _previewRandomRecording,
                previewReady = _lastPreviewRgb is not null,
                tab = _tab.ToString(),
                captureKind = _captureKind.ToString(),
                photoSource = _photoSource.ToString(),
                photoFormat = _photoFormat,
                videoFormat = _videoFormat,
                lookPreset = _lookPreset,
                paletteMode = _paletteMode.ToString(),
                lastCaptured = _lastCapturedPath is null ? null : Path.GetFileName(_lastCapturedPath),
                uptimeSeconds = (int)(DateTime.UtcNow - _apiStartedUtc).TotalSeconds
            }));

            app.MapGet("/api/preview.jpg", (bool? raw, int? q) =>
            {
                var jpeg = CreatePreviewJpeg(raw == true, q ?? 55);
                return jpeg is null
                    ? Results.NotFound(new { ok = false, message = "Preview not ready" })
                    : Results.File(jpeg, "image/jpeg", enableRangeProcessing: false);
            });

            app.MapGet("/api/stream.mjpg", async (HttpContext context, bool? raw, int? q, int? fps) =>
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "multipart/x-mixed-replace; boundary=frame";
                context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate, max-age=0";
                context.Response.Headers.Pragma = "no-cache";
                context.Response.Headers.Expires = "0";

                var useRaw = raw ?? false;
                var quality = Math.Clamp(q ?? 50, 35, 80);
                var targetFps = Math.Clamp(fps ?? 15, 1, 30);
                var delayMs = Math.Max(1, 1000 / targetFps);
                var token = context.RequestAborted;

                while (!token.IsCancellationRequested && _running)
                {
                    var jpeg = CreatePreviewJpeg(useRaw, quality);
                    if (jpeg is not null)
                    {
                        await context.Response.WriteAsync("--frame\r\n", token);
                        await context.Response.WriteAsync("Content-Type: image/jpeg\r\n", token);
                        await context.Response.WriteAsync($"Content-Length: {jpeg.Length}\r\n\r\n", token);
                        await context.Response.Body.WriteAsync(jpeg, token);
                        await context.Response.WriteAsync("\r\n", token);
                        await context.Response.Body.FlushAsync(token);
                    }

                    try
                    {
                        await Task.Delay(delayMs, token);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                }
            });

            app.MapPost("/api/capture", () =>
            {
                if (_isBusy)
                    return Results.Conflict(new { ok = false, message = "Camera busy" });

                _captureKind = CaptureKind.Photo;
                _captureRequested = true;
                return Results.Ok(new { ok = true, message = "Capture requested" });
            });

            app.MapPost("/api/video/toggle", () =>
            {
                if (_isBusy)
                    return Results.Conflict(new { ok = false, message = "Camera busy" });

                _captureKind = CaptureKind.Video;
                _captureRequested = true;
                return Results.Ok(new { ok = true, recording = !_previewRecording });
            });

            app.MapGet("/api/photos", () =>
            {
                Directory.CreateDirectory(outputDir);

                var files = Directory.GetFiles(outputDir)
                    .Where(IsMediaFile)
                    .OrderByDescending(File.GetCreationTime)
                    .Select(path => new
                    {
                        name = Path.GetFileName(path),
                        size = new FileInfo(path).Length,
                        created = File.GetCreationTime(path),
                        url = "/api/photos/" + Uri.EscapeDataString(Path.GetFileName(path))
                    })
                    .ToList();

                return Results.Ok(files);
            });

            app.MapGet("/api/photos/{file}", (string file) =>
            {
                var safeName = Path.GetFileName(Uri.UnescapeDataString(file));
                var path = Path.Combine(outputDir, safeName);

                if (!File.Exists(path) || !IsMediaFile(path))
                    return Results.NotFound();

                return Results.File(path, ContentTypeFor(path), enableRangeProcessing: true);
            });

            app.MapDelete("/api/photos/{file}", (string file) =>
            {
                var safeName = Path.GetFileName(Uri.UnescapeDataString(file));
                var path = Path.Combine(outputDir, safeName);

                if (!File.Exists(path) || !IsMediaFile(path))
                    return Results.NotFound(new { ok = false, message = "File not found" });

                File.Delete(path);
                return Results.Ok(new { ok = true });
            });

            app.MapGet("/api/settings", () => Results.Ok(CurrentApiSettings()));

            app.MapPost("/api/settings", async (HttpRequest request) =>
            {
                using var doc = await JsonDocument.ParseAsync(request.Body);
                ApplyApiSettings(doc.RootElement, preview);
                return Results.Ok(CurrentApiSettings());
            });

            app.MapGet("/api/settings/options", () => Results.Ok(new
            {
                lookPresets = new[] { "NORMAL", "LOW32", "LOW16", "RETRO8", "MONO4" },
                captureKinds = new[] { "Photo", "Video", "RandomFrame" },
                photoSources = new[] { "FullHq", "Preview" },
                photoFormats = new[] { "jpg", "png", "bmp", "raw", "rawjpg" },
                videoFormats = new[] { "mjpeg", "mp4" },
                sensorModes = new[] { "full", "bin", "fast" },
                paletteModes = Enum.GetNames<PaletteMode>(),
                denoise = new[] { "cdn_off", "cdn_fast", "cdn_hq" },
                colorChoices = _colorChoices
            }));

            Console.WriteLine($"[API] listening on {apiUrl}");
            await app.RunAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine("[API] " + ex);
        }
    }

    private static object CurrentApiSettings()
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
                previewFps = _previewFps,
                randomFrameMinFps = _randomFrameMinFps,
                randomFrameMaxFps = _randomFrameMaxFps,
                randomFrameSeconds = _randomFrameSeconds,
                sensorMode = _sensorMode,
                selectedColorAmount = _selectedColorAmount,
                paletteMode = _paletteMode.ToString(),
                redScale = _redScale,
                greenScale = _greenScale,
                blueScale = _blueScale,
                lowSaveGamma = _lowSaveGamma,
                lowGrayYellowFix = _lowGrayYellowFix,
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

    private static void ApplyApiSettings(JsonElement json, CameraPreviewService preview)
    {
        lock (_settingsLock)
        {
            if (TryGetString(json, "captureKind", out var captureKind) && Enum.TryParse<CaptureKind>(captureKind, true, out var ck))
                _captureKind = ck;

            if (TryGetString(json, "lookPreset", out var lookPreset))
                ApplyLookPreset(lookPreset);

            if (TryGetString(json, "photoFormat", out var photoFormat))
                _photoFormat = NextValue(photoFormat.ToLowerInvariant(), new[] { "jpg", "png", "bmp", "raw", "rawjpg" }, 0);

            if (TryGetString(json, "photoSource", out var photoSource) && Enum.TryParse<PhotoSource>(photoSource, true, out var ps))
                _photoSource = ps;

            if (TryGetInt(json, "photoWidth", out var photoWidth))
                _photoWidth = Math.Clamp(photoWidth, 320, 4056);

            if (TryGetInt(json, "photoHeight", out var photoHeight))
                _photoHeight = Math.Clamp(photoHeight, 240, 3040);

            if (TryGetInt(json, "jpgQuality", out var jpgQuality))
                _jpgQuality = Math.Clamp(jpgQuality, 70, 100);

            if (TryGetDouble(json, "photoEv", out var photoEv))
                _photoEv = Math.Clamp(photoEv, -8.0, 8.0);

            if (TryGetString(json, "videoFormat", out var videoFormat))
                _videoFormat = NormalizeVideoFormat(videoFormat);

            if (TryGetInt(json, "videoSeconds", out var videoSeconds))
                _videoSeconds = Math.Clamp(videoSeconds, 0, 3600);

            if (TryGetInt(json, "previewFps", out var previewFps))
                _previewFps = Math.Clamp(previewFps, 1, 30);

            if (TryGetInt(json, "randomFrameMinFps", out var randomFrameMinFps))
                _randomFrameMinFps = Math.Clamp(randomFrameMinFps, 1, 30);

            if (TryGetInt(json, "randomFrameMaxFps", out var randomFrameMaxFps))
                _randomFrameMaxFps = Math.Clamp(randomFrameMaxFps, 1, 30);

            if (_randomFrameMinFps > _randomFrameMaxFps)
                _randomFrameMaxFps = _randomFrameMinFps;

            if (TryGetInt(json, "randomFrameSeconds", out var randomFrameSeconds))
                _randomFrameSeconds = Math.Clamp(randomFrameSeconds, 1, 120);

            if (TryGetString(json, "sensorMode", out var sensorMode))
            {
                sensorMode = sensorMode.ToLowerInvariant();
                if (sensorMode is "full" or "bin" or "fast")
                    _sensorMode = sensorMode;
            }

            var rootColorAmountProvided = TryGetInt(json, "selectedColorAmount", out var selectedColorAmount) || TryGetInt(json, "previewColorLevels", out selectedColorAmount);
            if (rootColorAmountProvided)
            {
                SetPreviewColors(selectedColorAmount);
                _manualColorAmount = true;
            }

            if (TryGetString(json, "paletteMode", out var paletteMode))
                _paletteMode = ParsePaletteMode(paletteMode);

            if (TryGetDouble(json, "redScale", out var redScale))
                _redScale = ClampRound(redScale, 0.0, 2.0);

            if (TryGetDouble(json, "greenScale", out var greenScale))
                _greenScale = ClampRound(greenScale, 0.0, 2.0);

            if (TryGetDouble(json, "blueScale", out var blueScale))
                _blueScale = ClampRound(blueScale, 0.0, 2.0);

            if (TryGetDouble(json, "lowSaveGamma", out var lowSaveGamma))
                _lowSaveGamma = Math.Clamp(lowSaveGamma, 0.35, 2.5);

            if (TryGetInt(json, "lowGrayYellowFix", out var lowGrayYellowFix))
                _lowGrayYellowFix = Math.Clamp(lowGrayYellowFix, 0, 80);

            var previewJson = json.TryGetProperty("preview", out var p) && p.ValueKind == JsonValueKind.Object ? p : json;

            if (TryGetDouble(previewJson, "ev", out var ev)) _previewSettings.Ev = Math.Clamp(ev, -8.0, 8.0);
            if (TryGetDouble(previewJson, "sharpness", out var sharpness)) _previewSettings.Sharpness = Math.Clamp(sharpness, 0.0, 16.0);
            if (TryGetDouble(previewJson, "contrast", out var contrast)) _previewSettings.Contrast = Math.Clamp(contrast, 0.0, 32.0);
            if (TryGetDouble(previewJson, "saturation", out var saturation)) _previewSettings.Saturation = Math.Clamp(saturation, 0.0, 32.0);
            if (TryGetDouble(previewJson, "brightness", out var brightness)) _previewSettings.Brightness = Math.Clamp(brightness, -1.0, 1.0);
            if (TryGetInt(previewJson, "blackLevel", out var blackLevel)) _previewSettings.BlackLevel = Math.Clamp(blackLevel, 0, 240);
            if (TryGetDouble(previewJson, "darkLevel", out var darkLevel)) _previewSettings.DarkLevel = Math.Clamp(darkLevel, 0.25, 2.0);
            if (TryGetInt(previewJson, "previewPixelSize", out var pixelSize)) _previewSettings.PreviewPixelSize = Math.Clamp(pixelSize, 1, 32);
            if (!rootColorAmountProvided && TryGetInt(previewJson, "previewColorLevels", out var colorLevels)) SetPreviewColors(colorLevels);
            if (TryGetString(previewJson, "denoise", out var denoise) && !string.IsNullOrWhiteSpace(denoise)) _previewSettings.Denoise = denoise;

            preview.UpdateSettings(_previewSettings);
        }
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

    private static (int R, int G, int B) ApplyWebPreviewControls(int r, int g, int b)
    {
        // Te ustawienia mają być widoczne od razu w webowym podglądzie "Filtr".
        // EV z rpicam może wymagać restartu procesu kamery, więc tutaj dodajemy szybki
        // software-preview tylko dla MJPEG/web. Dzięki temu suwak EV reaguje na bieżąco.
        var evMul = Math.Pow(2.0, Math.Clamp(_previewSettings.Ev, -4.0, 4.0));
        var brightnessOffset = Math.Clamp(_previewSettings.Brightness, -1.0, 1.0) * 128.0;
        var contrast = Math.Clamp(_previewSettings.Contrast, 0.0, 32.0);
        var saturation = Math.Clamp(_previewSettings.Saturation, 0.0, 32.0);

        static int Tone(int v, double evMul, double brightnessOffset, double contrast)
        {
            var x = v * evMul;
            x = ((x - 128.0) * contrast) + 128.0 + brightnessOffset;
            return Math.Clamp((int)Math.Round(x), 0, 255);
        }

        r = Tone(r, evMul, brightnessOffset, contrast);
        g = Tone(g, evMul, brightnessOffset, contrast);
        b = Tone(b, evMul, brightnessOffset, contrast);

        if (Math.Abs(saturation - 1.0) > 0.001)
        {
            var gray = (r * 30 + g * 59 + b * 11) / 100.0;
            r = Math.Clamp((int)Math.Round(gray + (r - gray) * saturation), 0, 255);
            g = Math.Clamp((int)Math.Round(gray + (g - gray) * saturation), 0, 255);
            b = Math.Clamp((int)Math.Round(gray + (b - gray) * saturation), 0, 255);
        }

        return (r, g, b);
    }

    private static void FillImageWithCurrentLook(Image<Rgb24> image, byte[] rgb, int srcW, int srcH)
    {
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

                    var rawR = rgb[i];
                    var rawG = rgb[i + 1];
                    var rawB = rgb[i + 2];

                    var (toneR, toneG, toneB) = ApplyWebPreviewControls(rawR, rawG, rawB);

                    var r0 = ApplyBlackDarkSaved((byte)toneR, black, denom, dark);
                    var g0 = ApplyBlackDarkSaved((byte)toneG, black, denom, dark);
                    var b0 = ApplyBlackDarkSaved((byte)toneB, black, denom, dark);

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
    }

    private static bool TryGetString(JsonElement json, string name, out string value)
    {
        value = "";
        if (!json.TryGetProperty(name, out var prop)) return false;
        if (prop.ValueKind == JsonValueKind.String)
        {
            value = prop.GetString() ?? "";
            return true;
        }
        return false;
    }

    private static bool TryGetInt(JsonElement json, string name, out int value)
    {
        value = 0;
        if (!json.TryGetProperty(name, out var prop)) return false;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out value)) return true;
        if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out value)) return true;
        return false;
    }

    private static bool TryGetDouble(JsonElement json, string name, out double value)
    {
        value = 0;
        if (!json.TryGetProperty(name, out var prop)) return false;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out value)) return true;
        if (prop.ValueKind == JsonValueKind.String && double.TryParse(prop.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value)) return true;
        return false;
    }

    private static bool IsMediaFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".dng" or ".avi" or ".mp4" or ".mjpeg" or ".rawmjpeg";
    }

    private static string ContentTypeFor(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".bmp" => "image/bmp",
            ".dng" => "image/x-adobe-dng",
            ".avi" => "video/x-msvideo",
            ".mp4" => "video/mp4",
            ".mjpeg" or ".rawmjpeg" => "video/x-motion-jpeg",
            _ => "image/jpeg"
        };
    }

    private static string ApiHomeHtml() => """
<!doctype html>
<html lang="pl">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover,user-scalable=no">
<title>Pi Camera</title>
<style>
:root{
  --bg:#050505;
  --panel:#101010;
  --panel2:#171717;
  --line:#272727;
  --text:#f7f7f7;
  --muted:#9a9a9a;
  --accent:#ffffff;
  --danger:#ff4d4d;
  --safe-top:env(safe-area-inset-top,0px);
  --safe-bottom:env(safe-area-inset-bottom,0px);
}
*{box-sizing:border-box;-webkit-tap-highlight-color:transparent}
html,body{margin:0;width:100%;height:100%;background:#000;color:var(--text);font-family:system-ui,-apple-system,BlinkMacSystemFont,"Segoe UI",Arial,sans-serif;overflow:hidden}
body{display:flex;flex-direction:column;touch-action:manipulation}
button,select,input{font:inherit}
button{appearance:none;border:0;border-radius:18px;background:var(--panel2);color:var(--text);font-weight:850;min-height:48px;padding:12px 14px}
button:active{transform:scale(.98);filter:brightness(1.25)}
button.primary{background:#fff;color:#000}
button.ghost{background:rgba(255,255,255,.08);border:1px solid rgba(255,255,255,.09)}
button.danger{background:#2b1010;color:#fff;border:1px solid #6b2929}
button.round{width:58px;height:58px;border-radius:50%;padding:0;font-size:22px}
.hidden{display:none!important}

/* preview */
#app{height:100%;display:grid;grid-template-rows:1fr auto;background:#000}
#previewWrap{
  position:relative;
  min-height:0;
  width:100vw;
  background:#000;
  padding-top:var(--safe-top);
  display:grid;
  grid-template-columns:1fr;
  gap:0;
}
.view{position:relative;min-width:0;min-height:0;background:#000;overflow:hidden}
.view img{width:100%;height:100%;object-fit:contain;display:block;background:#000}
#previewWrap.dual{gap:3px;background:#111}
#previewWrap.dual.landscape{grid-template-columns:1fr 1fr}
#previewWrap.dual.portrait{grid-template-rows:1fr 1fr}
.badge{
  position:absolute;
  top:calc(10px + var(--safe-top));
  left:10px;
  background:rgba(0,0,0,.55);
  border:1px solid rgba(255,255,255,.12);
  backdrop-filter:blur(8px);
  padding:7px 10px;
  border-radius:999px;
  font-size:12px;
  font-weight:850;
}
.status{
  position:absolute;
  top:calc(10px + var(--safe-top));
  right:10px;
  max-width:62vw;
  background:rgba(0,0,0,.62);
  border:1px solid rgba(255,255,255,.12);
  backdrop-filter:blur(8px);
  padding:8px 10px;
  border-radius:999px;
  font-size:12px;
  font-weight:700;
  opacity:0;
  transform:translateY(-6px);
  transition:.18s;
  pointer-events:none;
  white-space:nowrap;
  overflow:hidden;
  text-overflow:ellipsis;
}
.status.show{opacity:1;transform:translateY(0)}

/* top overlay mode buttons */
.modePills{
  position:absolute;
  left:50%;
  bottom:14px;
  transform:translateX(-50%);
  display:flex;
  gap:7px;
  padding:6px;
  background:rgba(0,0,0,.52);
  border:1px solid rgba(255,255,255,.12);
  border-radius:999px;
  backdrop-filter:blur(12px);
}
.modePills button{min-height:38px;border-radius:999px;padding:8px 13px;font-size:13px;background:transparent;color:#ddd}
.modePills button.on{background:#fff;color:#000}

/* bottom nav */
.bottom{
  padding:8px 10px calc(8px + var(--safe-bottom));
  background:linear-gradient(180deg,rgba(0,0,0,.82),#050505);
  border-top:1px solid #151515;
}
.actions{
  display:grid;
  grid-template-columns:1fr 1fr 1fr 1fr;
  gap:8px;
  max-width:720px;
  margin:0 auto;
}
.actions button{font-size:13px;min-width:0;padding:10px 8px;border-radius:18px}
.actions .capture{grid-column:span 1;background:#fff;color:#000;font-size:15px}

/* drawers */
.drawer{
  position:fixed;
  left:0;right:0;bottom:0;
  max-height:86vh;
  background:rgba(10,10,10,.98);
  border-top:1px solid #2b2b2b;
  border-radius:24px 24px 0 0;
  display:none;
  overflow:hidden;
  box-shadow:0 -20px 50px rgba(0,0,0,.85);
  z-index:20;
}
.drawer.open{display:flex;flex-direction:column}
.handle{width:48px;height:5px;border-radius:999px;background:#444;margin:10px auto 4px}
.drawerHead{
  display:flex;
  align-items:center;
  justify-content:space-between;
  gap:12px;
  padding:6px 14px 10px;
}
.drawerTitle{font-size:17px;font-weight:900}
.drawerBody{
  overflow:auto;
  padding:0 14px calc(18px + var(--safe-bottom));
  -webkit-overflow-scrolling:touch;
}
.closeBtn{min-height:38px;padding:8px 12px;border-radius:999px;background:#1d1d1d;color:#ddd}

/* settings */
.tabs{
  position:sticky;top:0;z-index:2;
  display:grid;
  grid-template-columns:repeat(4,1fr);
  gap:6px;
  background:rgba(10,10,10,.98);
  padding:6px 0 10px;
}
.tabs button{min-width:0;min-height:42px;padding:8px 5px;border-radius:14px;font-size:12px;background:#171717;color:#bbb}
.tabs button.on{background:#fff;color:#000}
.section{display:none}
.section.on{display:block}
.grid{display:grid;gap:10px}
.card{
  background:#141414;
  border:1px solid #242424;
  border-radius:18px;
  padding:12px;
}
.row{
  display:grid;
  grid-template-columns:1fr;
  gap:8px;
  padding:10px 0;
  border-bottom:1px solid #242424;
}
.row:last-child{border-bottom:0}
.rowTop{display:flex;justify-content:space-between;align-items:center;gap:10px}
label{font-size:14px;font-weight:760;color:#eee}
.val{font-size:13px;color:#9c9c9c;text-align:right;white-space:nowrap}
select,input[type=number],input[type=text]{
  width:100%;
  background:#0d0d0d;
  color:#fff;
  border:1px solid #303030;
  border-radius:14px;
  padding:12px;
  min-height:46px;
}
input[type=range]{
  width:100%;
  accent-color:#fff;
}
.mini{font-size:12px;color:#888;line-height:1.45;margin-top:8px}

/* gallery */
.galleryTools{display:grid;grid-template-columns:1fr auto;gap:8px;margin:4px 0 10px}
.photo{
  display:grid;
  grid-template-columns:64px 1fr auto;
  gap:10px;
  align-items:center;
  padding:10px 0;
  border-bottom:1px solid #222;
}
.thumb{
  width:64px;height:48px;border-radius:12px;background:#222;object-fit:cover;border:1px solid #333;
}
.photo a{color:#fff;text-decoration:none;font-weight:750;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.meta{font-size:12px;color:#888;margin-top:3px}
@media (max-width:360px){
  .actions{gap:6px}
  .actions button{font-size:12px;padding-left:5px;padding-right:5px}
  .modePills button{padding:7px 10px}
}
</style>
</head>
<body>
<div id="app">
  <div id="previewWrap">
    <div id="viewFiltered" class="view hidden">
      <img id="previewFiltered" alt="Podgląd z filtrem">
      <div class="badge">Filtr</div>
    </div>
    <div id="viewRaw" class="view">
      <img id="previewRaw" alt="Podgląd normalny">
      <div class="badge">Normal</div>
    </div>

    <div id="status" class="status"></div>

    <div class="modePills">
      <button id="modeRaw" class="on" onclick="previewMode('raw')">Normal</button>
      <button id="modeFiltered" onclick="previewMode('filtered')">Filtr</button>
      <button id="modeBoth" onclick="previewMode('both')">Oba</button>
    </div>
  </div>

  <div class="bottom">
    <div class="actions">
      <button class="capture" onclick="capture()">Zdjęcie</button>
      <button onclick="toggleVideo()">Wideo</button>
      <button onclick="openDrawer('settings')">Ustaw.</button>
      <button onclick="openDrawer('photos');loadPhotos()">Galeria</button>
    </div>
  </div>
</div>

<div id="photos" class="drawer">
  <div class="handle"></div>
  <div class="drawerHead">
    <div class="drawerTitle">Galeria</div>
    <button class="closeBtn" onclick="closeDrawers()">Zamknij</button>
  </div>
  <div class="drawerBody">
    <div class="galleryTools">
      <button class="ghost" onclick="loadPhotos()">Odśwież</button>
      <button class="ghost" onclick="openCurrent()">Otwórz panel</button>
    </div>
    <div id="photosList"></div>
  </div>
</div>

<div id="settings" class="drawer">
  <div class="handle"></div>
  <div class="drawerHead">
    <div class="drawerTitle">Ustawienia</div>
    <button class="closeBtn" onclick="closeDrawers()">Zamknij</button>
  </div>
  <div class="drawerBody">
    <div class="tabs">
      <button id="tab-basic" class="on" onclick="tab('basic')">Tryby</button>
      <button id="tab-photo" onclick="tab('photo')">Foto</button>
      <button id="tab-look" onclick="tab('look')">Obraz</button>
      <button id="tab-advanced" onclick="tab('advanced')">Zaaw.</button>
    </div>

    <div id="basic" class="section on">
      <div class="card grid">
        <div class="row"><div class="rowTop"><label>Preset wyglądu</label></div><select id="lookPreset" onchange="set('lookPreset',this.value)"></select></div>
        <div class="row"><div class="rowTop"><label>Tryb zapisu</label></div><select id="captureKind" onchange="set('captureKind',this.value)"></select></div>
        <div class="row"><div class="rowTop"><label>Źródło zdjęcia</label></div><select id="photoSource" onchange="set('photoSource',this.value)"></select></div>
        <div class="row"><div class="rowTop"><label>Tryb sensora</label></div><select id="sensorMode" onchange="set('sensorMode',this.value)"></select></div>
        <div class="row"><div class="rowTop"><label>Format wideo</label></div><select id="videoFormat" onchange="set('videoFormat',this.value)"></select></div>
        <div class="row"><div class="rowTop"><label>Czas wideo</label><span class="val"><span id="videoSecondsV"></span>s</span></div><input id="videoSeconds" type="range" min="0" max="300" step="1" oninput="setNum('videoSeconds',this.value)"></div>
      </div>
    </div>

    <div id="photo" class="section">
      <div class="card grid">
        <div class="row"><div class="rowTop"><label>Format zdjęcia</label></div><select id="photoFormat" onchange="set('photoFormat',this.value)"></select></div>
        <div class="row"><div class="rowTop"><label>Szerokość</label><span class="val" id="photoWidthV"></span></div><input id="photoWidth" type="range" min="320" max="4056" step="16" oninput="setNum('photoWidth',this.value)"></div>
        <div class="row"><div class="rowTop"><label>Wysokość</label><span class="val" id="photoHeightV"></span></div><input id="photoHeight" type="range" min="240" max="3040" step="16" oninput="setNum('photoHeight',this.value)"></div>
        <div class="row"><div class="rowTop"><label>Jakość JPG</label><span class="val" id="jpgQualityV"></span></div><input id="jpgQuality" type="range" min="70" max="100" step="1" oninput="setNum('jpgQuality',this.value)"></div>
        <div class="row"><div class="rowTop"><label>EV zdjęcia</label><span class="val" id="photoEvV"></span></div><input id="photoEv" type="range" min="-8" max="8" step="0.1" oninput="setNum('photoEv',this.value)"></div>
        <div class="row"><div class="rowTop"><label>Random min FPS</label><span class="val" id="randomFrameMinFpsV"></span></div><input id="randomFrameMinFps" type="range" min="1" max="30" step="1" oninput="setNum('randomFrameMinFps',this.value)"></div>
        <div class="row"><div class="rowTop"><label>Random max FPS</label><span class="val" id="randomFrameMaxFpsV"></span></div><input id="randomFrameMaxFps" type="range" min="1" max="30" step="1" oninput="setNum('randomFrameMaxFps',this.value)"></div>
        <div class="row"><div class="rowTop"><label>Random sekundy</label><span class="val" id="randomFrameSecondsV"></span></div><input id="randomFrameSeconds" type="range" min="1" max="120" step="1" oninput="setNum('randomFrameSeconds',this.value)"></div>
      </div>
    </div>

    <div id="look" class="section">
      <div class="card grid">
        <div class="row"><div class="rowTop"><label>Paleta</label></div><select id="paletteMode" onchange="set('paletteMode',this.value)"></select></div>
        <div class="row"><div class="rowTop"><label>Ilość kolorów</label><span class="val" id="selectedColorAmountV"></span></div><input id="selectedColorAmount" type="range" min="2" max="256" step="1" oninput="setNum('selectedColorAmount',this.value)"><div class="mini">Najlepiej używać wartości: 2, 4, 8, 16, 32, 64, 128, 256.</div></div>
        <div class="row"><div class="rowTop"><label>Red scale</label><span class="val" id="redScaleV"></span></div><input id="redScale" type="range" min="0" max="2" step="0.01" oninput="setNum('redScale',this.value)"></div>
        <div class="row"><div class="rowTop"><label>Green scale</label><span class="val" id="greenScaleV"></span></div><input id="greenScale" type="range" min="0" max="2" step="0.01" oninput="setNum('greenScale',this.value)"></div>
        <div class="row"><div class="rowTop"><label>Blue scale</label><span class="val" id="blueScaleV"></span></div><input id="blueScale" type="range" min="0" max="2" step="0.01" oninput="setNum('blueScale',this.value)"></div>
        <div class="row"><div class="rowTop"><label>Gamma zapisu</label><span class="val" id="lowSaveGammaV"></span></div><input id="lowSaveGamma" type="range" min="0.35" max="2.5" step="0.01" oninput="setNum('lowSaveGamma',this.value)"></div>
        <div class="row"><div class="rowTop"><label>Yellow fix</label><span class="val" id="lowGrayYellowFixV"></span></div><input id="lowGrayYellowFix" type="range" min="0" max="80" step="1" oninput="setNum('lowGrayYellowFix',this.value)"></div>
      </div>
    </div>

    <div id="advanced" class="section">
      <div class="card grid">
        <div class="row"><div class="rowTop"><label>EV podglądu</label><span class="val" id="evV"></span></div><input id="ev" type="range" min="-8" max="8" step="0.1" oninput="setPreviewNum('ev',this.value)"></div>
        <div class="row"><div class="rowTop"><label>Kontrast</label><span class="val" id="contrastV"></span></div><input id="contrast" type="range" min="0" max="32" step="0.05" oninput="setPreviewNum('contrast',this.value)"></div>
        <div class="row"><div class="rowTop"><label>Saturacja</label><span class="val" id="saturationV"></span></div><input id="saturation" type="range" min="0" max="32" step="0.05" oninput="setPreviewNum('saturation',this.value)"></div>
        <div class="row"><div class="rowTop"><label>Jasność</label><span class="val" id="brightnessV"></span></div><input id="brightness" type="range" min="-1" max="1" step="0.01" oninput="setPreviewNum('brightness',this.value)"></div>
        <div class="row"><div class="rowTop"><label>Ostrość</label><span class="val" id="sharpnessV"></span></div><input id="sharpness" type="range" min="0" max="16" step="0.05" oninput="setPreviewNum('sharpness',this.value)"></div>
        <div class="row"><div class="rowTop"><label>Black level</label><span class="val" id="blackLevelV"></span></div><input id="blackLevel" type="range" min="0" max="240" step="1" oninput="setPreviewNum('blackLevel',this.value)"></div>
        <div class="row"><div class="rowTop"><label>Dark level</label><span class="val" id="darkLevelV"></span></div><input id="darkLevel" type="range" min="0.25" max="2" step="0.01" oninput="setPreviewNum('darkLevel',this.value)"></div>
        <div class="row"><div class="rowTop"><label>Pixel size</label><span class="val" id="previewPixelSizeV"></span></div><input id="previewPixelSize" type="range" min="1" max="32" step="1" oninput="setPreviewNum('previewPixelSize',this.value)"></div>
        <div class="row"><div class="rowTop"><label>Preview colors</label><span class="val" id="previewColorLevelsV"></span></div><input id="previewColorLevels" type="range" min="2" max="256" step="1" oninput="setPreviewNum('previewColorLevels',this.value)"></div>
        <div class="row"><div class="rowTop"><label>Denoise</label></div><select id="denoise" onchange="setPreview('denoise',this.value)"></select></div>
      </div>
    </div>
  </div>
</div>

<script>
let state={}, options={}, saveTimer=null, currentMode='raw';
let saveInFlight=false, saveQueued=false, lastSaveMs=0;

const $=id=>document.getElementById(id);

function toast(t){
  const s=$('status');
  s.textContent=t;
  s.classList.add('show');
  clearTimeout(toast.t);
  toast.t=setTimeout(()=>s.classList.remove('show'),900);
}

function closeDrawers(){
  $('photos').classList.remove('open');
  $('settings').classList.remove('open');
}

function openDrawer(id){
  closeDrawers();
  $(id).classList.add('open');
}

function openCurrent(){
  window.open(location.href,'_blank');
}

function tab(id){
  for(const x of ['basic','photo','look','advanced']){
    $(x).classList.toggle('on',x===id);
    $('tab-'+x).classList.toggle('on',x===id);
  }
}

function setImg(id,url){
  const img=$(id);
  const old=img.getAttribute('data-url');
  if(old===url) return;
  img.setAttribute('data-url',url);
  img.src=url;
}

function previewMode(mode){
  currentMode=mode;
  const wrap=$('previewWrap');
  const raw=$('viewRaw');
  const filtered=$('viewFiltered');

  $('modeRaw').classList.toggle('on',mode==='raw');
  $('modeFiltered').classList.toggle('on',mode==='filtered');
  $('modeBoth').classList.toggle('on',mode==='both');

  raw.classList.toggle('hidden',mode==='filtered');
  filtered.classList.toggle('hidden',mode==='raw');

  wrap.classList.toggle('dual',mode==='both');
  wrap.classList.toggle('portrait',mode==='both' && innerHeight>=innerWidth);
  wrap.classList.toggle('landscape',mode==='both' && innerWidth>innerHeight);

  if(mode==='raw'){
    setImg('previewRaw','/api/stream.mjpg?raw=true&q=42&fps=18&ts='+Date.now());
    setImg('previewFiltered','');
  }else if(mode==='filtered'){
    setImg('previewFiltered','/api/stream.mjpg?raw=false&q=45&fps=14&ts='+Date.now());
    setImg('previewRaw','');
  }else{
    setImg('previewRaw','/api/stream.mjpg?raw=true&q=40&fps=10&ts='+Date.now());
    setImg('previewFiltered','/api/stream.mjpg?raw=false&q=40&fps=10&ts='+Date.now());
  }
}
addEventListener('resize',()=>previewMode(currentMode));

function showFilteredLive(){
  if(currentMode==='raw') previewMode('filtered');
}

async function capture(){
  toast('Robię zdjęcie...');
  const r=await fetch('/api/capture',{method:'POST'});
  toast(r.ok?'Zdjęcie zlecone':'Błąd zdjęcia');
}

async function toggleVideo(){
  const r=await fetch('/api/video/toggle',{method:'POST'});
  toast(r.ok?'Wideo przełączone':'Błąd wideo');
}

function sizeText(n){
  if(!n) return '';
  if(n>1024*1024) return (n/1024/1024).toFixed(1)+' MB';
  if(n>1024) return (n/1024).toFixed(0)+' KB';
  return n+' B';
}

async function loadPhotos(){
  const box=$('photosList');
  box.innerHTML='<div class="mini">Ładuję...</div>';
  try{
    const r=await fetch('/api/photos?ts='+Date.now());
    const list=await r.json();
    if(!list.length){box.innerHTML='<div class="mini">Brak zdjęć/filmów.</div>';return}
    box.innerHTML=list.map(p=>{
      const isImg=/\.(jpg|jpeg|png|bmp)$/i.test(p.name);
      const thumb=isImg?`<img class="thumb" src="${p.url}">`:`<div class="thumb"></div>`;
      return `<div class="photo">${thumb}<div><a href="${p.url}" target="_blank">${p.name}</a><div class="meta">${sizeText(p.size)}</div></div><button class="small danger" onclick="delPhoto('${encodeURIComponent(p.name)}')">Usuń</button></div>`;
    }).join('');
  }catch(e){
    box.innerHTML='<div class="mini">Nie udało się wczytać galerii.</div>';
  }
}

async function delPhoto(name){
  if(!confirm('Usunąć plik?')) return;
  const r=await fetch('/api/photos/'+name,{method:'DELETE'});
  toast(r.ok?'Usunięto':'Błąd usuwania');
  loadPhotos();
}

function fillSelect(id,arr){
  const el=$(id);
  if(!el) return;
  el.innerHTML=(arr||[]).map(x=>`<option value="${x}">${x}</option>`).join('');
}

async function loadSettings(){
  options=await (await fetch('/api/settings/options')).json();
  state=await (await fetch('/api/settings')).json();

  fillSelect('lookPreset',options.lookPresets);
  fillSelect('captureKind',options.captureKinds);
  fillSelect('photoSource',options.photoSources);
  fillSelect('photoFormat',options.photoFormats);
  fillSelect('videoFormat',options.videoFormats);
  fillSelect('sensorMode',options.sensorModes);
  fillSelect('paletteMode',options.paletteModes);
  fillSelect('denoise',options.denoise);

  sync();
}

function valText(v){
  if(typeof v==='number'){
    if(Math.abs(v-Math.round(v))<0.001) return String(Math.round(v));
    return v.toFixed(2).replace(/0+$/,'').replace(/\.$/,'');
  }
  return v ?? '';
}

function put(id,value){
  const el=$(id);
  if(!el) return;
  if(el.tagName==='SELECT') el.value=value;
  else el.value=value;
  const v=$(id+'V');
  if(v) v.textContent=valText(value);
}

function sync(){
  for(const k of ['lookPreset','captureKind','photoSource','photoFormat','videoFormat','sensorMode','paletteMode','photoWidth','photoHeight','jpgQuality','photoEv','videoSeconds','previewFps','randomFrameMinFps','randomFrameMaxFps','randomFrameSeconds','selectedColorAmount','redScale','greenScale','blueScale','lowSaveGamma','lowGrayYellowFix']) put(k,state[k]);
  const p=state.preview||{};
  for(const k of ['ev','sharpness','contrast','saturation','brightness','blackLevel','darkLevel','previewPixelSize','previewColorLevels','denoise']) put(k,p[k]);
}

async function save(){
  if(saveInFlight){
    saveQueued=true;
    return;
  }

  saveInFlight=true;
  saveQueued=false;
  lastSaveMs=Date.now();

  try{
    const payload=JSON.stringify(state);
    const r=await fetch('/api/settings',{method:'POST',headers:{'Content-Type':'application/json'},body:payload});
    const newState=await r.json();

    // Nie robimy sync() podczas przesuwania suwaka, bo to potrafi cofać pozycję.
    // Uzupełniamy tylko stan po stronie JS.
    state=newState;
    toast('Zapisano');
  }catch(e){
    toast('Błąd zapisu');
  }finally{
    saveInFlight=false;
    if(saveQueued) scheduleSave(true);
  }
}

// To nie jest debounce czekający aż puścisz suwak.
// To throttle: wysyła zmiany cyklicznie podczas przesuwania, ale nie zalewa Raspberry Pi requestami.
function scheduleSave(force=false){
  toast('Zmieniono...');
  clearTimeout(saveTimer);

  const elapsed=Date.now()-lastSaveMs;
  if(force || elapsed>120){
    saveTimer=setTimeout(save,20);
  }else{
    saveTimer=setTimeout(save,120-elapsed);
  }
}

function visualChange(){
  showFilteredLive();
  scheduleSave();
}

function set(k,val){
  state[k]=val;
  if(k==='lookPreset' || k==='paletteMode') visualChange();
  else scheduleSave();
}

function setNum(k,val){
  state[k]=Number(val);

  if(k==='selectedColorAmount'){
    state.preview=state.preview||{};
    state.preview.previewColorLevels=state[k];
    put('previewColorLevels',state[k]);
    showFilteredLive();
  }

  if(k==='photoEv'){
    state.preview=state.preview||{};
    state.preview.ev=state[k];
    put('ev',state[k]);
    showFilteredLive();
  }

  if(['redScale','greenScale','blueScale','lowSaveGamma','lowGrayYellowFix'].includes(k)){
    showFilteredLive();
  }

  put(k,state[k]);
  scheduleSave();
}

function setPreview(k,val){
  state.preview=state.preview||{};
  state.preview[k]=val;
  if(k==='denoise') showFilteredLive();
  scheduleSave();
}

function setPreviewNum(k,val){
  state.preview=state.preview||{};
  state.preview[k]=Number(val);

  if(k==='previewColorLevels'){
    state.selectedColorAmount=state.preview[k];
    put('selectedColorAmount',state.selectedColorAmount);
  }

  if(['ev','contrast','saturation','brightness','sharpness','blackLevel','darkLevel','previewPixelSize','previewColorLevels'].includes(k)){
    showFilteredLive();
  }

  put(k,state.preview[k]);
  scheduleSave();
}

document.addEventListener('keydown',e=>{if(e.key==='Escape')closeDrawers()});
previewMode('filtered');
loadSettings();
loadPhotos();
</script>
</body>
</html>
""";

    private static void AddSensorArgs(List<string> args, bool still)
    {
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
        display.DrawTextScaled(_recording ? "REC" : "", 8, 8, _recording ? (ushort)0xF800 : (ushort)0x07E0, 2);
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
