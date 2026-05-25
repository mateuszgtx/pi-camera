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
                captureKinds = new[] { "Photo", "Video", "RandomFrame", "GlitchPhoto", "GlitchVideo" },
                photoSources = new[] { "FullHq", "Preview" },
                photoFormats = new[] { "jpg", "png", "bmp", "raw", "rawjpg" },
                videoFormats = new[] { "mjpeg", "mp4" },
                sensorModes = new[] { "full", "bin", "fast" },
                paletteModes = Enum.GetNames<PaletteMode>(),
                denoise = new[] { "cdn_off", "cdn_fast", "cdn_hq" },
                colorChoices = _colorChoices,
                pixelChoices = _pixelChoices,
                maxPreviewPixelSize = 2048,
                livePreviewPixelMax = _livePreviewPixelMax,
                previewPixelMaxForCurrentPhotoSource = MaxPixelSizeForCurrentSource(),
                pixelMeaning = "1 = mocny pixel-art / duże bloki, max = najlepsza jakość / najmniejsze bloki"
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
                randomSegmentSeconds = _randomFrameSeconds,
                glitchStrength = _glitchStrength,
                glitchChangeMs = _glitchChangeMs,
                glitchPaletteEnabled = _glitchPaletteEnabled,
                glitchPixelsEnabled = _glitchPixelsEnabled,
                glitchRgbEnabled = _glitchRgbEnabled,
                glitchVideoRecording = _glitchVideoRecording,
                sensorMode = _sensorMode,
                selectedColorAmount = _selectedColorAmount,
                pixelSize = _previewSettings.PreviewPixelSize,
                previewPixelSize = _previewSettings.PreviewPixelSize,
                maxPixelSize = MaxPixelSizeForCurrentSource(),
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
            {
                _photoSource = ps;
                if (_photoSource == PhotoSource.Preview && _previewSettings.PreviewPixelSize > 256)
                    _previewSettings.PreviewPixelSize = 256;
            }

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

            if (TryGetInt(json, "randomFrameSeconds", out var randomFrameSeconds) || TryGetInt(json, "randomSegmentSeconds", out randomFrameSeconds))
                _randomFrameSeconds = Math.Clamp(randomFrameSeconds, 1, 15);

            if (TryGetInt(json, "glitchStrength", out var glitchStrength))
                _glitchStrength = Math.Clamp(glitchStrength, 1, 10);

            if (TryGetInt(json, "glitchChangeMs", out var glitchChangeMs))
                _glitchChangeMs = Math.Clamp(glitchChangeMs, 100, 5000);

            if (TryGetBool(json, "glitchPaletteEnabled", out var glitchPaletteEnabled))
                _glitchPaletteEnabled = glitchPaletteEnabled;

            if (TryGetBool(json, "glitchPixelsEnabled", out var glitchPixelsEnabled))
                _glitchPixelsEnabled = glitchPixelsEnabled;

            if (TryGetBool(json, "glitchRgbEnabled", out var glitchRgbEnabled))
                _glitchRgbEnabled = glitchRgbEnabled;

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

            if (TryGetInt(json, "pixelSize", out var rootPixelSize) || TryGetInt(json, "previewPixelSize", out rootPixelSize))
                _previewSettings.PreviewPixelSize = Math.Clamp(rootPixelSize, 1, MaxPixelSizeForCurrentSource());

            if (TryGetDouble(previewJson, "ev", out var ev)) _previewSettings.Ev = Math.Clamp(ev, -8.0, 8.0);
            if (TryGetDouble(previewJson, "sharpness", out var sharpness)) _previewSettings.Sharpness = Math.Clamp(sharpness, 0.0, 16.0);
            if (TryGetDouble(previewJson, "contrast", out var contrast)) _previewSettings.Contrast = Math.Clamp(contrast, 0.0, 32.0);
            if (TryGetDouble(previewJson, "saturation", out var saturation)) _previewSettings.Saturation = Math.Clamp(saturation, 0.0, 32.0);
            if (TryGetDouble(previewJson, "brightness", out var brightness)) _previewSettings.Brightness = Math.Clamp(brightness, -1.0, 1.0);
            if (TryGetInt(previewJson, "blackLevel", out var blackLevel)) _previewSettings.BlackLevel = Math.Clamp(blackLevel, 0, 240);
            if (TryGetDouble(previewJson, "darkLevel", out var darkLevel)) _previewSettings.DarkLevel = Math.Clamp(darkLevel, 0.25, 2.0);
            if (TryGetInt(previewJson, "previewPixelSize", out var pixelSize)) _previewSettings.PreviewPixelSize = Math.Clamp(pixelSize, 1, MaxPixelSizeForCurrentSource());
            if (!rootColorAmountProvided && TryGetInt(previewJson, "previewColorLevels", out var colorLevels)) SetPreviewColors(colorLevels);
            if (TryGetString(previewJson, "denoise", out var denoise) && !string.IsNullOrWhiteSpace(denoise)) _previewSettings.Denoise = denoise;

            preview.UpdateSettings(_previewSettings);
        }
    }

    private static bool TryGetBool(JsonElement json, string name, out bool value)
    {
        value = false;
        if (!json.TryGetProperty(name, out var prop)) return false;
        if (prop.ValueKind == JsonValueKind.True) { value = true; return true; }
        if (prop.ValueKind == JsonValueKind.False) { value = false; return true; }
        if (prop.ValueKind == JsonValueKind.String && bool.TryParse(prop.GetString(), out var parsed)) { value = parsed; return true; }
        return false;
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

}
