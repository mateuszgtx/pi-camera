using System.Diagnostics;
using pi_camera.Services;

namespace pi_camera;

public static partial class Program
{
    private static int NetworkPageCount() => 2;

    private static int NetworkRowCount() => _networkPage == 0 ? 5 : 4;

    private static void HandleNetworkPrimaryButton(int dir, FramebufferDisplay display, int width, int height)
    {
        EnsureWifiConnectionsLoaded(force: false);

        if (_networkPage == 0)
        {
            if (_networkRow == 0)
            {
                var turnOn = !IsHotspotActive();
                QueueNetworkAction(turnOn ? "Włączam hotspot" : "Wyłączam hotspot", async () => await SetHotspotAsync(turnOn), display, width, height);
                return;
            }

            if (_networkRow == 1)
            {
                var turnOn = !IsWifiRadioOn();
                QueueNetworkAction(turnOn ? "Włączam WiFi" : "Wyłączam WiFi", async () => await SetWifiRadioAsync(turnOn), display, width, height);
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
                    SetNetworkStatus("Brak zapisanych sieci");
                    DrawNetwork(display, width, height);
                    return;
                }

                QueueNetworkAction("Łączę: " + name, async () => await ConnectSavedWifiAsync(name), display, width, height);
                return;
            }

            if (_networkRow == 4)
            {
                EnsureWifiConnectionsLoaded(force: true);
                SetNetworkStatus("Odświeżono sieci");
                DrawNetwork(display, width, height);
                return;
            }
        }
        else
        {
            if (_networkRow == 0 || _networkRow == 1 || _networkRow == 2 || _networkRow == 3)
            {
                EnsureWifiConnectionsLoaded(force: true);
                _batteryLastReadUtc = DateTime.MinValue;
                SetNetworkStatus("Odświeżono status");
                DrawNetwork(display, width, height);
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
                SetNetworkStatus("OK: " + busyText);
            }
            catch (Exception ex)
            {
                SetNetworkStatus("Błąd: " + ShortError(ex.Message));
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
            return "BRAK";
        return name.Length > 18 ? name[..18] : name;
    }

    private static string SelectedWifiActionLabel()
    {
        var name = SelectedWifiName();
        return string.IsNullOrWhiteSpace(name) ? "BRAK" : "START";
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

    private static async Task ConnectSavedWifiAsync(string connectionName)
    {
        await RunProcessAsync("nmcli", new List<string> { "connection", "up", "id", connectionName }, 15000);
    }


    private static async Task AddOrConnectWifiAsync(string ssid, string password, bool connectNow)
    {
        ssid = ssid.Trim();
        password = password.Trim();

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

            if (existing)
            {
                if (!string.IsNullOrEmpty(password))
                {
                    await RunProcessAsync("nmcli", new List<string> { "connection", "modify", connectionName, "wifi-sec.key-mgmt", "wpa-psk", "wifi-sec.psk", password }, 8000);
                }

                if (connectNow)
                    await ConnectSavedWifiAsync(connectionName);
            }
            else
            {
                var args = new List<string>
                {
                    "device", "wifi", "connect", ssid,
                    "ifname", "wlan0",
                    "name", connectionName
                };

                if (!string.IsNullOrEmpty(password))
                {
                    args.Add("password");
                    args.Add(password);
                }

                await RunProcessAsync("nmcli", args, 25000);
            }
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
            status = _networkStatus
        };
    }

    private static string ActiveNetworkLabel()
    {
        try
        {
            var output = RunProcessText("nmcli", new[] { "-t", "-f", "NAME,TYPE,DEVICE", "connection", "show", "--active" }, 2000);
            var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(line)) return "BRAK";
            return line.Length > 22 ? line[..22] : line;
        }
        catch { return "?"; }
    }

    private static string IpLabel()
    {
        try
        {
            var output = RunProcessText("hostname", new[] { "-I" }, 1500).Trim();
            if (string.IsNullOrWhiteSpace(output)) return "BRAK";
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
