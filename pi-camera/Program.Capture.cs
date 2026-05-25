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
