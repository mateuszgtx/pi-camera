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
    private static void DrawMode(FramebufferDisplay display, int width, int height)
    {
        display.Clear(0x0000);
        display.FillRect(0, 0, width, 38, 0x1082);
        display.DrawText($"MODE {_modePage + 1}/{ModePageCount()}", 8, 10, 0xFFFF);
        display.DrawText("MODE=OPTION  PREV/NEXT=CHANGE", 148, 10, 0xC618);

        if (_modePage == 0)
        {
            display.DrawTextScaled("COLORS", 8, 42, 0xFFFF, 2);
            DrawSettingRow(display, 74, width, "LOOK", _lookPreset, IsActiveModeRow(0));
            DrawSettingRow(display, 108, width, "COLORS", _selectedColorAmount.ToString(), IsActiveModeRow(1));
            DrawSettingRow(display, 142, width, "PALETTE", PaletteModeLabel(_paletteMode), IsActiveModeRow(2));
            DrawSettingRow(display, 176, width, "PIX/QUAL", _previewSettings.PreviewPixelSize + "/" + MaxPixelSizeForCurrentSource(), IsActiveModeRow(3));
        }
        else if (_modePage == 1)
        {
            display.DrawTextScaled("RGB SCALE", 8, 42, 0xFFFF, 2);
            DrawSettingRow(display, 82, width, "RED", _redScale.ToString("0.0"), IsActiveModeRow(0));
            DrawSettingRow(display, 116, width, "GREEN", _greenScale.ToString("0.0"), IsActiveModeRow(1));
            DrawSettingRow(display, 150, width, "BLUE", _blueScale.ToString("0.0"), IsActiveModeRow(2));
            DrawSettingRow(display, 184, width, "RESET", "1.0", IsActiveModeRow(3));
        }
        else if (_modePage == 2)
        {
            display.DrawTextScaled("PHOTO", 8, 42, 0xFFFF, 2);
            DrawSettingRow(display, 82, width, "SOURCE", PhotoSourceLabel(), IsActiveModeRow(0));
            DrawSettingRow(display, 116, width, "FORMAT", _photoFormat.ToUpperInvariant(), IsActiveModeRow(1));
            DrawSettingRow(display, 150, width, "WIDTH", _photoWidth.ToString(), IsActiveModeRow(2));
            DrawSettingRow(display, 184, width, "HEIGHT", _photoHeight.ToString(), IsActiveModeRow(3));
        }
        else if (_modePage == 3)
        {
            display.DrawTextScaled("VIDEO/SENSOR", 8, 42, 0xFFFF, 2);
            DrawSettingRow(display, 82, width, "MODE", CaptureKindLabel(_captureKind), IsActiveModeRow(0));
            DrawSettingRow(display, 116, width, "VIDEO", VideoFormatLabel(), IsActiveModeRow(1));
            DrawSettingRow(display, 150, width, "SENSOR", SensorLabel(_sensorMode), IsActiveModeRow(2));
            DrawSettingRow(display, 184, width, "PHOTO EV", _photoEv.ToString("0.0"), IsActiveModeRow(3));
        }
        else if (_modePage == 4)
        {
            display.DrawTextScaled("RANDOM", 8, 42, 0xFFFF, 2);
            DrawSettingRow(display, 74, width, "MIN FPS", _randomFrameMinFps.ToString(), IsActiveModeRow(0));
            DrawSettingRow(display, 108, width, "MAX FPS", _randomFrameMaxFps.ToString(), IsActiveModeRow(1));
            DrawSettingRow(display, 142, width, "SEG SEC", _randomFrameSeconds.ToString(), IsActiveModeRow(2));
            DrawSettingRow(display, 176, width, "VIDEO", VideoFormatLabel(), IsActiveModeRow(3));
            DrawSettingRow(display, 210, width, "SENSOR", SensorLabel(_sensorMode), IsActiveModeRow(4));
        }
        else if (_modePage == 5)
        {
            display.DrawTextScaled("GLITCH", 8, 42, 0xFFFF, 2);
            DrawSettingRow(display, 74, width, "STRENGTH", _glitchStrength.ToString(), IsActiveModeRow(0));
            DrawSettingRow(display, 108, width, "CHANGE", _glitchChangeMs + "ms", IsActiveModeRow(1));
            DrawSettingRow(display, 142, width, "PALETTE", OnOff(_glitchPaletteEnabled), IsActiveModeRow(2));
            DrawSettingRow(display, 176, width, "PIX", OnOff(_glitchPixelsEnabled), IsActiveModeRow(3));
            DrawSettingRow(display, 210, width, "RGB", OnOff(_glitchRgbEnabled), IsActiveModeRow(4));
        }
        else if (_modePage == 6)
        {
            display.DrawTextScaled("VHS", 8, 42, 0xFFFF, 2);
            DrawSettingRow(display, 74, width, "GLITCH FRQ", _vhsGlitchFrequency + "/10", IsActiveModeRow(0));
            DrawSettingRow(display, 108, width, "QUALITY", _vhsQuality + "/10", IsActiveModeRow(1));
            DrawSettingRow(display, 142, width, "STRIPES", _vhsScanlines + "/10", IsActiveModeRow(2));
            DrawSettingRow(display, 176, width, "NOISE", _vhsNoise + "/10", IsActiveModeRow(3));
            DrawSettingRow(display, 210, width, "WOBBLE", _vhsWobble + "/10", IsActiveModeRow(4));
        }
        else if (_modePage == 7)
        {
            display.DrawTextScaled("LIVE 1/2", 8, 42, 0xFFFF, 2);
            DrawSettingRow(display, 74, width, "EV", _previewSettings.Ev.ToString("0.0"), IsActiveModeRow(0));
            DrawSettingRow(display, 108, width, "BLACK", _previewSettings.BlackLevel.ToString(), IsActiveModeRow(1));
            DrawSettingRow(display, 142, width, "DARK", _previewSettings.DarkLevel.ToString("0.00"), IsActiveModeRow(2));
            DrawSettingRow(display, 176, width, "PIXEL", _previewSettings.PreviewPixelSize.ToString(), IsActiveModeRow(3));
            DrawSettingRow(display, 210, width, "COLORS", _previewSettings.PreviewColorLevels.ToString(), IsActiveModeRow(4));
        }
        else
        {
            display.DrawTextScaled("LIVE 2/2", 8, 42, 0xFFFF, 2);
            DrawSettingRow(display, 74, width, "CONTR", _previewSettings.Contrast.ToString("0.0"), IsActiveModeRow(0));
            DrawSettingRow(display, 108, width, "SAT", _previewSettings.Saturation.ToString("0.0"), IsActiveModeRow(1));
            DrawSettingRow(display, 142, width, "BRIGHT", _previewSettings.Brightness.ToString("0.00"), IsActiveModeRow(2));
            DrawSettingRow(display, 176, width, "SHARP", _previewSettings.Sharpness.ToString("0.0"), IsActiveModeRow(3));
            DrawSettingRow(display, 210, width, "JPG Q", _jpgQuality.ToString(), IsActiveModeRow(4));
        }

        DrawModePageButton(display, width, height);
        DrawTabs(display, width, height);
        display.Flush();
    }

    private static string OnOff(bool value) => value ? "ON" : "OFF";

    private static void DrawModePageButton(FramebufferDisplay display, int width, int height)
    {
        var y = height - 44 - 38;
        var active = _modeRow == ModeRowCount(_modePage) - 1;
        display.FillRect(8, y, width - 16, 34, active ? 0xFFFF : 0x2104);
        var label = _modePage == 0 ? "NEXT: RGB SCALE" :
                    _modePage == 1 ? "NEXT: PHOTO" :
                    _modePage == 2 ? "NEXT: VIDEO/SENSOR" :
                    _modePage == 3 ? "NEXT: RANDOM" :
                    _modePage == 4 ? "NEXT: GLITCH" :
                    _modePage == 5 ? "NEXT: VHS" :
                    _modePage == 6 ? "NEXT: LIVE 1/2" :
                    _modePage == 7 ? "NEXT: LIVE 2/2" :
                    "BACK: COLORS";
        display.DrawCenteredText(label, y + 12, active ? 0x0000 : 0xFFFF);
    }

    private static void DrawBigMinusPlus(FramebufferDisplay display, int width, int y)
    {
        display.FillRect(28, y, 120, 50, 0x4208);
        display.DrawTextScaled("-", 76, y + 15, 0xFFFF, 3);
        display.FillRect(width - 148, y, 120, 50, 0x4208);
        display.DrawTextScaled("+", width - 100, y + 15, 0xFFFF, 3);
    }

    private static void RefreshGallery(string outputDir)
    {
        _galleryFiles = Directory.Exists(outputDir)
            ? Directory.GetFiles(outputDir)
                .Where(IsPhotoFile)
                .OrderByDescending(File.GetLastWriteTime)
                .ToList()
            : new List<string>();

        if (_galleryFiles.Count == 0)
            _galleryIndex = 0;
        else
            _galleryIndex = Math.Clamp(_galleryIndex, 0, _galleryFiles.Count - 1);
    }

    private static void DrawGallery(FramebufferDisplay display, int width, int height, string outputDir)
    {
        RefreshGallery(outputDir);
        display.Clear(0x0000);
        display.FillRect(0, 0, width, 44, 0x2104);
        display.FillRect(0, 0, 112, 44, 0x4208);
        display.FillRect(width - 112, 0, 112, 44, 0x4208);
        display.DrawTextScaled("<", 44, 15, 0xFFFF, 2);
        display.DrawTextScaled(">", width - 68, 15, 0xFFFF, 2);

        if (_galleryFiles.Count == 0)
        {
            display.DrawCenteredTextScaled("NO FILES", height / 2 - 20, 0xFFFF, 2);
            DrawTabs(display, width, height);
            display.Flush();
            return;
        }

        var file = _galleryFiles[_galleryIndex];
        display.DrawText($"{_galleryIndex + 1}/{_galleryFiles.Count}", 210, 16, 0xFFFF);

        if (TryGetGalleryDisplayImagePath(file, out var displayImagePath))
        {
            try
            {
                var rgb = ImageLoader.LoadImageRgb(displayImagePath, out var imgW, out var imgH);
                display.DrawRgbFrameScaledKeepAspect(rgb, imgW, imgH, 0, 46, width, height - 46 - 44);
            }
            catch (Exception ex)
            {
                display.DrawCenteredTextScaled("FILE ERROR", 90, 0xF800, 2);
                display.DrawWrappedText(ex.Message, 8, 130, 0xFFFF);
            }
        }
        else
        {
            display.DrawCenteredTextScaled("RAW/DNG", 100, 0xFFE0, 2);
            display.DrawCenteredText("NO JPG PREVIEW", 135, 0xFFFF);
        }

        display.FillRect(0, height - 64, width, 20, 0x0000);
        display.DrawText(Path.GetFileName(file), 8, height - 58, 0xFFFF);
        DrawTabs(display, width, height);
        display.Flush();
    }

    private static void DrawNetwork(FramebufferDisplay display, int width, int height)
    {
        EnsureWifiConnectionsLoaded(force: false);
        display.Clear(0x0000);
        display.FillRect(0, 0, width, 38, 0x1082);
        display.DrawText($"NETWORK {_networkPage + 1}/{NetworkPageCount()}", 8, 10, 0xFFFF);
        display.DrawText("MODE=OPTION  +/-=ACTION", 150, 10, 0xC618);

        if (_networkPage == 0)
        {
            display.DrawTextScaled("WIFI / HOTSPOT", 8, 44, 0xFFFF, 2);
            DrawSettingRow(display, 82, width, "HOTSPOT", HotspotStatusLabel(), _networkRow == 0);
            DrawSettingRow(display, 116, width, "WIFI", WifiRadioStatusLabel(), _networkRow == 1);
            DrawSettingRow(display, 150, width, "SAVED", SelectedWifiLabel(), _networkRow == 2);
            DrawSettingRow(display, 184, width, "CONNECT", SelectedWifiActionLabel(), _networkRow == 3);
            DrawSettingRow(display, 218, width, "REFRESH", "NMCLI", _networkRow == 4);
        }
        else if (_networkPage == 1)
        {
            display.DrawTextScaled("STATUS", 8, 44, 0xFFFF, 2);
            DrawSettingRow(display, 82, width, "ACTIVE", ActiveNetworkLabel(), _networkRow == 0);
            DrawSettingRow(display, 116, width, "IP", IpLabel(), _networkRow == 1);
            DrawSettingRow(display, 150, width, "BATTERY", BatteryStatusText(), _networkRow == 2);
            DrawSettingRow(display, 184, width, "REFRESH", "STATUS", _networkRow == 3);
        }
        else
        {
            EnsureBluetoothDevicesLoaded(force: false);
            display.DrawTextScaled("AUDIO / BT", 8, 44, 0xFFFF, 2);
            DrawSettingRow(display, 68, width, "AUDIO", _audioInputMode.ToString().ToUpperInvariant(), _networkRow == 0);
            DrawSettingRow(display, 98, width, "SOURCE", AudioSourceLabel(), _networkRow == 1);
            DrawSettingRow(display, 128, width, "BT POWER", BluetoothRadioStatusLabel(), _networkRow == 2);
            DrawSettingRow(display, 158, width, "BT DEVICE", SelectedBluetoothLabel(), _networkRow == 3);
            DrawSettingRow(display, 188, width, "BT SCAN", BluetoothScanLabel(), _networkRow == 4);
            DrawSettingRow(display, 218, width, "BT CONNECT", BluetoothConnectLabel(), _networkRow == 5);
        }

        if (!string.IsNullOrWhiteSpace(_networkStatus) && DateTime.UtcNow < _networkStatusUntilUtc)
        {
            display.FillRect(8, height - 44 - 62, width - 16, 22, 0x2104);
            display.DrawText(_networkStatus.Length > 44 ? _networkStatus[..44] : _networkStatus, 16, height - 44 - 56, 0xFFE0);
        }

        var y = height - 44 - 38;
        var pageButtonActive = _networkRow == NetworkRowCount() - 1;
        display.FillRect(8, y, width - 16, 34, pageButtonActive ? 0xFFFF : 0x2104);
        var pageLabel = _networkPage == 0 ? "NEXT: STATUS" : _networkPage == 1 ? "NEXT: AUDIO/BT" : "BACK: WIFI/HOTSPOT";
        display.DrawCenteredText(pageLabel, y + 12, pageButtonActive ? 0x0000 : 0xFFFF);
        DrawTabs(display, width, height);
        display.Flush();
    }

    private static void RedrawNonPreview(FramebufferDisplay display, int width, int height, string outputDir)
    {
        if (_tab == Tab.Mode)
            DrawMode(display, width, height);
        else if (_tab == Tab.Gallery)
            DrawGallery(display, width, height, outputDir);
        else if (_tab == Tab.Network)
            DrawNetwork(display, width, height);
    }

    private static void DrawTopBar(FramebufferDisplay display, int width)
    {
        display.FillRect(0, 0, width, 28, 0x0000);
        var battery = BatteryStatusText();
        display.DrawText(string.IsNullOrWhiteSpace(battery) ? "BAT --" : battery, 4, 10, 0x07E0);

        // Keep the preview top bar readable on small TFT screens. Longer full labels
        // such as GLITCH PHOTO / GLITCH VIDEO are still shown in the Mode tab, but
        // abbreviated here so they do not collide with the palette name.
        display.DrawText(CaptureKindTopBarLabel(_captureKind), 74, 10, 0xFFFF);
        display.DrawText(PaletteModeTopBarLabel(_paletteMode), 134, 10, 0xFFE0);

        var statusX = 220;
        if (_previewRecording)
        {
            display.DrawText(_previewRandomRecording ? "RND" : "REC", statusX, 10, 0xF800);
            statusX += 38;
        }
        if (_streaming)
        {
            display.DrawText("STR", statusX, 10, 0x07E0);
            statusX += 38;
        }
        if (IsAudioEnabled())
            display.DrawText("MIC", statusX, 10, 0xFFE0);
        display.DrawText(DateTime.Now.ToString("HH:mm"), width - 38, 10, 0xFFFF);
    }

    private static string CaptureKindTopBarLabel(CaptureKind kind)
    {
        return kind switch
        {
            CaptureKind.Photo => "PHOTO",
            CaptureKind.Video => "VIDEO",
            CaptureKind.RandomFrame => "RANDOM",
            CaptureKind.GlitchPhoto => "GPHOTO",
            CaptureKind.GlitchVideo => "GVIDEO",
            CaptureKind.Stream => "STREAM",
            _ => kind.ToString().ToUpperInvariant()
        };
    }

    private static string PaletteModeTopBarLabel(PaletteMode mode)
    {
        return mode switch
        {
            PaletteMode.Green565 => "GRN565",
            PaletteMode.Balanced => "BAL",
            PaletteMode.Green => "GREEN",
            PaletteMode.Yellow => "YELLOW",
            PaletteMode.Blue => "BLUE",
            PaletteMode.Red => "RED",
            PaletteMode.Cyan => "CYAN",
            PaletteMode.Magenta => "MAG",
            PaletteMode.Amber => "AMBER",
            PaletteMode.Gray => "GRAY",
            PaletteMode.Warm => "WARM",
            PaletteMode.Cold => "COLD",
            _ => mode.ToString().ToUpperInvariant()
        };
    }

    private static void DrawTabs(FramebufferDisplay display, int width, int height)
    {
        var y = height - 44;
        var w = width / 4;
        DrawTab(display, 0, y, w, 44, "LIVE", _tab == Tab.Preview);
        DrawTab(display, w, y, w, 44, "MODE", _tab == Tab.Mode);
        DrawTab(display, w * 2, y, w, 44, "MEDIA", _tab == Tab.Gallery);
        DrawTab(display, w * 3, y, width - w * 3, 44, "NET", _tab == Tab.Network);
    }

    private static void DrawTab(FramebufferDisplay display, int x, int y, int w, int h, string text, bool active)
    {
        display.FillRect(x, y, w, h, active ? (ushort)0x03E0 : (ushort)0x2104);
        display.DrawText(text, x + 10, y + 16, 0xFFFF);
    }

    private static void DrawSettingRow(FramebufferDisplay display, int y, int width, string name, string value, bool active = false)
    {
        display.FillRect(8, y, width - 16, 30, active ? 0xFFFF : 0x1082);
        display.DrawText(name, 18, y + 10, active ? 0x0000 : 0xC618);
        display.DrawText(value, width - 170, y + 10, active ? 0x0000 : 0xFFFF);
    }

    private static void DrawBusy(FramebufferDisplay display, int width, int height, string text)
    {
        display.Clear(0x0000);
        display.DrawCenteredTextScaled(text, height / 2 - 16, 0xFFE0, 2);
        display.Flush();
    }

    private static void DrawSaved(FramebufferDisplay display, int width, int height, string text)
    {
        display.Clear(0x0000);
        display.DrawCenteredTextScaled("OK", height / 2 - 30, 0x07E0, 2);
        display.DrawCenteredText(text, height / 2 + 4, 0xFFFF);
        display.Flush();
    }

    private static void DrawError(FramebufferDisplay display, int width, int height, string error)
    {
        display.Clear(0x0000);
        display.DrawCenteredTextScaled("ERROR", 35, 0xF800, 2);
        display.DrawWrappedText(error, 8, 80, 0xFFFF);
        DrawTabs(display, width, height);
        display.Flush();
    }

}
