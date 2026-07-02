using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace pi_camera;

public static partial class Program
{
    private sealed record AudioCaptureSource(string Kind, string Format, string Device, string Label);
    private sealed record PulseCardProfile(string Name, string Description, int Sources, bool Available);
    private sealed record PulseBluetoothCard(string Name, string Mac, string ActiveProfile, List<PulseCardProfile> Profiles);
    private sealed record BluetoothDeviceInfo(
        string Mac,
        string Name,
        bool Paired,
        bool Trusted,
        bool Connected,
        bool HasName,
        bool IsLikelyAudio,
        bool IsRandomAddress,
        string Icon,
        string Uuids);

    private static readonly object _bluetoothScanLock = new();
    private static CancellationTokenSource? _bluetoothScanCts;
    private static Task? _bluetoothScanTask;
    private static Process? _bluetoothScanProcess;
    private static bool _bluetoothScanActive;
    private static DateTime _bluetoothScanStartedUtc = DateTime.MinValue;
    private static DateTime _bluetoothScanEndsUtc = DateTime.MinValue;

    private static bool IsAudioEnabled() => _audioEnabled && _audioInputMode != AudioInputMode.Off;

    private static AudioInputMode ParseAudioInputMode(string value)
    {
        return (value ?? "auto").Trim().ToLowerInvariant() switch
        {
            "off" or "none" or "disabled" => AudioInputMode.Off,
            "aux" or "jack" or "line" or "linein" or "line-in" or "alsa" => AudioInputMode.Aux,
            "bluetooth" or "bt" => AudioInputMode.Bluetooth,
            "manual" or "device" => AudioInputMode.Manual,
            _ => AudioInputMode.Auto
        };
    }

    private static string NormalizeAudioInputFormat(string value)
    {
        return (value ?? "auto").Trim().ToLowerInvariant() switch
        {
            "alsa" => "alsa",
            "pulse" or "pulseaudio" or "pipewire" => "pulse",
            _ => "auto"
        };
    }

    private static AudioCaptureSource? ResolveAudioCaptureSource()
    {
        if (!IsAudioEnabled())
            return null;

        var mode = _audioInputMode;
        var manualDevice = (_audioDevice ?? "").Trim();
        var manualFormat = NormalizeAudioInputFormat(_audioInputFormat);

        if (mode == AudioInputMode.Manual && !string.IsNullOrWhiteSpace(manualDevice))
        {
            var format = manualFormat == "auto" ? GuessAudioFormat(manualDevice) : manualFormat;
            return new AudioCaptureSource("manual", format, manualDevice, "Manual: " + manualDevice);
        }

        if (mode is AudioInputMode.Auto or AudioInputMode.Bluetooth)
        {
            var bluetooth = FindBluetoothAudioSource();
            if (bluetooth is not null)
                return bluetooth;

            TryEnsureBluetoothInputProfileRecently();
            bluetooth = FindBluetoothAudioSource();
            if (bluetooth is not null)
                return bluetooth;

            if (mode == AudioInputMode.Bluetooth)
                return null;
        }

        if (mode is AudioInputMode.Auto or AudioInputMode.Aux)
        {
            var aux = FindAuxAudioSource();
            if (aux is not null)
                return aux;

            if (mode == AudioInputMode.Aux)
                return new AudioCaptureSource("aux", "alsa", "default", "AUX/default ALSA");
        }

        var fallback = FindDefaultPulseSource();
        return fallback;
    }

    private static string GuessAudioFormat(string device)
    {
        var lower = (device ?? "").ToLowerInvariant();
        if (lower.StartsWith("hw:") || lower.StartsWith("plughw:") || lower == "default")
            return "alsa";
        if (lower.Contains("bluez") || lower.Contains("pulse"))
            return "pulse";
        return "alsa";
    }

    private static void SetAudioInputMessage(string message)
    {
        _lastAudioInputMessage = message ?? "";
        _lastAudioInputMessageUtc = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(_lastAudioInputMessage))
            Console.WriteLine("[AUDIO] " + _lastAudioInputMessage);
    }

    private static string CurrentAudioInputMessage(AudioCaptureSource? active)
    {
        if (active is not null)
            return "Input: " + active.Label;

        if (!IsAudioEnabled())
            return _audioEnabled ? "Audio source is Off." : "Audio is disabled.";

        var recent = !string.IsNullOrWhiteSpace(_lastAudioInputMessage) && (DateTime.UtcNow - _lastAudioInputMessageUtc).TotalSeconds < 180;
        if (recent)
            return _lastAudioInputMessage;

        if (_audioInputMode is AudioInputMode.Auto or AudioInputMode.Bluetooth)
        {
            try
            {
                EnsureBluetoothDevicesLoaded(force: false);
                if (_bluetoothAllDevices.Any(d => d.Connected && d.IsLikelyAudio))
                    return "Bluetooth is connected, but no microphone input is visible in the app. The app will keep checking PulseAudio/PipeWire automatically; make sure the headset is in hands-free mode.";
            }
            catch
            {
            }
        }

        if (_audioInputMode == AudioInputMode.Manual && string.IsNullOrWhiteSpace(_audioDevice))
            return "Manual audio mode needs a device name.";

        if (_audioInputMode is AudioInputMode.Auto or AudioInputMode.Aux)
            return "No ALSA/Pulse capture source detected.";

        return "No audio input detected.";
    }

    private static void TryEnsureBluetoothInputProfileRecently()
    {
        if (_audioInputMode is not (AudioInputMode.Auto or AudioInputMode.Bluetooth))
            return;

        try
        {
            EnsureBluetoothDevicesLoaded(force: false);
            var target = _bluetoothAllDevices.FirstOrDefault(d => d.Connected && d.IsLikelyAudio)?.Mac
                ?? _bluetoothAllDevices.FirstOrDefault(d => d.Connected)?.Mac;
            if (string.IsNullOrWhiteSpace(target))
                return;

            if ((DateTime.UtcNow - _lastBluetoothInputProfileAttemptUtc).TotalSeconds < 25)
                return;

            _lastBluetoothInputProfileAttemptUtc = DateTime.UtcNow;
            if (TryEnableBluetoothInputProfile(target, out var message))
                SetAudioInputMessage(message);
            TrySetDefaultBluetoothSource();
        }
        catch (Exception ex)
        {
            SetAudioInputMessage("Bluetooth input check failed: " + ShortError(ex.Message));
        }
    }

    private static AudioCaptureSource? FindBluetoothAudioSource()
    {
        try
        {
            if (!IsBluetoothRadioOn())
                return null;
        }
        catch
        {
        }

        foreach (var source in ListPulseSources())
        {
            var lower = (source.Device + " " + source.Label).ToLowerInvariant();
            if (lower.Contains("bluez") || lower.Contains("bluetooth"))
                return source with { Kind = "bluetooth" };
        }

        return null;
    }

    private static AudioCaptureSource? FindAuxAudioSource()
    {
        var alsa = ListAlsaCaptureSources();
        if (alsa.Count == 0)
            return null;

        var preferred = alsa.FirstOrDefault(s =>
        {
            var lower = (s.Device + " " + s.Label).ToLowerInvariant();
            return lower.Contains("usb") || lower.Contains("mic") || lower.Contains("audio") || lower.Contains("card");
        });

        return preferred ?? alsa[0];
    }

    private static AudioCaptureSource? FindDefaultPulseSource()
    {
        try
        {
            var name = RunPulseText(new[] { "get-default-source" }, 1200).Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Contains("monitor", StringComparison.OrdinalIgnoreCase))
                return null;

            return new AudioCaptureSource("default", "pulse", name, "Default: " + ShortAudioLabel(name));
        }
        catch
        {
            return null;
        }
    }

    private static List<AudioCaptureSource> ListPulseSources()
    {
        var list = new List<AudioCaptureSource>();
        var labels = ReadPulseSourceLabels();

        foreach (var output in RunPulseTextCandidates(new[] { "list", "short", "sources" }, 1800))
        {
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('	', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    continue;

                var name = parts[1].Trim();
                AddPulseSource(list, name, labels.TryGetValue(name, out var label) ? label : "");
            }
        }

        AddWpctlBluetoothSources(list);

        foreach (var item in labels)
            AddPulseSource(list, item.Key, item.Value);

        return list
            .GroupBy(s => s.Device, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.Kind == "bluetooth").First())
            .ToList();
    }

    private static void AddWpctlBluetoothSources(List<AudioCaptureSource> list)
    {
        try
        {
            var output = RunPipeWireText("wpctl", new[] { "status" }, 2200);
            var rx = new Regex(@"\b(bluez_input\.[0-9A-Fa-f:_\.-]+)", RegexOptions.IgnoreCase);
            foreach (Match match in rx.Matches(output))
                AddPulseSource(list, match.Groups[1].Value, "Bluetooth microphone");
        }
        catch
        {
        }
    }

    private static Dictionary<string, string> ReadPulseSourceLabels()
    {
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var output in RunPulseTextCandidates(new[] { "list", "sources" }, 2600))
        {
            foreach (var block in Regex.Split(output, @"(?=^Source #)", RegexOptions.Multiline))
            {
                var name = MatchPulseField(block, "Name");
                if (string.IsNullOrWhiteSpace(name) || name.Contains("monitor", StringComparison.OrdinalIgnoreCase))
                    continue;

                var description = MatchPulseField(block, "Description");
                var deviceDescription = MatchPulseProperty(block, "device.description");
                var bluetoothAlias = MatchPulseProperty(block, "bluez.alias");
                var label = FirstNonEmpty(bluetoothAlias, deviceDescription, description, name);
                labels[name] = ShortAudioLabel(label);
            }
        }

        return labels;
    }

    private static void AddPulseSource(List<AudioCaptureSource> list, string name, string label)
    {
        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Contains("monitor", StringComparison.OrdinalIgnoreCase))
            return;

        var lower = (name + " " + label).ToLowerInvariant();
        var kind = lower.Contains("bluez") || lower.Contains("bluetooth") ? "bluetooth" : "pulse";
        if (list.Any(s => s.Device.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return;

        list.Add(new AudioCaptureSource(kind, "pulse", name, ShortAudioLabel(string.IsNullOrWhiteSpace(label) ? name : label)));
    }

    private static string MatchPulseField(string text, string key)
    {
        var match = Regex.Match(text ?? "", @"^\s*" + Regex.Escape(key) + @":\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : "";
    }

    private static string MatchPulseProperty(string text, string key)
    {
        var match = Regex.Match(text ?? "", "^\\s*" + Regex.Escape(key) + "\\s*=\\s*\"?([^\"\\r\\n]+)\"?", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : "";
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }
        return "";
    }

    private static List<AudioCaptureSource> ListAlsaCaptureSources()
    {
        var list = new List<AudioCaptureSource>();
        try
        {
            var output = RunProcessText("arecord", new[] { "-l" }, 1800);
            var rx = new Regex(@"card\s+(\d+):\s*([^,\[]+)(?:\s*\[[^\]]+\])?,\s*device\s+(\d+):\s*([^\[]+)", RegexOptions.IgnoreCase);
            foreach (Match m in rx.Matches(output))
            {
                var card = m.Groups[1].Value.Trim();
                var cardName = m.Groups[2].Value.Trim();
                var dev = m.Groups[3].Value.Trim();
                var devName = m.Groups[4].Value.Trim();
                var label = ($"{cardName} {devName}").Trim();
                if (string.IsNullOrWhiteSpace(label))
                    label = $"ALSA card {card} device {dev}";

                var lower = label.ToLowerInvariant();
                if (lower.Contains("loopback"))
                    continue;

                list.Add(new AudioCaptureSource("aux", "alsa", $"plughw:{card},{dev}", ShortAudioLabel(label)));
            }
        }
        catch
        {
        }

        return list;
    }

    private static string ShortAudioLabel(string label)
    {
        label = (label ?? "").Replace("alsa_input.", "").Replace("bluez_input.", "BT ").Replace('_', ' ').Trim();
        return label.Length > 36 ? label[..36] : label;
    }

    private static object CurrentAudioStatus()
    {
        var active = ResolveAudioCaptureSource();
        var sources = ListAudioCaptureSources();
        return new
        {
            enabled = IsAudioEnabled(),
            audioEnabled = _audioEnabled,
            audioInputMode = _audioInputMode.ToString(),
            audioInputFormat = _audioInputFormat,
            audioDevice = _audioDevice,
            audioSampleRate = _audioSampleRate,
            audioBitrateKbps = _audioBitrateKbps,
            recordingAudio = _audioRecordProcess is not null,
            active = active is null ? null : new { active.Kind, active.Format, active.Device, active.Label },
            listenUrl = active is null ? null : "/api/audio/listen.wav",
            message = CurrentAudioInputMessage(active),
            sources,
            bluetooth = CurrentBluetoothStatus()
        };
    }

    private static object[] ListAudioCaptureSources()
    {
        var all = new List<AudioCaptureSource>();
        all.AddRange(ListPulseSources());
        all.AddRange(ListAlsaCaptureSources());

        if (all.Count == 0)
            all.Add(new AudioCaptureSource("default", "alsa", "default", "Default ALSA"));

        return all
            .GroupBy(s => s.Format + "|" + s.Device)
            .Select(g => g.First())
            .Select(s => new { s.Kind, s.Format, s.Device, s.Label })
            .Cast<object>()
            .ToArray();
    }

    private static async Task StreamAudioListenAsync(HttpContext context)
    {
        var source = ResolveAudioCaptureSource();
        if (source is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new { ok = false, message = CurrentAudioInputMessage(null) }, context.RequestAborted);
            return;
        }

        var token = context.RequestAborted;
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (source.Format == "pulse")
            ApplyAudioServerEnvironment(psi);

        foreach (var arg in BuildAudioListenArgs(source))
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                Console.WriteLine("[AUDIO LISTEN] " + e.Data);
        };

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Could not start ffmpeg audio monitor.");
            process.BeginErrorReadLine();

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "audio/wav";
            context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate, max-age=0";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers.Expires = "0";
            context.Response.Headers["X-Accel-Buffering"] = "no";

            Console.WriteLine($"[AUDIO LISTEN] streaming from {source.Label} ({source.Format}:{source.Device})");
            await process.StandardOutput.BaseStream.CopyToAsync(context.Response.Body, token);
        }
        catch (OperationCanceledException)
        {
            // Browser stopped listening.
        }
        catch (Exception ex)
        {
            Console.WriteLine("[AUDIO LISTEN] failed: " + ex.Message);
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsJsonAsync(new { ok = false, message = ShortError(ex.Message) }, CancellationToken.None);
            }
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    try { process.StandardInput.WriteLine("q"); process.StandardInput.Flush(); } catch { }
                    if (!process.WaitForExit(1200))
                        process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        }
    }

    private static async Task StreamAudioListenRawAsync(HttpContext context)
    {
        var source = ResolveAudioCaptureSource();
        if (source is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new { ok = false, message = CurrentAudioInputMessage(null) }, context.RequestAborted);
            return;
        }

        var requestedRate = _audioSampleRate;
        if (int.TryParse(context.Request.Query["rate"].FirstOrDefault(), out var queryRate))
            requestedRate = queryRate;
        requestedRate = Math.Clamp(requestedRate, 8000, 96000);

        var token = context.RequestAborted;
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (source.Format == "pulse")
            ApplyAudioServerEnvironment(psi);

        foreach (var arg in BuildAudioListenRawArgs(source, requestedRate))
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                Console.WriteLine("[AUDIO LISTEN RAW] " + e.Data);
        };

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Could not start ffmpeg low latency audio monitor.");
            process.BeginErrorReadLine();

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/octet-stream";
            context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate, max-age=0";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers.Expires = "0";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Audio-Format"] = "s16le";
            context.Response.Headers["X-Audio-Channels"] = "1";
            context.Response.Headers["X-Audio-Sample-Rate"] = requestedRate.ToString(System.Globalization.CultureInfo.InvariantCulture);

            Console.WriteLine($"[AUDIO LISTEN RAW] streaming from {source.Label} ({source.Format}:{source.Device}) at {requestedRate} Hz");

            var buffer = new byte[2048];
            while (true)
            {
                var read = await process.StandardOutput.BaseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
                if (read <= 0)
                    break;

                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), token);
                await context.Response.Body.FlushAsync(token);
            }
        }
        catch (OperationCanceledException)
        {
            // Browser stopped listening.
        }
        catch (Exception ex)
        {
            Console.WriteLine("[AUDIO LISTEN RAW] failed: " + ex.Message);
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsJsonAsync(new { ok = false, message = ShortError(ex.Message) }, CancellationToken.None);
            }
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    try { process.StandardInput.WriteLine("q"); process.StandardInput.Flush(); } catch { }
                    if (!process.WaitForExit(1000))
                        process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        }
    }

    private static IEnumerable<string> BuildAudioListenArgs(AudioCaptureSource source)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            "-fflags", "nobuffer",
            "-flags", "low_delay",
            "-probesize", "32",
            "-analyzeduration", "0"
        };

        AddAudioInputArgs(args, source, lowLatency: true);
        args.AddRange(new[]
        {
            "-vn",
            "-ac", "1",
            "-ar", Math.Clamp(_audioSampleRate, 8000, 96000).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-c:a", "pcm_s16le",
            "-f", "wav",
            "-flush_packets", "1",
            "pipe:1"
        });

        return args;
    }

    private static IEnumerable<string> BuildAudioListenRawArgs(AudioCaptureSource source, int sampleRate)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            "-fflags", "nobuffer",
            "-flags", "low_delay",
            "-probesize", "32",
            "-analyzeduration", "0"
        };

        AddAudioInputArgs(args, source, lowLatency: true);
        args.AddRange(new[]
        {
            "-vn",
            "-ac", "1",
            "-ar", Math.Clamp(sampleRate, 8000, 96000).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-c:a", "pcm_s16le",
            "-f", "s16le",
            "-flush_packets", "1",
            "pipe:1"
        });

        return args;
    }

    private static void StartAudioRecordingForVideo(string basePath)
    {
        StopAudioRecordingForVideo();

        var source = ResolveAudioCaptureSource();
        if (source is null)
        {
            Console.WriteLine("[AUDIO] no input source, recording video without sound");
            return;
        }

        var audioPath = basePath + ".audio.wav";
        Directory.CreateDirectory(Path.GetDirectoryName(audioPath) ?? ".");

        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (source.Format == "pulse")
            ApplyAudioServerEnvironment(psi);

        foreach (var arg in BuildAudioRecordArgs(source, audioPath))
            psi.ArgumentList.Add(arg);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Console.WriteLine("[AUDIO FFMPEG] " + e.Data); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Console.WriteLine("[AUDIO FFMPEG] " + e.Data); };
        process.Exited += (_, _) => Console.WriteLine("[AUDIO] recorder exited");

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Could not start ffmpeg audio recorder.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            lock (_audioLock)
            {
                _audioRecordProcess = process;
                _audioRecordPath = audioPath;
                _audioRecordDeviceLabel = source.Label;
            }

            Console.WriteLine($"[AUDIO] recording from {source.Label} ({source.Format}:{source.Device}) -> {Path.GetFileName(audioPath)}");
        }
        catch (Exception ex)
        {
            try { process.Dispose(); } catch { }
            Console.WriteLine("[AUDIO] recorder start failed: " + ex.Message);
        }
    }

    private static IEnumerable<string> BuildAudioRecordArgs(AudioCaptureSource source, string audioPath)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            "-y"
        };

        AddAudioInputArgs(args, source);
        args.AddRange(new[]
        {
            "-vn",
            "-ac", "1",
            "-ar", Math.Clamp(_audioSampleRate, 8000, 96000).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-c:a", "pcm_s16le",
            audioPath
        });
        return args;
    }

    private static string? StopAudioRecordingForVideo()
    {
        Process? process;
        string? path;

        lock (_audioLock)
        {
            process = _audioRecordProcess;
            path = _audioRecordPath;
            _audioRecordProcess = null;
            _audioRecordPath = null;
            _audioRecordDeviceLabel = null;
        }

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    try { process.StandardInput.WriteLine("q"); process.StandardInput.Flush(); } catch { }
                    if (!process.WaitForExit(2200))
                        process.Kill(entireProcessTree: true);
                }
            }
            catch { }

            try { process.Dispose(); } catch { }
        }

        if (path is not null && File.Exists(path) && new FileInfo(path).Length > 4096)
            return path;

        if (path is not null)
            TryDelete(path);

        return null;
    }

    private static void AddAudioInputArgs(List<string> args, AudioCaptureSource source, bool lowLatency = false)
    {
        args.Add("-thread_queue_size");
        args.Add(lowLatency ? "32" : "512");

        if (source.Format == "pulse")
        {
            args.Add("-f");
            args.Add("pulse");
            args.Add("-i");
            args.Add(source.Device);
        }
        else
        {
            args.Add("-f");
            args.Add("alsa");
            args.Add("-ar");
            args.Add(Math.Clamp(_audioSampleRate, 8000, 96000).ToString(System.Globalization.CultureInfo.InvariantCulture));
            args.Add("-ac");
            args.Add("1");
            args.Add("-i");
            args.Add(source.Device);
        }
    }

    private static List<string> BuildAudioCodecArgs()
    {
        var bitrate = Math.Clamp(_audioBitrateKbps, 32, 512);
        return new List<string>
        {
            "-c:a", "aac",
            "-b:a", bitrate + "k",
            "-ar", Math.Clamp(_audioSampleRate, 8000, 96000).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-ac", "2"
        };
    }

    private static object CurrentBluetoothStatus()
    {
        var powered = false;
        var radio = "?";
        try
        {
            powered = IsBluetoothRadioOn();
            radio = powered ? "ON" : "OFF";
        }
        catch (Exception ex)
        {
            radio = "ERROR: " + ShortError(ex.Message);
        }

        EnsureBluetoothDevicesLoaded(force: true);
        var selected = SelectedBluetoothDevice();
        var scanActive = IsBluetoothScanActive();
        var remainingSeconds = scanActive ? Math.Max(0, (int)Math.Ceiling((_bluetoothScanEndsUtc - DateTime.UtcNow).TotalSeconds)) : 0;
        var action = BluetoothActionApiView();
        return new
        {
            powered,
            radio,
            scanning = scanActive,
            scanStartedUtc = _bluetoothScanStartedUtc == DateTime.MinValue ? (DateTime?)null : _bluetoothScanStartedUtc,
            scanEndsUtc = _bluetoothScanEndsUtc == DateTime.MinValue ? (DateTime?)null : _bluetoothScanEndsUtc,
            scanRemainingSeconds = remainingSeconds,
            action,
            devices = _bluetoothDevices.Select(d => BluetoothDeviceApiView(d)).ToArray(),
            allDevices = _bluetoothAllDevices.Select(d => BluetoothDeviceApiView(d)).ToArray(),
            hiddenCount = Math.Max(0, _bluetoothAllDevices.Count - _bluetoothDevices.Count),
            lastScanUtc = _lastBluetoothScanUtc == DateTime.MinValue ? (DateTime?)null : _lastBluetoothScanUtc,
            lastScanLog = _bluetoothLastScanLog,
            selected = selected is null ? null : BluetoothDeviceApiView(selected)
        };
    }

    private static string BluetoothRadioStatusLabel()
    {
        try { return IsBluetoothRadioOn() ? "ON" : "OFF"; }
        catch { return "?"; }
    }

    private static bool HasConnectedBluetoothAudioDevice()
    {
        try
        {
            EnsureBluetoothDevicesLoaded(force: false);
            return _bluetoothAllDevices.Any(d => d.Connected && d.IsLikelyAudio) || _bluetoothAllDevices.Any(d => d.Connected);
        }
        catch
        {
            return false;
        }
    }

    private static string BluetoothScanLabel()
    {
        if (!IsBluetoothScanActive())
            return "START";

        var left = Math.Max(0, (int)Math.Ceiling((_bluetoothScanEndsUtc - DateTime.UtcNow).TotalSeconds));
        return left > 0 ? $"CANCEL {left}s" : "CANCEL";
    }

    private static void SetBluetoothActionStatus(string action, string message, bool busy, bool ok = true)
    {
        lock (_bluetoothActionLock)
        {
            _bluetoothAction = action ?? "";
            _bluetoothActionMessage = message ?? "";
            _bluetoothActionBusy = busy;
            _bluetoothActionOk = ok;
            _bluetoothActionUtc = DateTime.UtcNow;
        }

        if (!string.IsNullOrWhiteSpace(message))
            Console.WriteLine("[BT] " + message);
    }

    private static object BluetoothActionApiView()
    {
        lock (_bluetoothActionLock)
        {
            return new
            {
                busy = _bluetoothActionBusy,
                action = _bluetoothAction,
                message = _bluetoothActionMessage,
                ok = _bluetoothActionOk,
                utc = _bluetoothActionUtc == DateTime.MinValue ? (DateTime?)null : _bluetoothActionUtc
            };
        }
    }

    private static string BluetoothActionMessage()
    {
        lock (_bluetoothActionLock)
            return _bluetoothActionMessage;
    }

    private static async Task EnsureBluetoothAgentAsync()
    {
        try { await RunProcessAsync("bluetoothctl", new List<string> { "agent", "on" }, 3000); } catch { }
        try { await RunProcessAsync("bluetoothctl", new List<string> { "default-agent" }, 3000); } catch { }
        try { await RunProcessAsync("bluetoothctl", new List<string> { "pairable", "on" }, 3000); } catch { }
    }

    private static async Task TryBluetoothCtlAsync(int timeoutMs, params string[] args)
    {
        try { await RunProcessAsync("bluetoothctl", args.ToList(), timeoutMs); }
        catch (Exception ex) { Console.WriteLine("[BT bluetoothctl " + string.Join(' ', args) + "] " + ShortError(ex.Message)); }
    }

    private static async Task<bool> WaitForBluetoothRadioStateAsync(bool on, int timeoutMs)
    {
        var stopAt = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < stopAt)
        {
            try
            {
                if (IsBluetoothRadioOn() == on)
                    return true;
            }
            catch { }
            await Task.Delay(450);
        }

        try { return IsBluetoothRadioOn() == on; } catch { return false; }
    }

    private static async Task<bool> WaitForBluetoothDeviceStateAsync(string mac, Func<BluetoothDeviceInfo, bool> predicate, int timeoutMs)
    {
        mac = NormalizeBluetoothMac(mac);
        if (string.IsNullOrWhiteSpace(mac))
            return false;

        var stopAt = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < stopAt)
        {
            try
            {
                var device = ReadBluetoothInfo(mac, _bluetoothScanNames.TryGetValue(mac, out var name) ? name : "");
                if (device.HasName)
                    _bluetoothScanNames[mac] = device.Name;
                if (predicate(device))
                {
                    _lastBluetoothRefreshUtc = DateTime.MinValue;
                    EnsureBluetoothDevicesLoaded(force: true);
                    return true;
                }
            }
            catch { }

            await Task.Delay(600);
        }

        _lastBluetoothRefreshUtc = DateTime.MinValue;
        EnsureBluetoothDevicesLoaded(force: true);
        try
        {
            var final = _bluetoothAllDevices.FirstOrDefault(d => d.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase))
                ?? ReadBluetoothInfo(mac, _bluetoothScanNames.TryGetValue(mac, out var name) ? name : "");
            return predicate(final);
        }
        catch { return false; }
    }

    private static void SetPulseBluetoothCardsOff()
    {
        foreach (var card in ListPulseBluetoothCards())
        {
            try { RunPulseText(new[] { "set-card-profile", card.Name, "off" }, 2500); }
            catch (Exception ex) { Console.WriteLine("[BT AUDIO OFF] " + ShortError(ex.Message)); }
        }
    }

    private static bool IsBluetoothRadioOn()
    {
        try
        {
            var show = RunProcessText("bluetoothctl", new[] { "show" }, 1800);
            var match = Regex.Match(show, @"^\s*Powered:\s*(yes|no)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (match.Success)
                return match.Groups[1].Value.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
        }

        try
        {
            var output = RunProcessText("rfkill", new[] { "list", "bluetooth" }, 1800);
            if (Regex.IsMatch(output, @"Soft blocked:\s*yes", RegexOptions.IgnoreCase))
                return false;
            if (Regex.IsMatch(output, @"Soft blocked:\s*no", RegexOptions.IgnoreCase))
                return true;
        }
        catch
        {
        }

        return false;
    }

    private static async Task SetBluetoothRadioAsync(bool on)
    {
        SetBluetoothActionStatus(on ? "power-on" : "power-off", on ? "Turning Bluetooth on..." : "Turning Bluetooth off...", true);
        try
        {
            if (on)
            {
                try { await RunProcessAsync("systemctl", new List<string> { "start", "bluetooth" }, 5000); } catch { }
                try { await RunProcessAsync("rfkill", new List<string> { "unblock", "bluetooth" }, 3000); } catch { }
                await TryBluetoothCtlAsync(5000, "power", "on");
                await EnsureBluetoothAgentAsync();

                if (!await WaitForBluetoothRadioStateAsync(true, 8000))
                    throw new Exception("Bluetooth did not turn ON. Check rfkill/service permissions.");

                SetBluetoothActionStatus("power-on", "Bluetooth is ON", false, true);
            }
            else
            {
                await CancelBluetoothScanAsync();
                try { await RunProcessAsync("bluetoothctl", new List<string> { "scan", "off" }, 3000); } catch { }

                _lastBluetoothRefreshUtc = DateTime.MinValue;
                EnsureBluetoothDevicesLoaded(force: true);
                var connected = _bluetoothAllDevices.Where(d => d.Connected).Select(d => d.Mac).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                SetPulseBluetoothCardsOff();
                foreach (var mac in connected)
                {
                    await TryBluetoothCtlAsync(6000, "disconnect", mac);
                    await WaitForBluetoothDeviceStateAsync(mac, d => !d.Connected, 5000);
                }

                await TryBluetoothCtlAsync(5000, "power", "off");
                try { await RunProcessAsync("rfkill", new List<string> { "block", "bluetooth" }, 3000); } catch { }

                if (!await WaitForBluetoothRadioStateAsync(false, 8000))
                    throw new Exception("Bluetooth did not turn OFF. Check permissions or bluetooth service state.");

                _bluetoothDevices = new List<BluetoothDeviceInfo>();
                _bluetoothAllDevices = new List<BluetoothDeviceInfo>();
                SetAudioInputMessage("Bluetooth is OFF.");
                SetBluetoothActionStatus("power-off", "Bluetooth is OFF", false, true);
            }
        }
        catch (Exception ex)
        {
            SetBluetoothActionStatus(on ? "power-on" : "power-off", ShortError(ex.Message), false, false);
            throw;
        }
        finally
        {
            _lastBluetoothRefreshUtc = DateTime.MinValue;
            try { EnsureBluetoothDevicesLoaded(force: true); } catch { }
        }
    }

    private static void EnsureBluetoothDevicesLoaded(bool force)
    {
        if (!force && _bluetoothDevices.Count > 0 && (DateTime.UtcNow - _lastBluetoothRefreshUtc).TotalSeconds < 20)
            return;

        _lastBluetoothRefreshUtc = DateTime.UtcNow;
        try
        {
            var output = RunProcessText("bluetoothctl", new[] { "devices" }, 2500);
            var devices = new List<BluetoothDeviceInfo>();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                AddBluetoothDeviceFromLine(devices, line);

            foreach (var item in _bluetoothScanNames)
            {
                if (devices.Any(d => d.Mac.Equals(item.Key, StringComparison.OrdinalIgnoreCase)))
                    continue;
                devices.Add(ReadBluetoothInfo(item.Key, item.Value));
            }

            _bluetoothAllDevices = SortBluetoothDevices(devices
                .GroupBy(d => d.Mac)
                .Select(g => MergeBluetoothDeviceGroup(g))
                .ToList());

            _bluetoothDevices = _bluetoothAllDevices
                .Where(ShouldShowBluetoothDevice)
                .ToList();

            if (_bluetoothDevices.Count == 0)
                _bluetoothDeviceIndex = 0;
            else
                _bluetoothDeviceIndex = Math.Clamp(_bluetoothDeviceIndex, 0, _bluetoothDevices.Count - 1);
        }
        catch (Exception ex)
        {
            _bluetoothDevices = new List<BluetoothDeviceInfo>();
            _bluetoothAllDevices = new List<BluetoothDeviceInfo>();
            SetNetworkStatus("Bluetooth: " + ShortError(ex.Message));
        }
    }

    private static BluetoothDeviceInfo ReadBluetoothInfo(string mac, string fallbackName)
    {
        try
        {
            var info = RunProcessText("bluetoothctl", new[] { "info", mac }, 2200);
            return BuildBluetoothDeviceInfo(mac, fallbackName, info);
        }
        catch
        {
            var hasName = IsUsefulBluetoothName(fallbackName, mac);
            return new BluetoothDeviceInfo(
                mac,
                hasName ? fallbackName.Trim() : UnknownBluetoothLabel(mac),
                false,
                false,
                false,
                hasName,
                false,
                IsLikelyRandomBluetoothAddress(mac),
                "",
                "");
        }
    }

    private static string MatchBluetoothInfo(string info, string key)
    {
        var match = Regex.Match(info, @"^\s*" + Regex.Escape(key) + @":\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : "";
    }

    private static bool BoolBluetoothInfo(string info, string key)
    {
        return string.Equals(MatchBluetoothInfo(info, key), "yes", StringComparison.OrdinalIgnoreCase);
    }


    private static object BluetoothDeviceApiView(BluetoothDeviceInfo d)
    {
        return new
        {
            d.Mac,
            d.Name,
            DisplayName = d.Name,
            d.Paired,
            d.Trusted,
            d.Connected,
            d.HasName,
            d.IsLikelyAudio,
            d.IsRandomAddress,
            d.Icon,
            d.Uuids
        };
    }

    private static void AddBluetoothDeviceFromLine(List<BluetoothDeviceInfo> devices, string line)
    {
        var match = Regex.Match(line.Trim(), @"Device\s+([0-9A-Fa-f:]{17})\s+(.+)$");
        if (!match.Success)
            return;

        var mac = match.Groups[1].Value.ToUpperInvariant();
        var name = CleanBluetoothDiscoveryName(match.Groups[2].Value);
        devices.Add(ReadBluetoothInfo(mac, name));
    }

    private static string CleanBluetoothDiscoveryName(string name)
    {
        name = (name ?? "").Trim();
        foreach (var prefix in new[] { "Name:", "Alias:", "Name", "Alias" })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return name[prefix.Length..].Trim();
        }

        if (Regex.IsMatch(name, @"^(RSSI|TxPower|ManufacturerData|ServiceData|UUIDs|ServicesResolved|Connected|Paired|Trusted):", RegexOptions.IgnoreCase))
            return "";

        return name;
    }

    private static BluetoothDeviceInfo MergeBluetoothDeviceGroup(IGrouping<string, BluetoothDeviceInfo> group)
    {
        var items = group.ToList();
        var best = items
            .OrderByDescending(d => d.Connected)
            .ThenByDescending(d => d.Paired)
            .ThenByDescending(d => d.IsLikelyAudio)
            .ThenByDescending(d => d.HasName)
            .ThenBy(d => d.Name)
            .First();

        return best with
        {
            Paired = items.Any(d => d.Paired),
            Trusted = items.Any(d => d.Trusted),
            Connected = items.Any(d => d.Connected),
            IsLikelyAudio = items.Any(d => d.IsLikelyAudio),
            HasName = items.Any(d => d.HasName)
        };
    }

    private static List<BluetoothDeviceInfo> SortBluetoothDevices(IEnumerable<BluetoothDeviceInfo> devices)
    {
        return devices
            .OrderByDescending(d => d.Connected)
            .ThenByDescending(d => d.Paired)
            .ThenByDescending(d => d.IsLikelyAudio)
            .ThenByDescending(d => d.HasName)
            .ThenBy(d => d.Name)
            .ThenBy(d => d.Mac)
            .ToList();
    }

    private static bool ShouldShowBluetoothDevice(BluetoothDeviceInfo d)
    {
        if (d.Connected || d.Paired || d.Trusted || d.IsLikelyAudio)
            return true;
        return d.HasName;
    }

    private static BluetoothDeviceInfo BuildBluetoothDeviceInfo(string mac, string fallbackName, string info)
    {
        mac = NormalizeBluetoothMac(mac);
        var nameFromInfo = MatchBluetoothInfo(info, "Name");
        var alias = MatchBluetoothInfo(info, "Alias");
        var icon = MatchBluetoothInfo(info, "Icon");
        var uuids = string.Join(" | ", Regex.Matches(info, @"^\s*UUID:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase).Cast<Match>().Select(m => m.Groups[1].Value.Trim()));
        var chosen = FirstUsefulBluetoothName(mac, nameFromInfo, alias, fallbackName);
        var hasName = IsUsefulBluetoothName(chosen, mac);
        var audio = LooksLikeBluetoothAudioDevice(icon, uuids, chosen);

        return new BluetoothDeviceInfo(
            mac,
            hasName ? chosen.Trim() : UnknownBluetoothLabel(mac),
            BoolBluetoothInfo(info, "Paired"),
            BoolBluetoothInfo(info, "Trusted"),
            BoolBluetoothInfo(info, "Connected"),
            hasName,
            audio,
            IsLikelyRandomBluetoothAddress(mac),
            icon,
            uuids);
    }

    private static string FirstUsefulBluetoothName(string mac, params string[] names)
    {
        foreach (var name in names)
        {
            if (IsUsefulBluetoothName(name, mac))
                return name.Trim();
        }
        return "";
    }

    private static bool IsUsefulBluetoothName(string name, string mac)
    {
        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var normalizedName = Regex.Replace(name, @"[^0-9A-Fa-f]", "").ToUpperInvariant();
        var normalizedMac = Regex.Replace(mac ?? "", @"[^0-9A-Fa-f]", "").ToUpperInvariant();
        if (normalizedName.Length == 12 && normalizedName == normalizedMac)
            return false;
        if (Regex.IsMatch(name, @"^[0-9A-Fa-f]{2}([-:][0-9A-Fa-f]{2}){5}$"))
            return false;

        var lower = name.ToLowerInvariant();
        if (lower is "unknown" or "(unknown)" or "unnamed" or "n/a" or "null")
            return false;
        if (lower.StartsWith("unknown bt", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static string UnknownBluetoothLabel(string mac)
    {
        var shortMac = (mac ?? "").Trim();
        if (shortMac.Length >= 5)
            shortMac = shortMac[^5..];
        return "Unknown BT " + shortMac;
    }

    private static bool LooksLikeBluetoothAudioDevice(string icon, string uuids, string name)
    {
        var text = ((icon ?? "") + " " + (uuids ?? "") + " " + (name ?? "")).ToLowerInvariant();
        return text.Contains("audio")
            || text.Contains("headset")
            || text.Contains("headphone")
            || text.Contains("handsfree")
            || text.Contains("hands-free")
            || text.Contains("speaker")
            || text.Contains("earbud")
            || text.Contains("earbuds")
            || text.Contains("a2dp")
            || text.Contains("00001108")
            || text.Contains("0000110a")
            || text.Contains("0000110b")
            || text.Contains("0000110d")
            || text.Contains("0000111e");
    }

    private static bool IsLikelyRandomBluetoothAddress(string mac)
    {
        mac = NormalizeBluetoothMac(mac);
        if (string.IsNullOrWhiteSpace(mac))
            return false;

        var firstByteText = mac.Split(':')[0];
        if (!int.TryParse(firstByteText, System.Globalization.NumberStyles.HexNumber, null, out var firstByte))
            return false;

        // LE random private addresses often have the two most significant bits set to 00 or 01.
        // We only use this as a weak signal and still keep paired/connected/audio devices.
        var twoMsb = (firstByte & 0xC0) >> 6;
        return twoMsb is 0 or 1;
    }

    private static BluetoothDeviceInfo? SelectedBluetoothDevice()
    {
        EnsureBluetoothDevicesLoaded(force: false);
        if (_bluetoothDevices.Count == 0)
            return null;
        _bluetoothDeviceIndex = Math.Clamp(_bluetoothDeviceIndex, 0, _bluetoothDevices.Count - 1);
        return _bluetoothDevices[_bluetoothDeviceIndex];
    }

    private static string SelectedBluetoothLabel()
    {
        var device = SelectedBluetoothDevice();
        if (device is null)
            return "NONE";
        var label = string.IsNullOrWhiteSpace(device.Name) ? device.Mac : device.Name;
        return label.Length > 18 ? label[..18] : label;
    }

    private static string BluetoothDeviceFriendlyName(string mac)
    {
        mac = NormalizeBluetoothMac(mac);
        if (string.IsNullOrWhiteSpace(mac))
            return "device";

        try
        {
            var cached = _bluetoothAllDevices.FirstOrDefault(d => d.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase))
                ?? _bluetoothDevices.FirstOrDefault(d => d.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase));
            if (cached is not null && !string.IsNullOrWhiteSpace(cached.Name))
                return cached.Name;
        }
        catch { }

        if (_bluetoothScanNames.TryGetValue(mac, out var scanName) && IsUsefulBluetoothName(scanName, mac))
            return scanName;

        try
        {
            var info = ReadBluetoothInfo(mac, "");
            if (!string.IsNullOrWhiteSpace(info.Name))
                return info.Name;
        }
        catch { }

        return mac;
    }

    private static bool IsBluetoothScanActive()
    {
        lock (_bluetoothScanLock)
        {
            if (!_bluetoothScanActive)
                return false;
            if (_bluetoothScanTask is not null && _bluetoothScanTask.IsCompleted)
            {
                _bluetoothScanActive = false;
                return false;
            }
            return true;
        }
    }

    private static async Task StartBluetoothScanAsync(int seconds = 120)
    {
        seconds = Math.Clamp(seconds, 30, 120);

        lock (_bluetoothScanLock)
        {
            if (_bluetoothScanActive)
            {
                _bluetoothLastScanLog = $"Already scanning; {Math.Max(0, (int)Math.Ceiling((_bluetoothScanEndsUtc - DateTime.UtcNow).TotalSeconds))}s left";
                return;
            }
        }

        await SetBluetoothRadioAsync(true);
        try { await RunProcessAsync("bluetoothctl", new List<string> { "agent", "on" }, 3000); } catch { }
        try { await RunProcessAsync("bluetoothctl", new List<string> { "default-agent" }, 3000); } catch { }

        // Drop stale unnamed advertisements before each new scan. Named entries are kept,
        // because some headsets only expose their real name once and then show only RSSI/UUID changes.
        foreach (var item in _bluetoothScanNames.ToArray())
        {
            if (!IsUsefulBluetoothName(item.Value, item.Key))
                _bluetoothScanNames.Remove(item.Key);
        }

        var cts = new CancellationTokenSource();
        var started = DateTime.UtcNow;
        lock (_bluetoothScanLock)
        {
            _bluetoothScanCts = cts;
            _bluetoothScanStartedUtc = started;
            _bluetoothScanEndsUtc = started.AddSeconds(seconds);
            _bluetoothScanActive = true;
            _bluetoothLastScanLog = $"Scanning Bluetooth for up to {seconds}s...";
        }

        _bluetoothScanTask = Task.Run(async () =>
        {
            var cancelled = false;
            var output = "";
            try
            {
                output = await RunBluetoothDiscoveryScanAsync(seconds, cts.Token);
                cancelled = cts.IsCancellationRequested;
                RememberBluetoothScanOutput(output);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            catch (Exception ex)
            {
                _bluetoothLastScanLog = "Bluetooth scan error: " + ShortError(ex.Message);
                Console.WriteLine("[BT SCAN] " + ex);
            }
            finally
            {
                try { await RunProcessAsync("bluetoothctl", new List<string> { "scan", "off" }, 2000); } catch { }
                _lastBluetoothScanUtc = DateTime.UtcNow;
                _lastBluetoothRefreshUtc = DateTime.MinValue;
                EnsureBluetoothDevicesLoaded(force: true);

                var visible = _bluetoothDevices.Count;
                var hidden = Math.Max(0, _bluetoothAllDevices.Count - visible);
                var elapsed = Math.Max(1, (int)Math.Round((DateTime.UtcNow - started).TotalSeconds));
                lock (_bluetoothScanLock)
                {
                    if (ReferenceEquals(_bluetoothScanCts, cts))
                    {
                        _bluetoothScanActive = false;
                        _bluetoothScanCts = null;
                        _bluetoothScanProcess = null;
                        _bluetoothScanEndsUtc = DateTime.MinValue;
                    }
                }

                _bluetoothLastScanLog = cancelled
                    ? $"Scan cancelled after {elapsed}s; visible: {visible}; hidden unnamed: {hidden}; total: {_bluetoothAllDevices.Count}"
                    : $"Scan finished after {elapsed}s; visible: {visible}; hidden unnamed: {hidden}; total: {_bluetoothAllDevices.Count}";
            }
        });
    }

    private static async Task CancelBluetoothScanAsync()
    {
        CancellationTokenSource? cts;
        Process? process;
        lock (_bluetoothScanLock)
        {
            cts = _bluetoothScanCts;
            process = _bluetoothScanProcess;
            if (!_bluetoothScanActive && cts is null && process is null)
                return;
            _bluetoothLastScanLog = "Cancelling Bluetooth scan...";
            _bluetoothScanActive = false;
            _bluetoothScanEndsUtc = DateTime.MinValue;
        }

        try { cts?.Cancel(); } catch { }
        try
        {
            if (process is not null && !process.HasExited)
            {
                try { await process.StandardInput.WriteLineAsync("scan off"); await process.StandardInput.WriteLineAsync("quit"); await process.StandardInput.FlushAsync(); } catch { }
                await Task.Delay(250);
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
        }
        catch { }

        try { await RunProcessAsync("bluetoothctl", new List<string> { "scan", "off" }, 2000); } catch { }
        _lastBluetoothRefreshUtc = DateTime.MinValue;
        EnsureBluetoothDevicesLoaded(force: true);
    }

    private static async Task ScanBluetoothAsync(int seconds = 120)
    {
        await StartBluetoothScanAsync(seconds);
    }

    private static async Task<string> RunBluetoothDiscoveryScanAsync(int seconds, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var stopAt = DateTime.UtcNow.AddSeconds(seconds);

        void AppendLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            lock (output) output.AppendLine(line);
            RememberBluetoothScanLine(line);
        }

        try { await RunProcessAsync("bluetoothctl", new List<string> { "scan", "off" }, 2000); } catch { }

        // Some bluetoothctl versions can exit early when called non-interactively.
        // Keep starting sessions until the requested scan window has really elapsed or the user cancels.
        while (DateTime.UtcNow < stopAt && !cancellationToken.IsCancellationRequested)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "bluetoothctl",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += (_, e) => AppendLine(e.Data);
            process.ErrorDataReceived += (_, e) => AppendLine(e.Data);

            try
            {
                process.Start();
                lock (_bluetoothScanLock) _bluetoothScanProcess = process;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.StandardInput.WriteLineAsync("scan on");
                await process.StandardInput.FlushAsync();

                while (!process.HasExited && DateTime.UtcNow < stopAt && !cancellationToken.IsCancellationRequested)
                    await Task.Delay(250, cancellationToken);

                if (!process.HasExited)
                {
                    try
                    {
                        await process.StandardInput.WriteLineAsync("scan off");
                        await process.StandardInput.WriteLineAsync("devices");
                        await process.StandardInput.WriteLineAsync("quit");
                        await process.StandardInput.FlushAsync();
                    }
                    catch { }
                }

                using var waitCts = new CancellationTokenSource(3000);
                try { await process.WaitForExitAsync(waitCts.Token); }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                }
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                throw;
            }
            catch (Exception ex)
            {
                AppendLine("Bluetooth scan session ended: " + ex.Message);
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            }
            finally
            {
                lock (_bluetoothScanLock)
                {
                    if (ReferenceEquals(_bluetoothScanProcess, process))
                        _bluetoothScanProcess = null;
                }
            }

            if (DateTime.UtcNow < stopAt && !cancellationToken.IsCancellationRequested)
                await Task.Delay(350, cancellationToken);
        }

        try { await RunProcessAsync("bluetoothctl", new List<string> { "scan", "off" }, 2000); } catch { }

        lock (output) return output.ToString();
    }

    private static void RememberBluetoothScanOutput(string output)
    {
        foreach (var line in (output ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            RememberBluetoothScanLine(line);
    }

    private static void RememberBluetoothScanLine(string line)
    {
        var match = Regex.Match((line ?? "").Trim(), @"Device\s+([0-9A-Fa-f:]{17})\s+(.+)$");
        if (!match.Success)
            return;

        var mac = match.Groups[1].Value.ToUpperInvariant();
        var name = CleanBluetoothDiscoveryName(match.Groups[2].Value);
        if (IsUsefulBluetoothName(name, mac) || !_bluetoothScanNames.ContainsKey(mac))
            _bluetoothScanNames[mac] = name;

        _lastBluetoothRefreshUtc = DateTime.MinValue;
    }

    private static async Task PairBluetoothAsync(string mac)
    {
        await CancelBluetoothScanAsync();
        mac = NormalizeBluetoothMac(mac);
        if (string.IsNullOrWhiteSpace(mac))
            throw new ArgumentException("Bluetooth MAC is empty");

        SetBluetoothActionStatus("pair", "Pairing " + BluetoothDeviceFriendlyName(mac) + "...", true);
        try
        {
            await SetBluetoothRadioAsync(true);
            SetBluetoothActionStatus("pair", "Pairing " + BluetoothDeviceFriendlyName(mac) + "...", true);
            await EnsureBluetoothAgentAsync();

            var alreadyPaired = false;
            try { alreadyPaired = ReadBluetoothInfo(mac, _bluetoothScanNames.TryGetValue(mac, out var knownName) ? knownName : "").Paired; } catch { }

            if (!alreadyPaired)
            {
                await TryBluetoothCtlAsync(45000, "pair", mac);
                if (!await WaitForBluetoothDeviceStateAsync(mac, d => d.Paired, 18000))
                    throw new Exception("Pairing was not confirmed. Keep the headset in pairing mode and try again.");
            }

            await TryBluetoothCtlAsync(8000, "trust", mac);
            await WaitForBluetoothDeviceStateAsync(mac, d => d.Trusted || d.Paired, 5000);

            SetBluetoothActionStatus("pair", "Pairing OK, connecting " + BluetoothDeviceFriendlyName(mac) + "...", true);
            await TryBluetoothCtlAsync(22000, "connect", mac);
            if (!await WaitForBluetoothDeviceStateAsync(mac, d => d.Connected, 18000))
                throw new Exception("Paired, but not connected. Put the headset in pairing mode and press Connect.");

            try { await FixBluetoothInputAsync(mac); } catch { }
            _lastBluetoothRefreshUtc = DateTime.MinValue;
            EnsureBluetoothDevicesLoaded(force: true);
            SetBluetoothActionStatus("pair", "Paired and connected: " + BluetoothDeviceFriendlyName(mac), false, true);
        }
        catch (Exception ex)
        {
            _lastBluetoothRefreshUtc = DateTime.MinValue;
            try { EnsureBluetoothDevicesLoaded(force: true); } catch { }
            SetBluetoothActionStatus("pair", ShortError(ex.Message), false, false);
            throw;
        }
    }

    private static async Task ConnectBluetoothAsync(string mac)
    {
        await CancelBluetoothScanAsync();
        mac = NormalizeBluetoothMac(mac);
        if (string.IsNullOrWhiteSpace(mac))
            throw new ArgumentException("Bluetooth MAC is empty");

        SetBluetoothActionStatus("connect", "Connecting " + BluetoothDeviceFriendlyName(mac) + "...", true);
        try
        {
            await SetBluetoothRadioAsync(true);
            SetBluetoothActionStatus("connect", "Connecting " + BluetoothDeviceFriendlyName(mac) + "...", true);
            await EnsureBluetoothAgentAsync();
            await TryBluetoothCtlAsync(8000, "trust", mac);
            await TryBluetoothCtlAsync(22000, "connect", mac);

            if (!await WaitForBluetoothDeviceStateAsync(mac, d => d.Connected, 18000))
                throw new Exception("Bluetooth did not connect. Put the headset in pairing mode and try again.");

            try { await FixBluetoothInputAsync(mac); } catch { }
            _lastBluetoothRefreshUtc = DateTime.MinValue;
            EnsureBluetoothDevicesLoaded(force: true);
            SetBluetoothActionStatus("connect", "Connected: " + BluetoothDeviceFriendlyName(mac), false, true);
        }
        catch (Exception ex)
        {
            _lastBluetoothRefreshUtc = DateTime.MinValue;
            try { EnsureBluetoothDevicesLoaded(force: true); } catch { }
            SetBluetoothActionStatus("connect", ShortError(ex.Message), false, false);
            throw;
        }
    }

    private static async Task DisconnectBluetoothAsync(string mac)
    {
        await CancelBluetoothScanAsync();
        mac = NormalizeBluetoothMac(mac);
        if (string.IsNullOrWhiteSpace(mac))
            throw new ArgumentException("Bluetooth MAC is empty");

        SetBluetoothActionStatus("disconnect", "Disconnecting " + BluetoothDeviceFriendlyName(mac) + "...", true);
        try
        {
            SetPulseBluetoothCardsOff();
            await TryBluetoothCtlAsync(9000, "disconnect", mac);
            if (!await WaitForBluetoothDeviceStateAsync(mac, d => !d.Connected, 8000))
            {
                await TryBluetoothCtlAsync(9000, "disconnect", mac);
                if (!await WaitForBluetoothDeviceStateAsync(mac, d => !d.Connected, 8000))
                    throw new Exception("Device is still connected. Use Bluetooth Off to force all devices off.");
            }

            SetAudioInputMessage("Bluetooth disconnected.");
            _lastBluetoothRefreshUtc = DateTime.MinValue;
            EnsureBluetoothDevicesLoaded(force: true);
            SetBluetoothActionStatus("disconnect", "Disconnected: " + BluetoothDeviceFriendlyName(mac), false, true);
        }
        catch (Exception ex)
        {
            _lastBluetoothRefreshUtc = DateTime.MinValue;
            try { EnsureBluetoothDevicesLoaded(force: true); } catch { }
            SetBluetoothActionStatus("disconnect", ShortError(ex.Message), false, false);
            throw;
        }
    }

    private static async Task RemoveBluetoothAsync(string mac)
    {
        await CancelBluetoothScanAsync();
        mac = NormalizeBluetoothMac(mac);
        if (string.IsNullOrWhiteSpace(mac))
            throw new ArgumentException("Bluetooth MAC is empty");

        SetBluetoothActionStatus("remove", "Forgetting " + BluetoothDeviceFriendlyName(mac) + "...", true);
        try
        {
            try { await DisconnectBluetoothAsync(mac); } catch { }
            await TryBluetoothCtlAsync(10000, "remove", mac);
            await WaitForBluetoothDeviceStateAsync(mac, d => !d.Paired && !d.Connected, 7000);
            _bluetoothScanNames.Remove(mac);
            _lastBluetoothRefreshUtc = DateTime.MinValue;
            EnsureBluetoothDevicesLoaded(force: true);
            SetBluetoothActionStatus("remove", "Forgot device", false, true);
        }
        catch (Exception ex)
        {
            _lastBluetoothRefreshUtc = DateTime.MinValue;
            try { EnsureBluetoothDevicesLoaded(force: true); } catch { }
            SetBluetoothActionStatus("remove", ShortError(ex.Message), false, false);
            throw;
        }
    }

    private static string NormalizeBluetoothMac(string mac)
    {
        mac = (mac ?? "").Trim().ToUpperInvariant();
        return Regex.IsMatch(mac, @"^[0-9A-F]{2}(:[0-9A-F]{2}){5}$") ? mac : "";
    }

    private static bool TryEnableBluetoothInputProfile(string? preferredMac, out string message)
    {
        message = "";
        try
        {
            Thread.Sleep(700);
            var cards = ListPulseBluetoothCards();
            if (cards.Count == 0)
            {
                message = "No PulseAudio/PipeWire Bluetooth card found. Bluetooth may be connected only in BlueZ, not in the audio server.";
                return false;
            }

            preferredMac = NormalizeBluetoothMac(preferredMac ?? "");
            var ordered = cards
                .OrderByDescending(c => !string.IsNullOrWhiteSpace(preferredMac) && c.Mac.Equals(preferredMac, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(c => c.Profiles.Any(p => IsBluetoothInputProfile(p)))
                .ToList();

            var tried = new List<string>();
            foreach (var card in ordered)
            {
                var current = card.Profiles.FirstOrDefault(p => p.Name.Equals(card.ActiveProfile, StringComparison.OrdinalIgnoreCase));
                if (current is not null && IsBluetoothInputProfile(current))
                {
                    message = "Bluetooth microphone profile is already active.";
                    return true;
                }

                var candidates = card.Profiles
                    .Where(IsBluetoothInputProfile)
                    .OrderByDescending(p => p.Available)
                    .ThenByDescending(p => p.Name.Contains("msbc", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(p => p.Name.Contains("handsfree", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("headset", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (candidates.Count == 0)
                {
                    tried.Add($"{card.Name}: no input profile");
                    continue;
                }

                foreach (var profile in candidates)
                {
                    try
                    {
                        RunPulseText(new[] { "set-card-profile", card.Name, profile.Name }, 3500);
                        Thread.Sleep(1400);
                        if (ListPulseSources().Any(s => s.Kind == "bluetooth"))
                        {
                            message = "Bluetooth input profile enabled: " + profile.Name;
                            return true;
                        }

                        tried.Add($"{card.Name}: {profile.Name} set, but source not visible");
                    }
                    catch (Exception ex)
                    {
                        tried.Add($"{card.Name}: {profile.Name} failed ({ShortError(ex.Message)})");
                    }
                }
            }

            message = tried.Count > 0
                ? "Could not enable Bluetooth microphone. " + string.Join("; ", tried.Take(3))
                : "No Bluetooth microphone profile found.";
            return false;
        }
        catch (Exception ex)
        {
            message = "Bluetooth input profile failed: " + ShortError(ex.Message);
            return false;
        }
    }

    private static bool IsBluetoothInputProfile(PulseCardProfile profile)
    {
        if (profile.Sources <= 0)
            return false;
        if (!profile.Available)
            return false;

        var text = (profile.Name + " " + profile.Description).ToLowerInvariant();
        if (text.Contains("off") || text.Contains("a2dp"))
            return false;

        return text.Contains("headset")
            || text.Contains("handsfree")
            || text.Contains("hands-free")
            || text.Contains("hsp")
            || text.Contains("hfp")
            || text.Contains("head-unit")
            || text.Contains("input");
    }

    private static string RunPulseText(string[] args, int timeoutMs)
    {
        return RunAudioServerText("pactl", args, timeoutMs);
    }

    private static List<string> RunPulseTextCandidates(string[] args, int timeoutMs)
    {
        return RunAudioServerTextCandidates("pactl", args, timeoutMs);
    }

    private static string RunPipeWireText(string file, string[] args, int timeoutMs)
    {
        return RunAudioServerText(file, args, timeoutMs);
    }

    private static string RunAudioServerText(string file, string[] args, int timeoutMs)
    {
        Exception? last = null;
        try
        {
            return RunProcessText(file, args, timeoutMs);
        }
        catch (Exception ex)
        {
            last = ex;
        }

        foreach (var env in AudioServerEnvironmentCandidates())
        {
            try
            {
                return RunProcessTextWithEnvironment(file, args, timeoutMs, env);
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        foreach (var runAs in AudioServerRunAsCandidates())
        {
            try
            {
                var commandArgs = new List<string> { "-u", runAs.User, "--", "env" };
                foreach (var item in runAs.Env)
                    commandArgs.Add(item.Key + "=" + item.Value);
                commandArgs.Add(file);
                commandArgs.AddRange(args);
                return RunProcessText("runuser", commandArgs.ToArray(), timeoutMs + 1000);
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw last ?? new Exception(file + " failed");
    }

    private static List<string> RunAudioServerTextCandidates(string file, string[] args, int timeoutMs)
    {
        var outputs = new List<string>();
        void AddOutput(string output)
        {
            if (!string.IsNullOrWhiteSpace(output) && !outputs.Contains(output))
                outputs.Add(output);
        }

        try { AddOutput(RunProcessText(file, args, timeoutMs)); } catch { }

        foreach (var env in AudioServerEnvironmentCandidates())
        {
            try { AddOutput(RunProcessTextWithEnvironment(file, args, timeoutMs, env)); } catch { }
        }

        foreach (var runAs in AudioServerRunAsCandidates())
        {
            try
            {
                var commandArgs = new List<string> { "-u", runAs.User, "--", "env" };
                foreach (var item in runAs.Env)
                    commandArgs.Add(item.Key + "=" + item.Value);
                commandArgs.Add(file);
                commandArgs.AddRange(args);
                AddOutput(RunProcessText("runuser", commandArgs.ToArray(), timeoutMs + 1000));
            }
            catch { }
        }

        return outputs;
    }

    private static string RunProcessTextWithEnvironment(string file, string[] args, int timeoutMs, Dictionary<string, string> env)
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

        foreach (var item in env)
            process.StartInfo.Environment[item.Key] = item.Value;
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        process.Start();
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException(file + " timeout");
        }

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (process.ExitCode != 0)
            throw new Exception(string.IsNullOrWhiteSpace(error) ? output : error);
        return output;
    }

    private static List<Dictionary<string, string>> AudioServerEnvironmentCandidates()
    {
        var result = new List<Dictionary<string, string>>();
        foreach (var runtime in AudioRuntimeDirCandidates())
        {
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["XDG_RUNTIME_DIR"] = runtime
            };

            var pulse = Path.Combine(runtime, "pulse", "native");
            if (File.Exists(pulse))
                env["PULSE_SERVER"] = "unix:" + pulse;

            result.Add(env);
        }
        return result;
    }

    private static List<(string User, Dictionary<string, string> Env)> AudioServerRunAsCandidates()
    {
        var result = new List<(string User, Dictionary<string, string> Env)>();
        if (!IsRunningAsRoot())
            return result;

        foreach (var runtime in AudioRuntimeDirCandidates())
        {
            var uid = Path.GetFileName(runtime.TrimEnd(Path.DirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(uid) || uid == "0")
                continue;

            var user = UserNameForUid(uid);
            if (string.IsNullOrWhiteSpace(user))
                continue;

            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["XDG_RUNTIME_DIR"] = runtime
            };
            var pulse = Path.Combine(runtime, "pulse", "native");
            if (File.Exists(pulse))
                env["PULSE_SERVER"] = "unix:" + pulse;

            result.Add((user, env));
        }
        return result;
    }

    private static List<string> AudioRuntimeDirCandidates()
    {
        var dirs = new List<string>();
        void Add(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir))
                return;
            dir = dir.Trim();
            if (!Directory.Exists(dir))
                return;
            if (!dirs.Any(x => x.Equals(dir, StringComparison.OrdinalIgnoreCase)))
                dirs.Add(dir);
        }

        Add(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"));
        try
        {
            var uid = RunProcessText("id", new[] { "-u" }, 1000).Trim();
            if (!string.IsNullOrWhiteSpace(uid))
                Add("/run/user/" + uid);
        }
        catch { }

        Add("/run/user/1000");
        Add("/run/user/1001");

        try
        {
            if (Directory.Exists("/run/user"))
            {
                foreach (var dir in Directory.GetDirectories("/run/user"))
                    Add(dir);
            }
        }
        catch { }

        return dirs;
    }

    private static bool IsRunningAsRoot()
    {
        try { return RunProcessText("id", new[] { "-u" }, 1000).Trim() == "0"; }
        catch { return string.Equals(Environment.UserName, "root", StringComparison.OrdinalIgnoreCase); }
    }

    private static string UserNameForUid(string uid)
    {
        try
        {
            var passwd = RunProcessText("getent", new[] { "passwd", uid }, 1000).Trim();
            var parts = passwd.Split(':');
            return parts.Length > 0 ? parts[0] : "";
        }
        catch
        {
            return "";
        }
    }

    private static void ApplyAudioServerEnvironment(ProcessStartInfo psi)
    {
        foreach (var env in AudioServerEnvironmentCandidates())
        {
            if (!env.TryGetValue("XDG_RUNTIME_DIR", out var runtime))
                continue;

            var pulse = env.TryGetValue("PULSE_SERVER", out var pulseServer) ? pulseServer : "";
            if (!string.IsNullOrWhiteSpace(pulse) || File.Exists(Path.Combine(runtime, "pipewire-0")))
            {
                foreach (var item in env)
                    psi.Environment[item.Key] = item.Value;
                return;
            }
        }
    }

    private static List<PulseBluetoothCard> ListPulseBluetoothCards()
    {
        var cards = new List<PulseBluetoothCard>();
        foreach (var output in RunPulseTextCandidates(new[] { "list", "cards" }, 3500))
        {
            foreach (var block in Regex.Split(output, @"(?=^Card #)", RegexOptions.Multiline))
            {
                var name = MatchPulseField(block, "Name");
                if (string.IsNullOrWhiteSpace(name) || !name.Contains("bluez", StringComparison.OrdinalIgnoreCase))
                    continue;

                var active = MatchPulseField(block, "Active Profile");
                var profiles = new List<PulseCardProfile>();
                foreach (var line in block.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var m = Regex.Match(line,
                        @"^\s+([^\s:]+):\s*(.*?)\s*\(sinks:\s*(\d+),\s*sources:\s*(\d+),.*?(?:available:\s*([a-zA-Z]+))?.*\)",
                        RegexOptions.IgnoreCase);
                    if (!m.Success)
                        continue;

                    var profileName = m.Groups[1].Value.Trim();
                    var description = m.Groups[2].Value.Trim();
                    var sources = int.TryParse(m.Groups[4].Value, out var parsedSources) ? parsedSources : 0;
                    var availableText = m.Groups[5].Value.Trim();
                    var available = !availableText.Equals("no", StringComparison.OrdinalIgnoreCase);
                    profiles.Add(new PulseCardProfile(profileName, description, sources, available));
                }

                if (!cards.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    cards.Add(new PulseBluetoothCard(name, MacFromPulseBluetoothCardName(name), active, profiles));
            }
        }

        // Fallback for very old pactl output where `list cards` parsing fails.
        if (cards.Count == 0)
        {
            try
            {
                foreach (var shortCards in RunPulseTextCandidates(new[] { "list", "short", "cards" }, 2500))
                {
                    foreach (var line in shortCards.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 2)
                            parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 2)
                            continue;

                        var name = parts[1].Trim();
                        if (!name.Contains("bluez", StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (!cards.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                            cards.Add(new PulseBluetoothCard(name, MacFromPulseBluetoothCardName(name), "", new List<PulseCardProfile>
                            {
                                new("headset-head-unit", "Headset Head Unit", 1, true),
                                new("handsfree-head-unit", "Handsfree Head Unit", 1, true),
                                new("headset-head-unit-msbc", "Headset Head Unit mSBC", 1, true),
                                new("handsfree_head_unit", "Handsfree Head Unit", 1, true),
                                new("headset_head_unit", "Headset Head Unit", 1, true)
                            }));
                    }
                }
            }
            catch
            {
            }
        }

        return cards;
    }

    private static string MacFromPulseBluetoothCardName(string cardName)
    {
        var match = Regex.Match(cardName ?? "", @"bluez_card\.([0-9A-Fa-f_]{17})", RegexOptions.IgnoreCase);
        if (!match.Success)
            return "";
        return NormalizeBluetoothMac(match.Groups[1].Value.Replace('_', ':'));
    }

    private static async Task FixBluetoothInputAsync(string mac = "")
    {
        SetBluetoothActionStatus("audio-input", "Checking Bluetooth microphone input...", true);
        await CancelBluetoothScanAsync();
        await SetBluetoothRadioAsync(true);
        SetBluetoothActionStatus("audio-input", "Checking Bluetooth microphone input...", true);
        mac = NormalizeBluetoothMac(mac);

        EnsureBluetoothDevicesLoaded(force: true);
        var device = !string.IsNullOrWhiteSpace(mac)
            ? _bluetoothAllDevices.FirstOrDefault(d => d.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase))
            : _bluetoothAllDevices.FirstOrDefault(d => d.Connected && d.IsLikelyAudio) ?? _bluetoothAllDevices.FirstOrDefault(d => d.Connected);

        if (device is not null && !device.Connected)
        {
            try { await RunProcessAsync("bluetoothctl", new List<string> { "connect", device.Mac }, 16000); } catch { }
        }

        await Task.Delay(900);
        var targetMac = device?.Mac ?? mac;
        var ok = TryEnableBluetoothInputProfile(targetMac, out var profileMessage);
        TrySetDefaultBluetoothSource();

        AudioCaptureSource? source = null;
        for (var i = 0; i < 6; i++)
        {
            source = FindBluetoothAudioSource();
            if (source is not null)
                break;
            await Task.Delay(1000);
            if (i == 2)
            {
                ok = TryEnableBluetoothInputProfile(targetMac, out profileMessage) || ok;
                TrySetDefaultBluetoothSource();
            }
        }

        if (source is not null)
        {
            SetAudioInputMessage("Bluetooth microphone ready: " + source.Label);
            SetBluetoothActionStatus("audio-input", "Bluetooth microphone ready: " + source.Label, false, true);
        }
        else if (!string.IsNullOrWhiteSpace(profileMessage))
        {
            SetAudioInputMessage(profileMessage + ". If this is a headset, make sure the Raspberry Pi audio stack supports HSP/HFP microphone profiles.");
            SetBluetoothActionStatus("audio-input", profileMessage, false, false);
        }
        else
        {
            SetAudioInputMessage("Bluetooth is connected, but no microphone input appeared.");
            SetBluetoothActionStatus("audio-input", "Bluetooth is connected, but no microphone input appeared.", false, false);
        }

        _lastBluetoothRefreshUtc = DateTime.MinValue;
        EnsureBluetoothDevicesLoaded(force: true);
    }

    private static void TrySetDefaultBluetoothSource()
    {
        try
        {
            Thread.Sleep(1200);
            var source = FindBluetoothAudioSource();
            if (source is null)
                return;
            RunPulseText(new[] { "set-default-source", source.Device }, 2500);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[AUDIO] set default BT source skipped: " + ex.Message);
        }
    }

    private static string ReadBluetoothRequestMac(JsonElement root)
    {
        if (TryGetString(root, "mac", out var mac)) return mac;
        if (TryGetString(root, "address", out var address)) return address;
        return "";
    }
}
