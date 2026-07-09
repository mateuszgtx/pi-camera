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

            app.UseDefaultFiles();
            app.UseStaticFiles();
            app.Use(EnforceOptionalWebAuthenticationAsync);

            app.MapGet("/", () => Results.Redirect("/index.html"));

            app.MapGet("/api/auth/status", (HttpContext context) => Results.Ok(CurrentWebAuthStatus(context)));
            app.MapPost("/api/auth/login", async (HttpContext context) => await LoginWebAsync(context));
            app.MapPost("/api/auth/logout", (HttpContext context) => LogoutWeb(context));
            app.MapPost("/api/auth/password", async (HttpContext context) => await SetWebPasswordAsync(context));
            app.MapPost("/api/auth/password/clear", (HttpContext context) => ClearWebPasswordFromApi(context));

            app.MapGet("/api/status", () => Results.Ok(new
            {
                ok = true,
                running = _running,
                busy = _isBusy,
                recording = _previewRecording,
                randomRecording = _previewRandomRecording,
                streaming = _streaming,
                streamTarget = MaskStreamUrl(_streamUrl),
                streamUptimeSeconds = _streaming ? (int)(DateTime.UtcNow - _streamStartedUtc).TotalSeconds : 0,
                audio = new
                {
                    enabled = IsAudioEnabled(),
                    inputMode = _audioInputMode.ToString(),
                    active = ResolveAudioCaptureSource()?.Label
                },
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

                if (_captureKind == CaptureKind.Video || _captureKind == CaptureKind.RandomFrame || _captureKind == CaptureKind.GlitchVideo)
                    return Results.BadRequest(new { ok = false, message = "Current mode is video. Use video toggle." });

                if (_captureKind == CaptureKind.Stream)
                    return Results.BadRequest(new { ok = false, message = "Current mode is stream. Use stream toggle/start/stop." });

                _captureRequested = true;
                return Results.Ok(new
                {
                    ok = true,
                    captureKind = _captureKind.ToString(),
                    glitchPhotoCount = _captureKind == CaptureKind.GlitchPhoto ? _glitchPhotoCount : 1,
                    message = _captureKind == CaptureKind.GlitchPhoto
                        ? (_glitchPhotoCount > 1 ? $"Glitch burst x{_glitchPhotoCount} requested" : "Glitch capture requested")
                        : "Capture requested"
                });
            });

            app.MapPost("/api/action", () => QueueCurrentModeRequest());

            app.MapPost("/api/video/toggle", () =>
            {
                if (_isBusy)
                    return Results.Conflict(new { ok = false, message = "Camera busy" });

                _captureKind = CaptureKind.Video;
                _captureRequested = true;
                return Results.Ok(new { ok = true, recording = !_previewRecording });
            });

            app.MapPost("/api/stream/toggle", () => QueueStreamRequest(0));
            app.MapPost("/api/stream/start", () => QueueStreamRequest(1));
            app.MapPost("/api/stream/stop", () => QueueStreamRequest(2));

            app.MapGet("/api/network", () => Results.Ok(CurrentNetworkStatus()));

            app.MapPost("/api/network/wifi", async (HttpRequest request) =>
            {
                try
                {
                    using var doc = await JsonDocument.ParseAsync(request.Body);
                    var root = doc.RootElement;

                    var ssid = root.TryGetProperty("ssid", out var ssidEl) ? ssidEl.GetString() ?? "" : "";
                    var password = root.TryGetProperty("password", out var passEl) ? passEl.GetString() ?? "" : "";
                    var connectNow = !root.TryGetProperty("connectNow", out var connectEl) || connectEl.ValueKind != JsonValueKind.False;

                    await AddOrConnectWifiAsync(ssid, password, connectNow);
                    SetNetworkStatus("WiFi OK: " + ssid);

                    return Results.Ok(new { ok = true, message = "WiFi saved", network = CurrentNetworkStatus() });
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[API WIFI] " + ex);
                    return Results.BadRequest(new { ok = false, message = ShortError(ex.Message), network = CurrentNetworkStatus() });
                }
            });

            app.MapPost("/api/network/wifi/connect", async (HttpRequest request) =>
            {
                try
                {
                    using var doc = await JsonDocument.ParseAsync(request.Body);
                    var name = doc.RootElement.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(name))
                        return Results.BadRequest(new { ok = false, message = "Empty network name" });

                    await ConnectSavedWifiAsync(name);
                    SetNetworkStatus("Connected: " + name);
                    return Results.Ok(new { ok = true, network = CurrentNetworkStatus() });
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[API WIFI CONNECT] " + ex);
                    return Results.BadRequest(new { ok = false, message = ShortError(ex.Message), network = CurrentNetworkStatus() });
                }
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
                        kind = IsVideoFile(path) ? "video" : IsRawPhotoFile(path) ? "raw" : "image",
                        url = "/api/photos/" + Uri.EscapeDataString(Path.GetFileName(path)),
                        previewUrl = IsPhotoFile(path) ? "/api/photos/" + Uri.EscapeDataString(Path.GetFileName(path)) + "/preview.jpg" : null
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

            app.MapGet("/api/photos/{file}/preview.jpg", async (string file) =>
            {
                var safeName = Path.GetFileName(Uri.UnescapeDataString(file));
                var path = Path.Combine(outputDir, safeName);

                if (!File.Exists(path) || !IsPhotoFile(path))
                    return Results.NotFound();

                var previewPath = GalleryPreviewPathFor(path);

                if (IsRawPhotoFile(path))
                {
                    if (!File.Exists(previewPath))
                    {
                        if (TryFindRawCompanionImage(path, out var companionPath))
                            await ImageLoader.SaveJpegPreviewAsync(companionPath, previewPath, 1600, Math.Clamp(_jpgQuality, 70, 95));
                        else
                            return Results.NotFound(new { ok = false, message = "No preview is available for this RAW/DNG file." });
                    }
                }
                else if (!File.Exists(previewPath) || File.GetLastWriteTimeUtc(previewPath) < File.GetLastWriteTimeUtc(path))
                {
                    await ImageLoader.SaveJpegPreviewAsync(path, previewPath, 1600, Math.Clamp(_jpgQuality, 70, 95));
                }

                return Results.File(previewPath, "image/jpeg", enableRangeProcessing: true);
            });

            app.MapDelete("/api/photos/{file}", (string file) =>
            {
                var safeName = Path.GetFileName(Uri.UnescapeDataString(file));
                var path = Path.Combine(outputDir, safeName);

                if (!File.Exists(path) || !IsMediaFile(path))
                    return Results.NotFound(new { ok = false, message = "File not found" });

                File.Delete(path);
                TryDelete(GalleryPreviewPathFor(path));
                return Results.Ok(new { ok = true });
            });

            app.MapGet("/api/audio", () => Results.Ok(CurrentAudioStatus()));
            app.MapGet("/api/audio/listen.wav", async (HttpContext context) => await StreamAudioListenAsync(context));
            app.MapGet("/api/audio/listen.raw", async (HttpContext context) => await StreamAudioListenRawAsync(context));

            app.MapPost("/api/audio/bluetooth/power", async (HttpRequest request) =>
            {
                try
                {
                    var on = true;
                    if (request.ContentLength is > 0)
                    {
                        using var doc = await JsonDocument.ParseAsync(request.Body);
                        if (TryGetBool(doc.RootElement, "enabled", out var enabled)) on = enabled;
                        else if (TryGetBool(doc.RootElement, "on", out var onValue)) on = onValue;
                        else if (TryGetBool(doc.RootElement, "powered", out var powered)) on = powered;
                        else if (TryGetString(doc.RootElement, "state", out var state)) on = !state.Equals("off", StringComparison.OrdinalIgnoreCase);
                    }

                    await SetBluetoothRadioAsync(on);
                    SetNetworkStatus("Bluetooth " + (on ? "ON" : "OFF"));
                    return Results.Ok(CurrentAudioStatus());
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[API BT POWER] " + ex);
                    return Results.BadRequest(new { ok = false, message = ShortError(ex.Message), audio = CurrentAudioStatus() });
                }
            });

            app.MapPost("/api/audio/bluetooth/on", async () =>
            {
                try
                {
                    await SetBluetoothRadioAsync(true);
                    SetNetworkStatus("Bluetooth ON");
                    return Results.Ok(CurrentAudioStatus());
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[API BT ON] " + ex);
                    return Results.BadRequest(new { ok = false, message = ShortError(ex.Message), audio = CurrentAudioStatus() });
                }
            });

            app.MapPost("/api/audio/bluetooth/off", async () =>
            {
                try
                {
                    await SetBluetoothRadioAsync(false);
                    SetNetworkStatus("Bluetooth OFF");
                    return Results.Ok(CurrentAudioStatus());
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[API BT OFF] " + ex);
                    return Results.BadRequest(new { ok = false, message = ShortError(ex.Message), audio = CurrentAudioStatus() });
                }
            });

            app.MapPost("/api/audio/bluetooth/scan", async (HttpRequest request) =>
            {
                try
                {
                    var seconds = 120;
                    if (request.ContentLength is > 0)
                    {
                        using var doc = await JsonDocument.ParseAsync(request.Body);
                        if (TryGetInt(doc.RootElement, "seconds", out var requestedSeconds))
                            seconds = requestedSeconds;
                    }

                    await ScanBluetoothAsync(seconds);
                    return Results.Ok(CurrentAudioStatus());
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[API BT SCAN] " + ex);
                    return Results.BadRequest(new { ok = false, message = ShortError(ex.Message), audio = CurrentAudioStatus() });
                }
            });

            app.MapPost("/api/audio/bluetooth/cancel", async () =>
            {
                try
                {
                    await CancelBluetoothScanAsync();
                    SetNetworkStatus("Bluetooth scan cancelled");
                    return Results.Ok(CurrentAudioStatus());
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[API BT CANCEL] " + ex);
                    return Results.BadRequest(new { ok = false, message = ShortError(ex.Message), audio = CurrentAudioStatus() });
                }
            });

            app.MapPost("/api/audio/bluetooth/scan/cancel", async () =>
            {
                try
                {
                    await CancelBluetoothScanAsync();
                    SetNetworkStatus("Bluetooth scan cancelled");
                    return Results.Ok(CurrentAudioStatus());
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[API BT SCAN CANCEL] " + ex);
                    return Results.BadRequest(new { ok = false, message = ShortError(ex.Message), audio = CurrentAudioStatus() });
                }
            });

            app.MapPost("/api/audio/bluetooth/pair", async (HttpRequest request) =>
            {
                try
                {
                    using var doc = await JsonDocument.ParseAsync(request.Body);
                    var mac = ReadBluetoothRequestMac(doc.RootElement);
                    await PairBluetoothAsync(mac);
                    return Results.Ok(CurrentAudioStatus());
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[API BT PAIR] " + ex);
                    return Results.BadRequest(new { ok = false, message = ShortError(ex.Message), audio = CurrentAudioStatus() });
                }
            });

            app.MapPost("/api/audio/bluetooth/connect", async (HttpRequest request) =>
            {
                try
                {
                    using var doc = await JsonDocument.ParseAsync(request.Body);
                    var mac = ReadBluetoothRequestMac(doc.RootElement);
                    await ConnectBluetoothAsync(mac);
                    return Results.Ok(CurrentAudioStatus());
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[API BT CONNECT] " + ex);
                    return Results.BadRequest(new { ok = false, message = ShortError(ex.Message), audio = CurrentAudioStatus() });
                }
            });

            app.MapPost("/api/audio/bluetooth/disconnect", async (HttpRequest request) =>
            {
                try
                {
                    using var doc = await JsonDocument.ParseAsync(request.Body);
                    var mac = ReadBluetoothRequestMac(doc.RootElement);
                    await DisconnectBluetoothAsync(mac);
                    return Results.Ok(CurrentAudioStatus());
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[API BT DISCONNECT] " + ex);
                    return Results.BadRequest(new { ok = false, message = ShortError(ex.Message), audio = CurrentAudioStatus() });
                }
            });

            app.MapPost("/api/audio/bluetooth/remove", async (HttpRequest request) =>
            {
                try
                {
                    using var doc = await JsonDocument.ParseAsync(request.Body);
                    var mac = ReadBluetoothRequestMac(doc.RootElement);
                    await RemoveBluetoothAsync(mac);
                    return Results.Ok(CurrentAudioStatus());
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[API BT REMOVE] " + ex);
                    return Results.BadRequest(new { ok = false, message = ShortError(ex.Message), audio = CurrentAudioStatus() });
                }
            });

            app.MapGet("/api/settings", () => Results.Ok(CurrentApiSettings()));

            app.MapPost("/api/settings", async (HttpRequest request) =>
            {
                using var doc = await JsonDocument.ParseAsync(request.Body);
                ApplyApiSettings(doc.RootElement, preview);
                SavePersistentSettingsToDisk();
                return Results.Ok(CurrentApiSettings());
            });

            app.MapPost("/api/settings/reset", () =>
            {
                ResetSettingsToDefaults(preview);
                SavePersistentSettingsToDisk();
                return Results.Ok(CurrentApiSettings());
            });

            app.MapGet("/api/settings/options", () => Results.Ok(new
            {
                captureKinds = new[] { "Photo", "Video", "RandomFrame", "GlitchPhoto", "GlitchVideo", "Stream" },
                lookPresets = new[] { "NORMAL", "LOW32", "LOW16", "RETRO8", "VHS", "MONO4" },
                photoSources = new[] { "FullHq", "Preview" },
                photoFormats = new[] { "jpg", "png", "bmp", "raw", "rawjpg" },
                videoFormats = new[] { "mjpeg", "mp4" },
                streamOutputFormats = new[] { "auto", "flv", "mpegts", "rtsp" },
                audioInputModes = Enum.GetNames<AudioInputMode>(),
                audioInputFormats = new[] { "auto", "alsa", "pulse" },
                sensorModes = new[] { "full", "bin", "fast" },
                paletteModes = Enum.GetNames<PaletteMode>(),
                denoise = new[] { "cdn_off", "cdn_fast", "cdn_hq" },
                colorChoices = _colorChoices,
                pixelChoices = _pixelChoices,
                maxPreviewPixelSize = 2048,
                glitchPhotoCountChoices = new[] { 1, 2, 3, 4, 5, 6, 8, 10, 12 },
                vhsGlitchFrequencyChoices = Enumerable.Range(0, 11).ToArray(),
                vhsQualityChoices = Enumerable.Range(0, 11).ToArray(),
                vhsScanlinesChoices = Enumerable.Range(0, 11).ToArray(),
                vhsNoiseChoices = Enumerable.Range(0, 11).ToArray(),
                vhsWobbleChoices = Enumerable.Range(0, 11).ToArray(),
                livePreviewPixelMax = _livePreviewPixelMax,
                previewPixelMaxForCurrentPhotoSource = MaxPixelSizeForCurrentSource(),
                pixelMeaning = "1 = strong pixel-art / large blocks, max = best quality / smallest blocks"
            }));

            Console.WriteLine($"[API] listening on {apiUrl}");
            await app.RunAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine("[API] " + ex);
        }
    }

    private static IResult QueueCurrentModeRequest()
    {
        if (_captureKind == CaptureKind.Stream)
            return QueueStreamRequest(0);

        if (_isBusy)
            return Results.Conflict(new { ok = false, message = "Camera busy" });

        _captureRequested = true;

        return Results.Ok(new
        {
            ok = true,
            queued = true,
            captureKind = _captureKind.ToString(),
            recording = _previewRecording ? false : (_captureKind is CaptureKind.Video or CaptureKind.RandomFrame or CaptureKind.GlitchVideo),
            randomRecording = _captureKind == CaptureKind.RandomFrame && !_previewRecording,
            glitchVideoRecording = _captureKind == CaptureKind.GlitchVideo && !_previewRecording,
            glitchPhotoCount = _captureKind == CaptureKind.GlitchPhoto ? _glitchPhotoCount : 1,
            message = CurrentModeRequestMessage()
        });
    }

    private static string CurrentModeRequestMessage()
    {
        return _captureKind switch
        {
            CaptureKind.Photo => "Photo queued",
            CaptureKind.GlitchPhoto => _glitchPhotoCount > 1 ? $"Glitch x{_glitchPhotoCount} queued" : "Glitch photo queued",
            CaptureKind.Video => _previewRecording ? "Video stop queued" : "Video start queued",
            CaptureKind.RandomFrame => _previewRecording ? "Random video stop queued" : "Random video start queued",
            CaptureKind.GlitchVideo => _previewRecording ? "Glitch video stop queued" : "Glitch video start queued",
            CaptureKind.Stream => _streaming ? "Stream stop queued" : "Stream start queued",
            _ => "Action queued"
        };
    }

    private static IResult QueueStreamRequest(int action)
    {
        if (_isBusy)
            return Results.Conflict(new { ok = false, message = "Camera busy" });

        if (action == 1 && _streaming)
            return Results.Ok(new { ok = true, streaming = true, queued = false, message = "Stream already running" });

        if (action == 2 && !_streaming)
            return Results.Ok(new { ok = true, streaming = false, queued = false, message = "Stream already stopped" });

        var wantsStart = action == 1 || (action == 0 && !_streaming);
        if (wantsStart && string.IsNullOrWhiteSpace(_streamUrl))
            return Results.BadRequest(new { ok = false, message = "streamUrl is missing. Set it in /api/settings." });

        Interlocked.Exchange(ref _streamRequestedAction, action);
        _captureKind = CaptureKind.Stream;
        _captureRequested = true;

        return Results.Ok(new
        {
            ok = true,
            queued = true,
            requested = action == 1 ? "start" : action == 2 ? "stop" : "toggle",
            streaming = action == 0 ? !_streaming : action == 1,
            streamTarget = MaskStreamUrl(_streamUrl)
        });
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
                recording = _previewRecording,
                randomRecording = _previewRandomRecording,
                streaming = _streaming,
                streamUrl = _streamUrl,
                streamOutputFormat = _streamOutputFormat,
                streamFps = _streamFps,
                streamBitrateKbps = _streamBitrateKbps,
                streamJpegQuality = _streamJpegQuality,
                streamUseRaw = _streamUseRaw,
                streamTarget = MaskStreamUrl(_streamUrl),
                streamUptimeSeconds = _streaming ? (int)(DateTime.UtcNow - _streamStartedUtc).TotalSeconds : 0,
                audioEnabled = _audioEnabled,
                audioInputMode = _audioInputMode.ToString(),
                audioInputFormat = _audioInputFormat,
                audioDevice = _audioDevice,
                audioSampleRate = _audioSampleRate,
                audioBitrateKbps = _audioBitrateKbps,
                audioActive = ResolveAudioCaptureSource()?.Label,
                webAuthEnabled = IsWebPasswordEnabled(),
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
                glitchPhotoCount = _glitchPhotoCount,
                vhsGlitchFrequency = _vhsGlitchFrequency,
                vhsQuality = _vhsQuality,
                vhsScanlines = _vhsScanlines,
                vhsNoise = _vhsNoise,
                vhsWobble = _vhsWobble,
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

    private static void ApplyApiSettings(JsonElement json, CameraPreviewService? preview)
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

            if (TryGetString(json, "streamUrl", out var streamUrl))
                _streamUrl = streamUrl.Trim();

            if (TryGetString(json, "streamOutputFormat", out var streamOutputFormat) || TryGetString(json, "streamFormat", out streamOutputFormat))
                _streamOutputFormat = NormalizeStreamOutputFormat(streamOutputFormat);

            if (TryGetInt(json, "streamFps", out var streamFps))
                _streamFps = Math.Clamp(streamFps, 1, 30);

            if (TryGetInt(json, "streamBitrateKbps", out var streamBitrateKbps) || TryGetInt(json, "streamBitrate", out streamBitrateKbps))
                _streamBitrateKbps = Math.Clamp(streamBitrateKbps, 256, 20000);

            if (TryGetInt(json, "streamJpegQuality", out var streamJpegQuality))
                _streamJpegQuality = Math.Clamp(streamJpegQuality, 35, 95);

            if (TryGetBool(json, "streamUseRaw", out var streamUseRaw) || TryGetBool(json, "streamRaw", out streamUseRaw))
                _streamUseRaw = streamUseRaw;

            if (TryGetBool(json, "audioEnabled", out var audioEnabled) || TryGetBool(json, "audio", out audioEnabled))
                _audioEnabled = audioEnabled;

            if (TryGetString(json, "audioInputMode", out var audioInputMode) || TryGetString(json, "audioMode", out audioInputMode))
                _audioInputMode = ParseAudioInputMode(audioInputMode);

            if (TryGetString(json, "audioInputFormat", out var audioInputFormat) || TryGetString(json, "audioFormat", out audioInputFormat))
                _audioInputFormat = NormalizeAudioInputFormat(audioInputFormat);

            if (TryGetString(json, "audioDevice", out var audioDevice))
                _audioDevice = audioDevice.Trim();

            if (TryGetInt(json, "audioSampleRate", out var audioSampleRate))
                _audioSampleRate = Math.Clamp(audioSampleRate, 8000, 96000);

            if (TryGetInt(json, "audioBitrateKbps", out var audioBitrateKbps) || TryGetInt(json, "audioBitrate", out audioBitrateKbps))
                _audioBitrateKbps = Math.Clamp(audioBitrateKbps, 32, 512);

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

            if (TryGetInt(json, "glitchPhotoCount", out var glitchPhotoCount))
                _glitchPhotoCount = Math.Clamp(glitchPhotoCount, 1, 12);

            if (TryGetInt(json, "vhsGlitchFrequency", out var vhsGlitchFrequency))
                _vhsGlitchFrequency = Math.Clamp(vhsGlitchFrequency, 0, 10);

            if (TryGetInt(json, "vhsQuality", out var vhsQuality))
                _vhsQuality = Math.Clamp(vhsQuality, 0, 10);

            if (TryGetInt(json, "vhsScanlines", out var vhsScanlines))
                _vhsScanlines = Math.Clamp(vhsScanlines, 0, 10);

            if (TryGetInt(json, "vhsNoise", out var vhsNoise))
                _vhsNoise = Math.Clamp(vhsNoise, 0, 10);

            if (TryGetInt(json, "vhsWobble", out var vhsWobble))
                _vhsWobble = Math.Clamp(vhsWobble, 0, 10);

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

            preview?.UpdateSettings(_previewSettings);
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

}
