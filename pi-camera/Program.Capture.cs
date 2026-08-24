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
    private static async Task<string> TakePhotoAsync(string outputDir, string? suffix = null)
    {
        Directory.CreateDirectory(outputDir);

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmssfff");
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

        var suffixPart = string.IsNullOrWhiteSpace(suffix) ? string.Empty : suffix;
        var finalPath = Path.Combine(outputDir, $"IMG_{stamp}{suffixPart}.{finalExt}");

        if (_photoSource == PhotoSource.Preview && !isRaw)
        {
            if (TrySaveCurrentPreviewFrame(finalPath, _photoFormat))
                return finalPath;
        }

        if (!isRaw)
        {
            return await TakeFullHqPhotoAsync(outputDir, finalPath);
        }

        await CaptureRawPhotoAsync(outputDir, finalPath);
        return finalPath;
    }











    private sealed record HqProcessingSnapshot(
        int PixelBlockSize,
        int ColorLevels,
        int BlackLevel,
        double DarkLevel,
        double Saturation,
        double RedScale,
        double GreenScale,
        double BlueScale,
        PaletteMode PaletteMode,
        string LookPreset,
        double LowSaveGamma,
        int LowGrayYellowFix,
        int VhsGlitchFrequency,
        int VhsQuality,
        int VhsScanlines,
        int VhsNoise,
        int VhsWobble,
        int JpgQuality);

    private static readonly SemaphoreSlim _hqProcessingGate = new(1, 1);
    private static readonly object _hqProcessingTasksLock = new();
    private static readonly HashSet<Task> _hqProcessingTasks = new();
    private static int _hqProcessingPending;

    private static HqProcessingSnapshot CaptureHqProcessingSnapshot()
    {
        return new HqProcessingSnapshot(
            PixelBlockSize: FullHqPixelBlockSize(),
            ColorLevels: Math.Clamp(_previewSettings.PreviewColorLevels, 2, 256),
            BlackLevel: Math.Clamp(_previewSettings.BlackLevel, 0, 240),
            DarkLevel: Math.Clamp(_previewSettings.DarkLevel, 0.25, 2.0),
            Saturation: _previewSettings.Saturation,
            RedScale: _redScale,
            GreenScale: _greenScale,
            BlueScale: _blueScale,
            PaletteMode: _paletteMode,
            LookPreset: _lookPreset,
            LowSaveGamma: _lowSaveGamma,
            LowGrayYellowFix: _lowGrayYellowFix,
            VhsGlitchFrequency: _vhsGlitchFrequency,
            VhsQuality: _vhsQuality,
            VhsScanlines: _vhsScanlines,
            VhsNoise: _vhsNoise,
            VhsWobble: _vhsWobble,
            JpgQuality: Math.Clamp(_jpgQuality, 70, 100));
    }

    private static async Task<string> TakeFullHqPhotoAsync(string outputDir, string finalPath)
    {
        // Keep source captures outside the gallery while background processing is running.
        var processingDir = Path.Combine(outputDir, ".hq-processing");
        Directory.CreateDirectory(processingDir);
        var tempPath = Path.Combine(processingDir, $"TMP_HQ_{DateTime.Now:yyyyMMdd_HHmmssfff}_{Guid.NewGuid():N}.jpg");
        var format = _photoFormat;
        var snapshot = CaptureHqProcessingSnapshot();

        var captureTimer = Stopwatch.StartNew();
        await CaptureStillJpegAsync(tempPath, _photoWidth, _photoHeight);
        captureTimer.Stop();
        Console.WriteLine($"[HQ CAPTURE] sensor capture finished in {captureTimer.Elapsed.TotalSeconds:0.00}s: {Path.GetFileName(finalPath)}");

        // A glitch burst changes the look between frames. Keep it synchronous so every
        // frame is processed with exactly the settings used for that capture. Regular
        // HQ photos are queued in the background, which lets the preview restart as soon
        // as rpicam-still has released the camera.
        if (_captureKind == CaptureKind.GlitchPhoto)
        {
            try
            {
                await ProcessCapturedHqPhotoAsync(tempPath, finalPath, format, snapshot);
            }
            finally
            {
                TryDelete(tempPath);
            }

            return finalPath;
        }

        QueueHqPhotoProcessing(tempPath, finalPath, format, snapshot);
        return finalPath;
    }

    private static void QueueHqPhotoProcessing(string tempPath, string finalPath, string format, HqProcessingSnapshot snapshot)
    {
        Interlocked.Increment(ref _hqProcessingPending);

        var task = Task.Run(async () =>
        {
            await _hqProcessingGate.WaitAsync();
            try
            {
                var processTimer = Stopwatch.StartNew();
                Console.WriteLine($"[HQ PROCESS] start {Path.GetFileName(finalPath)} (pending={Volatile.Read(ref _hqProcessingPending)})");
                await ProcessCapturedHqPhotoAsync(tempPath, finalPath, format, snapshot);
                processTimer.Stop();
                Console.WriteLine($"[HQ PROCESS] done {Path.GetFileName(finalPath)} in {processTimer.Elapsed.TotalSeconds:0.00}s");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HQ PROCESS] failed {Path.GetFileName(finalPath)}: {ex}");

                try
                {
                    TryDelete(finalPath);
                    var recoveryPath = Path.Combine(
                        Path.GetDirectoryName(finalPath) ?? ".",
                        Path.GetFileNameWithoutExtension(finalPath) + "_UNPROCESSED.jpg");
                    File.Copy(tempPath, recoveryPath, overwrite: true);
                    Console.WriteLine($"[HQ PROCESS] source preserved as {Path.GetFileName(recoveryPath)}");
                }
                catch (Exception recoveryEx)
                {
                    Console.WriteLine($"[HQ PROCESS] recovery copy failed: {recoveryEx.Message}");
                }
            }
            finally
            {
                TryDelete(tempPath);
                Interlocked.Decrement(ref _hqProcessingPending);
                _hqProcessingGate.Release();
            }
        });

        lock (_hqProcessingTasksLock)
            _hqProcessingTasks.Add(task);

        _ = task.ContinueWith(_ =>
        {
            lock (_hqProcessingTasksLock)
                _hqProcessingTasks.Remove(task);
        }, TaskScheduler.Default);
    }

    private static async Task ProcessCapturedHqPhotoAsync(
        string tempPath,
        string finalPath,
        string format,
        HqProcessingSnapshot snapshot)
    {
        using var image = await Image.LoadAsync<Rgb24>(tempPath);
        ApplyFullPhotoLook(image, snapshot);
        await SaveImageByFormatAsync(image, finalPath, format, snapshot.JpgQuality);
    }

    private static async Task WaitForPendingHqProcessingAsync()
    {
        Task[] tasks;
        lock (_hqProcessingTasksLock)
            tasks = _hqProcessingTasks.ToArray();

        if (tasks.Length == 0)
            return;

        Console.WriteLine($"[HQ PROCESS] waiting for {tasks.Length} pending job(s) before exit");
        await Task.WhenAll(tasks);
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

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Cannot start rpicam-still");

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

                using var retry = Process.Start(psi) ?? throw new InvalidOperationException("Cannot start rpicam-still retry");
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

    private static async Task CaptureRawPhotoAsync(string outputDir, string finalPath)
    {
        var isRawJpg = _photoFormat.Equals("rawjpg", StringComparison.OrdinalIgnoreCase);
        var stem = Path.GetFileNameWithoutExtension(finalPath);

        // Keep the RAW sidecar name aligned with the visible photo name. This is
        // important for burst/glitch photos, where finalPath can contain suffixes
        // such as _G01, _G02, etc.
        var rawPath = isRawJpg ? Path.Combine(outputDir, stem + ".dng") : finalPath;
        var previewPath = GalleryPreviewPathFor(rawPath);

        // rpicam-still normally writes a JPEG and a DNG sidecar when --raw is used.
        // In pure RAW mode we capture to a temporary JPEG, move the generated DNG to
        // finalPath, then turn the temporary JPEG into the gallery preview.
        var jpgOutputPath = isRawJpg
            ? finalPath
            : Path.Combine(outputDir, $"TMP_RAW_PREVIEW_{stem}.jpg");
        var generatedRawPath = Path.ChangeExtension(jpgOutputPath, ".dng");

        if (!isRawJpg)
        {
            TryDelete(jpgOutputPath);
            TryDelete(generatedRawPath);
            TryDelete(rawPath);
            TryDelete(previewPath);
        }
        else
        {
            TryDelete(rawPath);
            TryDelete(previewPath);
        }

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
            "-o", jpgOutputPath
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

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Cannot start rpicam-still");

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

        if (File.Exists(generatedRawPath) && !string.Equals(generatedRawPath, rawPath, StringComparison.OrdinalIgnoreCase))
        {
            File.Move(generatedRawPath, rawPath, overwrite: true);
        }

        if (!File.Exists(rawPath))
        {
            var msg = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new Exception($"rpicam-still RAW did not create the expected DNG file: {Path.GetFileName(rawPath)}. {msg.Trim()}");
        }

        try
        {
            if (!File.Exists(jpgOutputPath))
            {
                // Some rpicam-still versions/configurations can produce the DNG sidecar
                // without leaving a usable JPEG. Capture a small fallback JPEG so the
                // gallery still has something to display for RAW/DNG files.
                await CaptureStillJpegAsync(jpgOutputPath, _photoWidth, _photoHeight);
            }

            await ImageLoader.SaveJpegPreviewAsync(jpgOutputPath, previewPath, 1600, Math.Clamp(_jpgQuality, 70, 95));

            if (!File.Exists(previewPath))
                throw new Exception("RAW preview file was not created");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[RAW PREVIEW] " + ex.Message);
            TrySaveCurrentFrameGalleryPreview(rawPath);
        }
        finally
        {
            if (!isRawJpg)
                TryDelete(jpgOutputPath);
        }
    }

    private static async Task SaveImageByFormatAsync(Image<Rgb24> image, string path, string format, int jpgQuality)
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
                await image.SaveAsJpegAsync(path, new JpegEncoder { Quality = Math.Clamp(jpgQuality, 70, 100) });
                break;
        }
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


}
