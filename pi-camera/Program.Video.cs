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
    private sealed record RandomSegmentInfo(string SourcePath, string VideoPath, int Fps);

    private static readonly List<RandomSegmentInfo> _previewRandomSegments = new();
    private static string? _previewRandomBasePath;
    private static int _previewRandomSegmentIndex;
    private static DateTime _previewRandomSegmentStartedUtc;
    private static double _previewRandomSegmentSeconds = Math.Clamp(_randomFrameSeconds, 1, 15);

    private static async Task RecordRandomFrameVideoAsync(string outputDir, FramebufferDisplay display, int width, int height)
    {
        if (_previewRecording)
        {
            StopPreviewRecording(display, width, height, "RANDOM STOP");
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
        var finalPath = basePath + (finalFormat == "mp4" ? ".mp4" : ".avi");

        lock (_previewRecordLock)
        {
            _previewRecordStream?.Dispose();

            _previewRecordPath = null;
            _previewRecordFinalPath = finalPath;
            _previewRecordFinalFormat = finalFormat;
            _previewRecording = true;
            _previewRandomRecording = random;
            _previewRecordStartedUtc = DateTime.UtcNow;
            _previewLastFrameWrittenUtc = DateTime.MinValue;
            _previewRandomSecond = -1;
            _previewRandomFps = Math.Clamp(_randomFrameMinFps, 1, 30);

            _previewRandomSegments.Clear();
            _previewRandomBasePath = null;
            _previewRandomSegmentIndex = 0;
            _previewRandomSegmentStartedUtc = DateTime.UtcNow;
            _previewRandomSegmentSeconds = Math.Clamp(_randomFrameSeconds, 1, 15);

            if (random)
            {
                _previewRandomBasePath = basePath;
                StartRandomSegmentLocked();
            }
            else
            {
                var tempPath = basePath + ".rawmjpeg";
                _previewRecordStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                _previewRecordPath = tempPath;
            }
        }

        Console.WriteLine(random
            ? $"[REC] random segmented MJPEG: {basePath}_segXXX -> {finalPath}"
            : $"[REC] preview source MJPEG: {basePath}.rawmjpeg -> {finalPath}");
    }

    private static void StartRandomSegmentLocked()
    {
        if (_previewRandomBasePath is null)
            return;

        _previewRecordStream?.Flush();
        _previewRecordStream?.Dispose();

        var finalFormat = NormalizeVideoFormat(_previewRecordFinalFormat);
        var ext = finalFormat == "mp4" ? ".mp4" : ".avi";

        _previewRandomFps = _randomFrameRandom.Next(
            Math.Clamp(_randomFrameMinFps, 1, 30),
            Math.Clamp(_randomFrameMaxFps, Math.Clamp(_randomFrameMinFps, 1, 30), 30) + 1);

        var sourcePath = $"{_previewRandomBasePath}_seg{_previewRandomSegmentIndex:D3}.rawmjpeg";
        var videoPath = $"{_previewRandomBasePath}_seg{_previewRandomSegmentIndex:D3}{ext}";

        _previewRandomSegmentIndex++;
        _previewRandomSegmentStartedUtc = DateTime.UtcNow;
        _previewLastFrameWrittenUtc = DateTime.MinValue;

        _previewRecordStream = new FileStream(sourcePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        _previewRecordPath = sourcePath;
        _previewRandomSegments.Add(new RandomSegmentInfo(sourcePath, videoPath, _previewRandomFps));

        Console.WriteLine($"[REC] random segment {_previewRandomSegmentIndex}: fps={_previewRandomFps}, seconds={_previewRandomSegmentSeconds:0.#}, source={Path.GetFileName(sourcePath)}");
    }

    private static void StopPreviewRecording(FramebufferDisplay display, int width, int height, string message)
    {
        var result = ClosePreviewRecordingCore();
        if (_glitchVideoRecording)
            StopGlitchVideoMode();

        if (result.wasRandom && result.randomSegments.Count > 0 && result.finalPath is not null)
        {
            DrawSaved(display, width, height, "KLEJENIE RANDOM");
            QueueRandomSegmentsConversion(result.randomSegments, result.finalPath, result.finalFormat);
            return;
        }

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

    private static (string? sourcePath, string? finalPath, string finalFormat, string? displayName, List<RandomSegmentInfo> randomSegments, bool wasRandom) ClosePreviewRecordingCore()
    {
        string? sourcePath;
        string? finalPath;
        string finalFormat;
        bool wasRandom;
        List<RandomSegmentInfo> randomSegments;

        var waitUntil = DateTime.UtcNow.AddMilliseconds(1500);
        while (Interlocked.CompareExchange(ref _recordEncodeBusy, 0, 0) != 0 && DateTime.UtcNow < waitUntil)
            Thread.Sleep(20);

        lock (_previewRecordLock)
        {
            sourcePath = _previewRecordPath;
            finalPath = _previewRecordFinalPath;
            finalFormat = _previewRecordFinalFormat;
            wasRandom = _previewRandomRecording;
            randomSegments = _previewRandomSegments.ToList();

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

            _previewRandomSegments.Clear();
            _previewRandomBasePath = null;
            _previewRandomSegmentIndex = 0;
            _previewRandomSegmentStartedUtc = DateTime.MinValue;
        }

        var dropped = Interlocked.Exchange(ref _recordFramesDropped, 0);
        if (dropped > 0)
            Console.WriteLine($"[REC] dropped frames while encoding: {dropped}");

        var displayName = finalPath is null ? null : Path.GetFileName(finalPath);

        return (sourcePath, finalPath, finalFormat, displayName, randomSegments, wasRandom);
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

    private static void QueueRandomSegmentsConversion(List<RandomSegmentInfo> segments, string finalPath, string finalFormat)
    {
        _ = Task.Run(async () =>
        {
            Interlocked.Increment(ref _backgroundVideoConversions);
            try
            {
                if (segments.Count == 0)
                    return;

                Directory.CreateDirectory(Path.GetDirectoryName(finalPath) ?? ".");

                var converted = new List<string>();

                foreach (var segment in segments)
                {
                    if (!File.Exists(segment.SourcePath) || new FileInfo(segment.SourcePath).Length == 0)
                    {
                        Console.WriteLine($"[REC] skip empty random segment: {Path.GetFileName(segment.SourcePath)}");
                        TryDelete(segment.SourcePath);
                        continue;
                    }

                    var ok = await ConvertRawMjpegAsync(segment.SourcePath, segment.VideoPath, finalFormat, segment.Fps);
                    if (!ok)
                    {
                        Console.WriteLine($"[REC] random segment conversion failed: {Path.GetFileName(segment.SourcePath)}");
                        continue;
                    }

                    converted.Add(segment.VideoPath);
                    Console.WriteLine($"[REC] random segment ready: {Path.GetFileName(segment.VideoPath)} fps={segment.Fps}");
                }

                if (converted.Count == 0)
                {
                    Console.WriteLine("[REC] no random segments to concat");
                    return;
                }

                var listPath = Path.ChangeExtension(finalPath, ".concat.txt");
                await File.WriteAllLinesAsync(listPath, converted.Select(p => $"file '{EscapeConcatPath(p)}'"));

                var concatOk = await ConcatVideosAsync(listPath, finalPath);
                if (concatOk && File.Exists(finalPath))
                {
                    Console.WriteLine($"[REC] random final ready: {finalPath}");

                    TryDelete(listPath);

                    foreach (var segment in segments)
                        TryDelete(segment.SourcePath);

                    foreach (var path in converted)
                        TryDelete(path);
                }
                else
                {
                    Console.WriteLine("[REC] random concat failed, temporary files left for debug");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[REC] random convert error: " + ex);
            }
            finally
            {
                Interlocked.Decrement(ref _backgroundVideoConversions);
            }
        });
    }

    private static string EscapeConcatPath(string path)
    {
        return Path.GetFullPath(path).Replace("'", "'\\''");
    }

    private static async Task<bool> ConcatVideosAsync(string listPath, string finalPath)
    {
        var args = $"-y -f concat -safe 0 -i \"{listPath}\" -c copy \"{finalPath}\"";

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
                Console.WriteLine("[FFMPEG CONCAT] " + stderr.Trim());
            else if (!string.IsNullOrWhiteSpace(stdout))
                Console.WriteLine("[FFMPEG CONCAT] " + stdout.Trim());
            return false;
        }

        return File.Exists(finalPath);
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
            targetFps = random ? _previewRandomFps : _previewFps;
        }

        var now = DateTime.UtcNow;

        if (_glitchVideoRecording)
            MaybeApplyGlitchVideoStep();
        if (random)
        {
            lock (_previewRecordLock)
            {
                if (_previewRecording &&
                    _previewRandomRecording &&
                    _previewRandomBasePath is not null &&
                    (now - _previewRandomSegmentStartedUtc).TotalSeconds >= _previewRandomSegmentSeconds)
                {
                    StartRandomSegmentLocked();
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
