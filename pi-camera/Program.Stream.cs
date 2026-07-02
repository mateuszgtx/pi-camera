using System.Diagnostics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using pi_camera.Services;

namespace pi_camera;

public static partial class Program
{
    private static async Task ToggleStreamAsync(FramebufferDisplay display, int width, int height, int requestedAction)
    {
        // requestedAction: 0 = toggle, 1 = start, 2 = stop
        try
        {
            if (requestedAction == 1 && _streaming)
            {
                DrawSaved(display, width, height, "STREAM RUNNING");
                await Task.Delay(100);
                return;
            }

            if (requestedAction == 2 && !_streaming)
            {
                DrawSaved(display, width, height, "STREAM STOP");
                await Task.Delay(100);
                return;
            }

            if (_streaming && requestedAction != 1)
            {
                StopStreamCore(logOnly: false);
                DrawSaved(display, width, height, "STREAM STOP");
                await Task.Delay(100);
                return;
            }

            if (!_streaming && requestedAction != 2)
            {
                StartStreamCore();
                DrawSaved(display, width, height, "STREAM START");
                await Task.Delay(100);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[STREAM] " + ex);
            DrawError(display, width, height, ShortError(ex.Message));
            await Task.Delay(1200);
        }
    }

    private static void StartStreamCore()
    {
        var url = (_streamUrl ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("streamUrl is missing. Set the address through the API.");

        StopStreamCore(logOnly: true);

        var fps = Math.Clamp(_streamFps, 1, 30);
        var bitrate = Math.Clamp(_streamBitrateKbps, 256, 20000);
        var muxer = ResolveStreamMuxer(_streamOutputFormat, url);

        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        ApplyAudioServerEnvironment(psi);

        foreach (var arg in BuildStreamFfmpegArgs(url, muxer, fps, bitrate))
            psi.ArgumentList.Add(arg);

        var process = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                Console.WriteLine("[STREAM FFMPEG] " + e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                Console.WriteLine("[STREAM FFMPEG] " + e.Data);
        };

        process.Exited += (_, _) =>
        {
            lock (_streamLock)
            {
                if (ReferenceEquals(_streamProcess, process))
                {
                    _streaming = false;
                    _streamInput = null;
                    _streamProcess = null;
                }
            }

            try { process.Dispose(); } catch { }
            Console.WriteLine("[STREAM] ffmpeg exited");
        };

        if (!process.Start())
            throw new InvalidOperationException("Could not start ffmpeg.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        lock (_streamLock)
        {
            _streamProcess = process;
            _streamInput = process.StandardInput.BaseStream;
            _streaming = true;
            _streamStartedUtc = DateTime.UtcNow;
            _streamLastFrameWrittenUtc = DateTime.MinValue;
            _streamFramesDropped = 0;
        }

        var audio = ResolveAudioCaptureSource();
        Console.WriteLine($"[STREAM] start: muxer={muxer}, fps={fps}, bitrate={bitrate}k, raw={_streamUseRaw}, audio={(audio is null ? "off" : audio.Label)}, url={MaskStreamUrl(url)}");
    }

    private static void StopStreamCore(bool logOnly)
    {
        Process? process;
        System.IO.Stream? input;
        int dropped;

        lock (_streamLock)
        {
            process = _streamProcess;
            input = _streamInput;
            dropped = _streamFramesDropped;

            _streamInput = null;
            _streamProcess = null;
            _streaming = false;
            _streamStartedUtc = DateTime.MinValue;
            _streamLastFrameWrittenUtc = DateTime.MinValue;
            _streamFramesDropped = 0;
        }

        try { input?.Flush(); } catch { }
        try { input?.Dispose(); } catch { }

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    if (!process.WaitForExit(1800))
                        process.Kill(entireProcessTree: true);
                }
            }
            catch { }

            try { process.Dispose(); } catch { }
        }

        if (!logOnly || dropped > 0)
            Console.WriteLine($"[STREAM] stop, dropped frames={dropped}");
    }

    private static void WriteStreamFrameIfNeeded(byte[] rgb, int width, int height)
    {
        Process? process;
        int targetFps;

        lock (_streamLock)
        {
            if (!_streaming || _streamInput is null || _streamProcess is null)
                return;

            process = _streamProcess;
            targetFps = Math.Clamp(_streamFps, 1, 30);
        }

        bool processExited;
        try
        {
            processExited = process.HasExited;
        }
        catch
        {
            StopStreamCore(logOnly: true);
            return;
        }

        if (processExited)
        {
            StopStreamCore(logOnly: true);
            return;
        }

        var now = DateTime.UtcNow;
        var minGapMs = 1000.0 / targetFps;

        lock (_streamLock)
        {
            if ((now - _streamLastFrameWrittenUtc).TotalMilliseconds < minGapMs)
                return;

            _streamLastFrameWrittenUtc = now;
        }

        if (Interlocked.CompareExchange(ref _streamEncodeBusy, 1, 0) != 0)
        {
            lock (_streamLock) _streamFramesDropped++;
            return;
        }

        var copy = rgb.ToArray();

        _ = Task.Run(() =>
        {
            try
            {
                var jpeg = EncodeStreamFrameJpeg(copy, width, height);

                lock (_streamLock)
                {
                    if (_streaming && _streamInput is not null)
                    {
                        _streamInput.Write(jpeg, 0, jpeg.Length);
                        _streamInput.Flush();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[STREAM] frame write error: " + ex.Message);
                StopStreamCore(logOnly: true);
            }
            finally
            {
                Interlocked.Exchange(ref _streamEncodeBusy, 0);
            }
        });
    }

    private static byte[] EncodeStreamFrameJpeg(byte[] rgb, int srcW, int srcH)
    {
        using var image = new Image<Rgb24>(srcW, srcH);

        if (_streamUseRaw)
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
        image.SaveAsJpeg(ms, new JpegEncoder { Quality = Math.Clamp(_streamJpegQuality, 35, 95) });
        return ms.ToArray();
    }

    private static IEnumerable<string> BuildStreamFfmpegArgs(string url, string muxer, int fps, int bitrate)
    {
        var gop = Math.Clamp(fps * 2, 2, 120);
        var audio = ResolveAudioCaptureSource();

        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            "-fflags", "nobuffer",
            "-thread_queue_size", "512",
            "-f", "mjpeg",
            "-framerate", fps.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-i", "pipe:0"
        };

        if (audio is not null)
            AddAudioInputArgs(args, audio);

        args.AddRange(new[]
        {
            "-map", "0:v:0"
        });

        if (audio is not null)
        {
            args.Add("-map");
            args.Add("1:a:0?");
        }
        else
        {
            args.Add("-an");
        }

        args.AddRange(new[]
        {
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-tune", "zerolatency",
            "-pix_fmt", "yuv420p",
            "-b:v", bitrate + "k",
            "-maxrate", bitrate + "k",
            "-bufsize", Math.Max(bitrate * 2, 512) + "k",
            "-g", gop.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-r", fps.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });

        if (audio is not null)
            args.AddRange(BuildAudioCodecArgs());

        if (muxer == "rtsp")
        {
            args.Add("-rtsp_transport");
            args.Add("tcp");
        }

        args.Add("-f");
        args.Add(muxer);
        args.Add(url);
        return args;
    }

    private static string ResolveStreamMuxer(string format, string url)
    {
        format = NormalizeStreamOutputFormat(format);
        if (format != "auto")
            return format;

        var lower = url.Trim().ToLowerInvariant();
        if (lower.StartsWith("rtmp://") || lower.StartsWith("rtmps://")) return "flv";
        if (lower.StartsWith("srt://") || lower.StartsWith("udp://")) return "mpegts";
        if (lower.StartsWith("rtsp://")) return "rtsp";
        return "flv";
    }

    private static string NormalizeStreamOutputFormat(string format)
    {
        return (format ?? "auto").Trim().ToLowerInvariant() switch
        {
            "flv" or "rtmp" => "flv",
            "mpegts" or "ts" or "srt" or "udp" => "mpegts",
            "rtsp" => "rtsp",
            _ => "auto"
        };
    }

    private static string MaskStreamUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "";

        try
        {
            var uri = new Uri(url);
            if (string.IsNullOrEmpty(uri.UserInfo))
                return url;

            var builder = new UriBuilder(uri) { UserName = "***", Password = "***" };
            return builder.Uri.ToString();
        }
        catch
        {
            return url.Length <= 12 ? "***" : url[..6] + "..." + url[^6..];
        }
    }
}
