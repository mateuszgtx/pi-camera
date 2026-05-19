using System.Diagnostics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace pi_camera.Services;

public sealed class CameraPreviewService : IDisposable
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private PreviewSettings _settings;

    private Process? _process;
    private CancellationTokenSource? _cts;

    public event Action<PreviewFrame>? FrameReady;
    public event Action<string>? StatusChanged;

    public CameraPreviewService(int width, int height, int fps, PreviewSettings settings)
    {
        _width = width;
        _height = height;
        _fps = fps;
        _settings = settings.Clone();
    }

    public void UpdateSettings(PreviewSettings settings)
    {
        _settings = settings.Clone();
    }

    public void Start()
    {
        if (_process is not null && !_process.HasExited)
            return;

        _cts = new CancellationTokenSource();

        var args =
            $"--codec mjpeg " +
            $"-t 0 " +
            $"--width {_width} --height {_height} " +
            $"--framerate {_fps} " +
            $"--nopreview " +
            $"--ev {_settings.Ev.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"--sharpness {_settings.Sharpness.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"--contrast {_settings.Contrast.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"--saturation {_settings.Saturation.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"--brightness {_settings.Brightness.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"--denoise {_settings.Denoise} " +
            $"-o -";

        StatusChanged?.Invoke("rpicam-vid " + args);

        _process = new Process
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

        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                StatusChanged?.Invoke(e.Data);
        };

        _process.Start();
        _process.BeginErrorReadLine();

        _ = Task.Run(() => ReadMjpegAsync(_process.StandardOutput.BaseStream, _cts.Token));
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();

            if (_process is not null && !_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch { }
        finally
        {
            _process?.Dispose();
            _process = null;
        }
    }

    private async Task ReadMjpegAsync(Stream stream, CancellationToken token)
    {
        var buffer = new List<byte>(1024 * 512);
        var temp = new byte[8192];

        while (!token.IsCancellationRequested)
        {
            int read;

            try
            {
                read = await stream.ReadAsync(temp.AsMemory(0, temp.Length), token);
            }
            catch
            {
                break;
            }

            if (read <= 0)
                break;

            for (var i = 0; i < read; i++)
                buffer.Add(temp[i]);

            while (true)
            {
                var start = FindMarker(buffer, 0xFF, 0xD8, 0);
                if (start < 0)
                {
                    if (buffer.Count > 1024 * 1024)
                        buffer.Clear();
                    break;
                }

                var end = FindMarker(buffer, 0xFF, 0xD9, start + 2);
                if (end < 0)
                {
                    if (start > 0)
                        buffer.RemoveRange(0, start);
                    break;
                }

                var len = end - start + 2;
                var jpg = buffer.GetRange(start, len).ToArray();
                buffer.RemoveRange(0, end + 2);

                try
                {
                    using var image = Image.Load<Rgb24>(jpg);
                    var rgb = new byte[image.Width * image.Height * 3];

                    image.ProcessPixelRows(accessor =>
                    {
                        for (var y = 0; y < image.Height; y++)
                        {
                            var row = accessor.GetRowSpan(y);
                            var offset = y * image.Width * 3;

                            for (var x = 0; x < image.Width; x++)
                            {
                                rgb[offset + x * 3 + 0] = row[x].R;
                                rgb[offset + x * 3 + 1] = row[x].G;
                                rgb[offset + x * 3 + 2] = row[x].B;
                            }
                        }
                    });

                    FrameReady?.Invoke(new PreviewFrame(image.Width, image.Height, rgb));
                }
                catch
                {
                    // pomiń uszkodzoną klatkę
                }
            }
        }
    }

    private static int FindMarker(List<byte> data, byte a, byte b, int from)
    {
        for (var i = from; i < data.Count - 1; i++)
        {
            if (data[i] == a && data[i + 1] == b)
                return i;
        }

        return -1;
    }

    public void Dispose() => Stop();
}

public sealed record PreviewFrame(int Width, int Height, byte[] Rgb);
