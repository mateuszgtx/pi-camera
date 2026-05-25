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



}
