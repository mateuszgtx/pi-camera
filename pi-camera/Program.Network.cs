using System.Diagnostics;
using pi_camera.Services;

namespace pi_camera;

public static partial class Program
{
    private static int NetworkPageCount() => 3;

    private static int NetworkRowCount() => _networkPage == 0 ? 6 : _networkPage == 1 ? 5 : 7;

    private static void HandleNetworkPrimaryButton(int dir, FramebufferDisplay display, int width, int height)
    {
        EnsureWifiConnectionsLoaded(force: false);
        if (_networkPage == 2)
            EnsureBluetoothDevicesLoaded(force: false);

        var lastRow = NetworkRowCount() - 1;
        if (_networkRow == lastRow)
        {
            _networkPage = (_networkPage + (dir >= 0 ? 1 : -1) + NetworkPageCount()) % NetworkPageCount();
            _networkRow = 0;
            DrawNetwork(display, width, height);
            return;
        }

        if (_networkPage == 0)
        {
            if (_networkRow == 0)
            {
                var turnOn = !IsHotspotActive();
                QueueNetworkAction(turnOn ? "Enabling hotspot" : "Disabling hotspot", async () => await SetHotspotAsync(turnOn), display, width, height);
                return;
            }

            if (_networkRow == 1)
            {
                var turnOn = !IsWifiRadioOn();
                QueueNetworkAction(turnOn ? "Enabling WiFi" : "Disabling WiFi", async () => await SetWifiRadioAsync(turnOn), display, width, height);
                return;
            }

            if (_networkRow == 2)
            {
                EnsureWifiConnectionsLoaded(force: true);
                if (_savedWifiConnections.Count > 0)
                    _wifiConnectionIndex = (_wifiConnectionIndex + dir + _savedWifiConnections.Count) % _savedWifiConnections.Count;
                DrawNetwork(display, width, height);
                return;
            }

            if (_networkRow == 3)
            {
                var name = SelectedWifiName();
                if (string.IsNullOrWhiteSpace(name))
                {
                    SetNetworkStatus("No saved networks");
                    DrawNetwork(display, width, height);
                    return;
                }

                QueueNetworkAction("Connecting: " + name, async () => await ConnectSavedWifiAsync(name), display, width, height);
                return;
            }

            if (_networkRow == 4)
            {
                EnsureWifiConnectionsLoaded(force: true);
                SetNetworkStatus("Networks refreshed");
                DrawNetwork(display, width, height);
                return;
            }
        }
        else if (_networkPage == 1)
        {
            if (_networkRow == 0 || _networkRow == 1 || _networkRow == 2 || _networkRow == 3)
            {
                EnsureWifiConnectionsLoaded(force: true);
                _batteryLastReadUtc = DateTime.MinValue;
                SetNetworkStatus("Status refreshed");
                DrawNetwork(display, width, height);
                return;
            }
        }
        else
        {
            if (_networkRow == 0)
            {
                _audioInputMode = NextAudioInputMode(_audioInputMode, dir);
                SetNetworkStatus("Audio: " + _audioInputMode);
                DrawNetwork(display, width, height);
                return;
            }

            if (_networkRow == 1)
            {
                if ((_audioInputMode is AudioInputMode.Auto or AudioInputMode.Bluetooth) && HasConnectedBluetoothAudioDevice() && ResolveAudioCaptureSource() is null)
                    QueueNetworkAction("Fixing BT input", async () => await FixBluetoothInputAsync(), display, width, height);
                else
                {
                    SetNetworkStatus("Audio source refreshed");
                    DrawNetwork(display, width, height);
                }
                return;
            }

            if (_networkRow == 2)
            {
                var turnOn = !IsBluetoothRadioOn();
                QueueNetworkAction(turnOn ? "Enabling Bluetooth" : "Disabling Bluetooth", async () => await SetBluetoothRadioAsync(turnOn), display, width, height);
                return;
            }

            if (_networkRow == 3)
            {
                EnsureBluetoothDevicesLoaded(force: true);
                if (_bluetoothDevices.Count > 0)
                    _bluetoothDeviceIndex = (_bluetoothDeviceIndex + dir + _bluetoothDevices.Count) % _bluetoothDevices.Count;
                DrawNetwork(display, width, height);
                return;
            }

            if (_networkRow == 4)
            {
                if (IsBluetoothScanActive())
                    QueueNetworkAction("Cancelling BT scan", async () => await CancelBluetoothScanAsync(), display, width, height);
                else
                    QueueNetworkAction("Starting BT scan", async () => await StartBluetoothScanAsync(120), display, width, height);
                return;
            }

            if (_networkRow == 5)
            {
                var device = SelectedBluetoothDevice();
                if (device is null)
                {
                    SetNetworkStatus("No Bluetooth devices");
                    DrawNetwork(display, width, height);
                    return;
                }

                if (device.Connected)
                    QueueNetworkAction("Disconnecting Bluetooth", async () => await DisconnectBluetoothAsync(device.Mac), display, width, height);
                else
                    QueueNetworkAction("Connecting Bluetooth", async () => await PairBluetoothAsync(device.Mac), display, width, height);
                return;
            }
        }
    }

    private static void QueueNetworkAction(string busyText, Func<Task> action, FramebufferDisplay display, int width, int height)
    {
        SetNetworkStatus(busyText + "...");
        DrawNetwork(display, width, height);

        _ = Task.Run(async () =>
        {
            try
            {
                await action();
                EnsureWifiConnectionsLoaded(force: true);
                var btMessage = (busyText.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) || busyText.Contains("BT", StringComparison.OrdinalIgnoreCase))
                    ? BluetoothActionMessage()
                    : "";
                SetNetworkStatus(string.IsNullOrWhiteSpace(btMessage) ? "OK: " + busyText : btMessage);
            }
            catch (Exception ex)
            {
                SetNetworkStatus("Error: " + ShortError(ex.Message));
                Console.WriteLine("[NETWORK] " + ex);
            }

            try
            {
                if (_tab == Tab.Network)
                    DrawNetwork(display, width, height);
            }
            catch { }
        });
    }

    private static void SetNetworkStatus(string text)
    {
        _networkStatus = text;
        _networkStatusUntilUtc = DateTime.UtcNow.AddSeconds(6);
    }

    private static string ShortError(string text)
    {
        text = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return text.Length > 60 ? text[..60] : text;
    }

    private static void EnsureWifiConnectionsLoaded(bool force)
    {
        if (!force && _savedWifiConnections.Count > 0 && (DateTime.UtcNow - _lastNetworkRefreshUtc).TotalSeconds < 20)
            return;

        _lastNetworkRefreshUtc = DateTime.UtcNow;
        try
        {
            var output = RunProcessText("nmcli", new[] { "-t", "-f", "NAME,TYPE", "connection", "show" }, 2500);
            var list = new List<string>();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(':', 2);
                if (parts.Length != 2) continue;

                var name = UnescapeNmcliValue(parts[0]).Trim();
                var type = parts[1].Trim().ToLowerInvariant();
                if (type.Contains("wireless") || type.Contains("wifi") || type.Contains("802-11"))
                    list.Add(name);
            }

            _savedWifiConnections = list.Distinct().OrderBy(x => x).ToList();
            if (_savedWifiConnections.Count == 0)
                _wifiConnectionIndex = 0;
            else
                _wifiConnectionIndex = Math.Clamp(_wifiConnectionIndex, 0, _savedWifiConnections.Count - 1);
        }
        catch (Exception ex)
        {
            _savedWifiConnections = new List<string>();
            SetNetworkStatus("nmcli: " + ShortError(ex.Message));
        }
    }

    private static string UnescapeNmcliValue(string value)
    {
        return value.Replace("\\:", ":").Replace("\\\\", "\\");
    }

    private static string SelectedWifiName()
    {
        EnsureWifiConnectionsLoaded(force: false);
        if (_savedWifiConnections.Count == 0)
            return "";
        _wifiConnectionIndex = Math.Clamp(_wifiConnectionIndex, 0, _savedWifiConnections.Count - 1);
        return _savedWifiConnections[_wifiConnectionIndex];
    }

    private static string SelectedWifiLabel()
    {
        var name = SelectedWifiName();
        if (string.IsNullOrWhiteSpace(name))
            return "NONE";
        return name.Length > 18 ? name[..18] : name;
    }

    private static string SelectedWifiActionLabel()
    {
        var name = SelectedWifiName();
        return string.IsNullOrWhiteSpace(name) ? "NONE" : "START";
    }

    private static AudioInputMode NextAudioInputMode(AudioInputMode current, int dir)
    {
        var values = new[] { AudioInputMode.Auto, AudioInputMode.Aux, AudioInputMode.Bluetooth, AudioInputMode.Off };
        var i = Array.IndexOf(values, current);
        if (i < 0) i = 0;
        return values[(i + dir + values.Length) % values.Length];
    }

    private static string AudioSourceLabel()
    {
        try
        {
            var source = ResolveAudioCaptureSource();
            if (source is null)
                return IsAudioEnabled() ? "NO INPUT" : "OFF";
            return source.Label.Length > 18 ? source.Label[..18] : source.Label;
        }
        catch
        {
            return "?";
        }
    }

    private static string BluetoothConnectLabel()
    {
        var device = SelectedBluetoothDevice();
        if (device is null)
            return "NONE";
        return device.Connected ? "DISCONNECT" : device.Paired ? "CONNECT" : "PAIR";
    }

    private static string HotspotStatusLabel()
    {
        try { return IsHotspotActive() ? "ON" : "OFF"; }
        catch { return "?"; }
    }

    private static string WifiRadioStatusLabel()
    {
        try { return IsWifiRadioOn() ? "ON" : "OFF"; }
        catch { return "?"; }
    }

    private static bool IsWifiRadioOn()
    {
        var output = RunProcessText("nmcli", new[] { "radio", "wifi" }, 1500).Trim().ToLowerInvariant();
        return output.Contains("enabled") || output.Contains("włącz") || output == "on";
    }

    private static bool IsHotspotActive()
    {
        var output = RunProcessText("nmcli", new[] { "-t", "-f", "NAME", "connection", "show", "--active" }, 1500);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Any(x => x.Trim().Equals(_hotspotSsid, StringComparison.OrdinalIgnoreCase) ||
                      x.Trim().Equals("Hotspot", StringComparison.OrdinalIgnoreCase) ||
                      x.Trim().Contains("hotspot", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task SetWifiRadioAsync(bool on)
    {
        await RunProcessAsync("nmcli", new List<string> { "radio", "wifi", on ? "on" : "off" }, 5000);
    }

    private static async Task SetHotspotAsync(bool on)
    {
        if (!on)
        {
            try { await RunProcessAsync("nmcli", new List<string> { "connection", "down", _hotspotSsid }, 5000); } catch { }
            try { await RunProcessAsync("nmcli", new List<string> { "connection", "down", "Hotspot" }, 5000); } catch { }
            return;
        }

        await RunProcessAsync("nmcli", new List<string>
        {
            "device", "wifi", "hotspot",
            "ifname", "wlan0",
            "ssid", _hotspotSsid,
            "password", _hotspotPassword
        }, 15000);
    }

    private static int _wifiConnectionTaskActive;

    private static async Task ConnectSavedWifiAsync(string connectionName, string? expectedSsid = null)
    {
        var hotspotWasActive = false;
        try { hotspotWasActive = IsHotspotActive(); } catch { }

        if (!IsWifiRadioOn())
            await SetWifiRadioAsync(true);

        try
        {
            // Disable automatic retries during the hand-over. On failure this prevents
            // NetworkManager from repeatedly taking wlan0 away from the restored hotspot.
            try
            {
                await RunProcessAsync("nmcli", new List<string>
                {
                    "connection", "modify", connectionName, "connection.autoconnect", "no"
                }, 5000);
            }
            catch { }

            // wlan0 cannot reliably scan for client networks while it is running the hotspot.
            if (hotspotWasActive)
            {
                SetNetworkStatus("Stopping hotspot...");
                await SetHotspotAsync(false);
                await Task.Delay(700);
            }

            // Force an actual scan after wlan0 has left AP mode. A failed rescan is not
            // fatal because `connection up` performs its own activation/scan as well.
            if (!string.IsNullOrWhiteSpace(expectedSsid))
            {
                SetNetworkStatus("Scanning WiFi: " + expectedSsid);
                try
                {
                    await RunProcessAsync("nmcli", new List<string>
                    {
                        "device", "wifi", "rescan", "ifname", "wlan0", "ssid", expectedSsid
                    }, 12000);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[WIFI RESCAN] " + ex.Message);
                }
            }

            SetNetworkStatus("Connecting: " + connectionName);
            await RunProcessAsync("nmcli", new List<string>
            {
                "connection", "up", "id", connectionName, "ifname", "wlan0"
            }, 30000);

            // Only enable autoconnect after a confirmed successful activation.
            await RunProcessAsync("nmcli", new List<string>
            {
                "connection", "modify", connectionName, "connection.autoconnect", "yes"
            }, 5000);
        }
        catch
        {
            // If switching away from the setup hotspot failed, restore it so the phone
            // can reconnect and the user can correct the SSID/password.
            if (hotspotWasActive)
            {
                try
                {
                    SetNetworkStatus("WiFi failed - restoring hotspot");
                    await SetHotspotAsync(true);
                }
                catch (Exception restoreEx)
                {
                    Console.WriteLine("[WIFI HOTSPOT RESTORE] " + restoreEx);
                }
            }

            throw;
        }
    }

    private static bool QueueWifiConnection(string connectionName, string? expectedSsid = null)
    {
        if (Interlocked.CompareExchange(ref _wifiConnectionTaskActive, 1, 0) != 0)
            return false;

        SetNetworkStatus("WiFi switch queued: " + connectionName);
        _ = Task.Run(async () =>
        {
            try
            {
                // Give the HTTP response enough time to reach the phone before the
                // hotspot disappears.
                await Task.Delay(1500);
                await ConnectSavedWifiAsync(connectionName, expectedSsid);
                SetNetworkStatus("Connected: " + connectionName);
            }
            catch (Exception ex)
            {
                SetNetworkStatus("WiFi error: " + ShortError(ex.Message));
                Console.WriteLine("[WIFI CONNECT BACKGROUND] " + ex);
            }
            finally
            {
                Interlocked.Exchange(ref _wifiConnectionTaskActive, 0);
                EnsureWifiConnectionsLoaded(force: true);
            }
        });

        return true;
    }

    private static async Task<string> AddOrConnectWifiAsync(string ssid, string password, bool connectNow)
    {
        ssid = ssid.Trim();

        if (string.IsNullOrWhiteSpace(ssid))
            throw new ArgumentException("SSID is empty");

        if (!IsWifiRadioOn())
            await SetWifiRadioAsync(true);

        var connectionName = ssid;

        try
        {
            var existing = RunProcessText("nmcli", new[] { "-t", "-f", "NAME", "connection", "show" }, 2500)
                .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => UnescapeNmcliValue(x).Trim())
                .Any(x => x.Equals(connectionName, StringComparison.OrdinalIgnoreCase));

            if (!existing)
            {
                // Create a profile without requiring the network to be visible. This is
                // essential while wlan0 is still serving the setup hotspot.
                await RunProcessAsync("nmcli", new List<string>
                {
                    "connection", "add",
                    "type", "wifi",
                    "ifname", "wlan0",
                    "con-name", connectionName,
                    "ssid", ssid
                }, 10000);
            }

            await RunProcessAsync("nmcli", new List<string>
            {
                "connection", "modify", connectionName,
                "802-11-wireless.mode", "infrastructure",
                "802-11-wireless.ssid", ssid,
                "connection.autoconnect", "no",
                "ipv4.method", "auto"
            }, 10000);

            if (!string.IsNullOrEmpty(password))
            {
                await RunProcessAsync("nmcli", new List<string>
                {
                    "connection", "modify", connectionName,
                    "wifi-sec.key-mgmt", "wpa-psk",
                    "wifi-sec.psk", password
                }, 10000);
            }

            if (connectNow)
                await ConnectSavedWifiAsync(connectionName, ssid);

            return connectionName;
        }
        finally
        {
            EnsureWifiConnectionsLoaded(force: true);
        }
    }

    private static object CurrentNetworkStatus()
    {
        EnsureWifiConnectionsLoaded(force: true);
        return new
        {
            wifiRadio = WifiRadioStatusLabel(),
            hotspot = HotspotStatusLabel(),
            active = ActiveNetworkLabel(),
            ip = IpLabel(),
            saved = _savedWifiConnections.ToArray(),
            selected = SelectedWifiName(),
            status = _networkStatus,
            audio = CurrentAudioStatus(),
            bluetooth = CurrentBluetoothStatus()
        };
    }

    private static string ActiveNetworkLabel()
    {
        try
        {
            var output = RunProcessText("nmcli", new[] { "-t", "-f", "NAME,TYPE,DEVICE", "connection", "show", "--active" }, 2000);
            var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(line)) return "NONE";
            return line.Length > 22 ? line[..22] : line;
        }
        catch { return "?"; }
    }

    private static string IpLabel()
    {
        try
        {
            var output = RunProcessText("hostname", new[] { "-I" }, 1500).Trim();
            if (string.IsNullOrWhiteSpace(output)) return "NONE";
            return output.Length > 24 ? output[..24] : output;
        }
        catch { return "?"; }
    }

    private static string BatteryStatusText()
    {
        if ((DateTime.UtcNow - _batteryLastReadUtc).TotalSeconds < 8)
            return _batteryText;

        _batteryLastReadUtc = DateTime.UtcNow;
        _batteryText = ReadBatteryStatusText();
        return _batteryText;
    }

    private static string ReadBatteryStatusText()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_batteryFile) && File.Exists(_batteryFile))
            {
                var text = File.ReadAllText(_batteryFile).Trim();
                return NormalizeBatteryText(text);
            }

            var powerDir = new DirectoryInfo("/sys/class/power_supply");
            if (powerDir.Exists)
            {
                foreach (var dir in powerDir.GetDirectories())
                {
                    var cap = Path.Combine(dir.FullName, "capacity");
                    if (File.Exists(cap))
                        return NormalizeBatteryText(File.ReadAllText(cap).Trim());
                }
            }

            if (!string.IsNullOrWhiteSpace(_batteryCommand))
            {
                var text = RunShellText(_batteryCommand, 2000).Trim();
                return NormalizeBatteryText(text);
            }
        }
        catch
        {
            return "BAT ?";
        }

        return "BAT --";
    }

    private static string NormalizeBatteryText(string text)
    {
        text = text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return "";
        if (text.EndsWith("%")) return "BAT " + text;
        if (int.TryParse(text, out var pct)) return "BAT " + Math.Clamp(pct, 0, 100) + "%";
        return text.Length > 10 ? text[..10] : text;
    }

    private static string RunProcessText(string file, string[] args, int timeoutMs)
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

    private static string RunShellText(string command, int timeoutMs)
    {
        return RunProcessText("/bin/sh", new[] { "-c", command }, timeoutMs);
    }
}
