using System.Diagnostics;
using System.Globalization;

namespace pi_camera.Services;

public sealed class CameraService
{
    private readonly string _outputDir;
    private Process? _videoProcess;

    public bool IsRecording => _videoProcess is not null && !_videoProcess.HasExited;
    public string? CurrentVideoPath { get; private set; }

    public CameraService(string outputDir)
    {
        _outputDir = outputDir;
        Directory.CreateDirectory(_outputDir);
    }

    public async Task<string> TakePhotoAsync(CameraSettings settings)
    {
        var ext = settings.PhotoFormat switch
        {
            PhotoFormat.Png => "png",
            PhotoFormat.Bmp => "bmp",
            _ => "jpg"
        };

        var path = Path.Combine(_outputDir, $"IMG_{DateTime.Now:yyyyMMdd_HHmmss}.{ext}");
        var (w, h) = settings.PhotoSize;

        var args =
            $"-o \"{path}\" " +
            $"--width {w} --height {h} " +
            $"--nopreview " +
            $"--ev {settings.PhotoEv.ToString(CultureInfo.InvariantCulture)} " +
            $"--metering {settings.Metering} " +
            $"--denoise cdn_hq " +
            $"--sharpness 0.5 " +
            $"--contrast 0.9 " +
            $"--saturation 0.9 ";

        if (settings.PhotoFormat == PhotoFormat.Jpg)
            args += $"--quality {settings.JpegQuality} ";
        else
            args += $"--encoding {ext} ";

        var result = await RunAsync("rpicam-still", args, 180_000);

        if (result.ExitCode != 0)
            throw new Exception(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);

        return path;
    }

    public string StartVideo(CameraSettings settings)
    {
        if (IsRecording)
            return CurrentVideoPath ?? "";

        var ext = settings.VideoFormat switch
        {
            VideoFormat.Mjpeg => "mjpeg",
            _ => "h264"
        };

        var path = Path.Combine(_outputDir, $"VID_{DateTime.Now:yyyyMMdd_HHmmss}.{ext}");

        var codec = settings.VideoFormat switch
        {
            VideoFormat.Mjpeg => "mjpeg",
            _ => "h264"
        };

        var timeout = settings.VideoSeconds <= 0 ? 0 : settings.VideoSeconds * 1000;

        var args =
            $"--codec {codec} " +
            $"-t {timeout} " +
            $"--width 1920 --height 1080 " +
            $"--framerate 30 " +
            $"--nopreview " +
            $"--ev {settings.PhotoEv.ToString(CultureInfo.InvariantCulture)} " +
            $"--denoise cdn_fast " +
            $"-o \"{path}\"";

        _videoProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "rpicam-vid",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            },
            EnableRaisingEvents = true
        };

        _videoProcess.Start();
        CurrentVideoPath = path;
        return path;
    }

    public void StopVideo()
    {
        try
        {
            if (_videoProcess is not null && !_videoProcess.HasExited)
                _videoProcess.Kill(entireProcessTree: true);
        }
        catch { }
        finally
        {
            _videoProcess?.Dispose();
            _videoProcess = null;
        }
    }

    private static async Task<CommandResult> RunAsync(string fileName, string args, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);

        var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        p.Start();

        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new Exception("Timeout aparatu");
        }

        return new CommandResult(p.ExitCode, await stdoutTask, await stderrTask);
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error);
}
