namespace pi_camera.Services;

public sealed class FramebufferDisplay : IDisposable
{
    private readonly FileStream _stream;
    private readonly int _rotate;
    private readonly bool _swapRedBlue;
    private readonly byte[] _buffer;

    public int Width { get; }
    public int Height { get; }

    public FramebufferDisplay(string path, int width, int height, int rotate, bool swapRedBlue = false)
    {
        Width = width;
        Height = height;
        _rotate = rotate;
        _swapRedBlue = swapRedBlue;
        _buffer = new byte[width * height * 2];
        _stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
    }

    public void Clear(int color)
    {
        for (var i = 0; i < _buffer.Length; i += 2)
        {
            _buffer[i] = (byte)(color & 0xFF);
            _buffer[i + 1] = (byte)(color >> 8);
        }
    }

    public void Flush()
    {
        _stream.Position = 0;
        _stream.Write(_buffer, 0, _buffer.Length);
        _stream.Flush();
    }

    public void DrawRgbFrame(byte[] rgb, int srcW, int srcH, int dstX, int dstY)
    {
        for (var y = 0; y < srcH; y++)
        {
            var ty = dstY + y;
            if (ty < 0 || ty >= Height) continue;

            for (var x = 0; x < srcW; x++)
            {
                var tx = dstX + x;
                if (tx < 0 || tx >= Width) continue;

                var i = (y * srcW + x) * 3;
                var color = Rgb565(rgb[i], rgb[i + 1], rgb[i + 2]);
                SetPixel(tx, ty, color);
            }
        }
    }


    public void DrawRgbFrameAdjusted(byte[] rgb, int srcW, int srcH, int dstX, int dstY, int blackLevel, double darkLevel, int pixelSize, int colorLevels, double redScale = 1.0, double greenScale = 1.0, double blueScale = 1.0, string paletteMode = "green565")
    {
        if (srcW <= 0 || srcH <= 0 || rgb.Length < srcW * srcH * 3)
            return;

        pixelSize = Math.Clamp(pixelSize, 1, 32);
        colorLevels = Math.Clamp(colorLevels, 2, 256);

        var drawW = Math.Min(Width - dstX, srcW);
        var drawH = Math.Min(Height - dstY - 44, srcH);
        if (drawW <= 0 || drawH <= 0)
            return;

        var denom = Math.Max(1, 255 - blackLevel);

        for (var y = 0; y < drawH; y += pixelSize)
        {
            for (var x = 0; x < drawW; x += pixelSize)
            {
                var sampleX = Math.Min(srcW - 1, x + pixelSize / 2);
                var sampleY = Math.Min(srcH - 1, y + pixelSize / 2);
                var i = (sampleY * srcW + sampleX) * 3;

                var r0 = ApplyBlackDark(rgb[i], blackLevel, denom, darkLevel);
                var g0 = ApplyBlackDark(rgb[i + 1], blackLevel, denom, darkLevel);
                var b0 = ApplyBlackDark(rgb[i + 2], blackLevel, denom, darkLevel);

                (r0, g0, b0) = ApplyColorScaleDisplay(r0, g0, b0, redScale, greenScale, blueScale);
                (r0, g0, b0) = DisplayToneMap(r0, g0, b0);

                var (r, g, b) = QuantizePalette(r0, g0, b0, colorLevels, paletteMode);

                var color = Rgb565((byte)r, (byte)g, (byte)b);

                var maxY = Math.Min(drawH, y + pixelSize);
                var maxX = Math.Min(drawW, x + pixelSize);

                for (var yy = y; yy < maxY; yy++)
                {
                    for (var xx = x; xx < maxX; xx++)
                        SetPixel(dstX + xx, dstY + yy, color);
                }
            }
        }
    }





    private static (int R, int G, int B) ApplyColorScaleDisplay(int r, int g, int b, double redScale, double greenScale, double blueScale)
    {
        r = Math.Clamp((int)Math.Round(r * redScale), 0, 255);
        g = Math.Clamp((int)Math.Round(g * greenScale), 0, 255);
        b = Math.Clamp((int)Math.Round(b * blueScale), 0, 255);
        return (r, g, b);
    }


    private static (int R, int G, int B) DisplayToneMap(int r, int g, int b)
    {
        // TYLKO EKRAN TFT.
        // Zdjęcia i nagrania nie używają tej korekcji.
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var luma = (r * 30 + g * 59 + b * 11) / 100;

        // Przepalone punkty na małym RGB565 nie mogą wskakiwać w czarny/czerwony.
        if (max >= 230)
        {
            var v = Math.Clamp(luma, 210, 245);
            return (v, v, v);
        }

        // Jasne poświaty zmniejszamy w nasyceniu tylko na ekranie.
        if (max >= 190 && max - min > 45)
        {
            var blend = Math.Clamp((max - 190) / 70.0, 0.0, 0.75);
            var gray = Math.Clamp(luma, 165, 235);
            r = Math.Clamp((int)(r * (1.0 - blend) + gray * blend), 0, 255);
            g = Math.Clamp((int)(g * (1.0 - blend) + gray * blend), 0, 255);
            b = Math.Clamp((int)(b * (1.0 - blend) + gray * blend), 0, 255);
        }

        return (r, g, b);
    }

    private static bool IsMonoPaletteMode(string mode)
    {
        mode = (mode ?? "").ToLowerInvariant();
        return mode is "green" or "yellow" or "blue" or "red" or "cyan" or "magenta" or "amber";
    }

    private static (int R, int G, int B) MonoTint(string mode)
    {
        return (mode ?? "").ToLowerInvariant() switch
        {
            "green" => (55, 255, 75),
            "yellow" => (255, 225, 45),
            "blue" => (65, 145, 255),
            "red" => (255, 65, 55),
            "cyan" => (55, 245, 255),
            "magenta" => (255, 65, 235),
            "amber" => (255, 145, 25),
            _ => (255, 255, 255)
        };
    }

    private static (int R, int G, int B) ApplyMonoPalette(int r, int g, int b, int palette, string mode)
    {
        palette = Math.Clamp(palette, 2, 256);
        var luma = Math.Clamp((r * 30 + g * 59 + b * 11) / 100, 0, 255);
        var idx = (int)Math.Round((luma / 255.0) * (palette - 1));
        var t = idx / (double)(palette - 1);
        t = 0.025 + 0.975 * Math.Pow(t, 0.92);
        var tint = MonoTint(mode);
        return (
            Math.Clamp((int)Math.Round(tint.R * t), 0, 255),
            Math.Clamp((int)Math.Round(tint.G * t), 0, 255),
            Math.Clamp((int)Math.Round(tint.B * t), 0, 255)
        );
    }

    private static (int R, int G, int B) QuantizePalette(int r, int g, int b, int palette, string paletteMode = "green565")
    {
        var mode = (paletteMode ?? "green565").ToLowerInvariant();
        if (palette >= 256 && !IsMonoPaletteMode(mode))
            return (r, g, b);

        if (IsMonoPaletteMode(mode))
            return ApplyMonoPalette(r, g, b, palette, mode);

        if (mode == "gray")
        {
            var gray = (r * 30 + g * 59 + b * 11) / 100;
            var levels = palette <= 16 ? Math.Max(2, palette) : 16;
            var q = QuantizeChannel(gray, levels);
            return (q, q, q);
        }

        if (mode == "balanced")
        {
            r = Math.Clamp((int)Math.Round(r * 1.08), 0, 255);
            g = Math.Clamp((int)Math.Round(g * 0.88), 0, 255);
            b = Math.Clamp((int)Math.Round(b * 1.08), 0, 255);
        }

        if (mode == "warm")
        {
            r = Math.Clamp((int)(r * 1.15), 0, 255);
            b = Math.Clamp((int)(b * 0.82), 0, 255);
        }
        else if (mode == "cold")
        {
            r = Math.Clamp((int)(r * 0.82), 0, 255);
            b = Math.Clamp((int)(b * 1.18), 0, 255);
        }

        if (mode == "green565")
        {
            if (palette <= 4)
            {
                var gray = (r * 30 + g * 59 + b * 11) / 100;
                var q = QuantizeChannel(gray, palette);
                return (q, q, q);
            }
            if (palette <= 8) return (QuantizeChannel(r, 2), QuantizeChannel(g, 2), QuantizeChannel(b, 2));
            if (palette <= 16) return (QuantizeChannel(r, 2), QuantizeChannel(g, 4), QuantizeChannel(b, 2));
            if (palette <= 32) return (QuantizeChannel(r, 4), QuantizeChannel(g, 4), QuantizeChannel(b, 2));
            if (palette <= 64) return (QuantizeChannel(r, 4), QuantizeChannel(g, 4), QuantizeChannel(b, 4));
            return (QuantizeChannel(r, 6), QuantizeChannel(g, 7), QuantizeChannel(b, 6));
        }

        if (palette <= 4)
        {
            var gray = (r * 30 + g * 59 + b * 11) / 100;
            var q = QuantizeChannel(gray, palette);
            return (q, q, q);
        }
        if (palette <= 8) return (QuantizeChannel(r, 2), QuantizeChannel(g, 2), QuantizeChannel(b, 2));
        if (palette <= 16) return (QuantizeChannel(r, 2), QuantizeChannel(g, 2), QuantizeChannel(b, 4));
        if (palette <= 32) return (QuantizeChannel(r, 4), QuantizeChannel(g, 2), QuantizeChannel(b, 4));
        if (palette <= 64) return (QuantizeChannel(r, 4), QuantizeChannel(g, 4), QuantizeChannel(b, 4));
        return (QuantizeChannel(r, 5), QuantizeChannel(g, 5), QuantizeChannel(b, 5));
    }







    private static int QuantizeChannel(int value, int levels)
    {
        if (levels >= 256)
            return value;

        if (levels <= 2)
            return value < 128 ? 0 : 255;

        var step = 255.0 / (levels - 1);
        return Math.Clamp((int)(Math.Round(value / step) * step), 0, 255);
    }



    private static int ApplyBlackDark(byte value, int blackLevel, int denom, double darkLevel)
    {
        // TYLKO EKRAN: nie ścinamy bardzo jasnych pikseli czernią.
        if (value >= 225)
            return value;

        var v = value - blackLevel;
        if (v <= 0)
            return 0;

        return Math.Clamp((int)((v * 255 / denom) * darkLevel), 0, 255);
    }





    public void DrawRgbFrameScaledKeepAspect(byte[] rgb, int srcW, int srcH, int areaX, int areaY, int areaW, int areaH)
    {
        if (srcW <= 0 || srcH <= 0 || areaW <= 0 || areaH <= 0)
            return;

        var scaleX = (double)areaW / srcW;
        var scaleY = (double)areaH / srcH;
        var scale = Math.Min(scaleX, scaleY);

        var dstW = Math.Max(1, (int)(srcW * scale));
        var dstH = Math.Max(1, (int)(srcH * scale));

        var dstX = areaX + (areaW - dstW) / 2;
        var dstY = areaY + (areaH - dstH) / 2;

        FillRect(areaX, areaY, areaW, areaH, 0x0000);

        for (var y = 0; y < dstH; y++)
        {
            var sy = Math.Min(srcH - 1, (int)(y / scale));
            var ty = dstY + y;

            if (ty < 0 || ty >= Height)
                continue;

            for (var x = 0; x < dstW; x++)
            {
                var sx = Math.Min(srcW - 1, (int)(x / scale));
                var tx = dstX + x;

                if (tx < 0 || tx >= Width)
                    continue;

                var i = (sy * srcW + sx) * 3;
                SetPixel(tx, ty, Rgb565(rgb[i], rgb[i + 1], rgb[i + 2]));
            }
        }
    }

    public void FillRect(int x, int y, int w, int h, int color)
    {
        for (var yy = y; yy < y + h; yy++)
        {
            if (yy < 0 || yy >= Height) continue;

            for (var xx = x; xx < x + w; xx++)
            {
                if (xx < 0 || xx >= Width) continue;
                SetPixel(xx, yy, color);
            }
        }
    }

    public void SetPixel(int x, int y, int color)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
            return;

        var (rx, ry) = Rotate(x, y);

        if (rx < 0 || ry < 0 || rx >= Width || ry >= Height)
            return;

        var index = (ry * Width + rx) * 2;
        var c = (ushort)Math.Clamp(color, 0, 0xFFFF);
        _buffer[index] = (byte)(c & 0xFF);
        _buffer[index + 1] = (byte)(c >> 8);
    }

    private (int x, int y) Rotate(int x, int y)
    {
        return _rotate switch
        {
            90 => (Height - 1 - y, x),
            180 => (Width - 1 - x, Height - 1 - y),
            270 => (y, Width - 1 - x),
            _ => (x, y)
        };
    }

    private ushort Rgb565(byte r, byte g, byte b)
    {
        if (_swapRedBlue)
            (r, b) = (b, r);

        return (ushort)(((r & 0xF8) << 8) | ((g & 0xFC) << 3) | (b >> 3));
    }



    public void DrawCenteredText(string text, int y, int color)
    {
        var x = Math.Max(0, (Width - text.Length * 6) / 2);
        DrawText(text, x, y, color);
    }

    public void DrawCenteredTextScaled(string text, int y, int color, int scale)
    {
        var x = Math.Max(0, (Width - text.Length * 6 * scale) / 2);
        DrawTextScaled(text, x, y, color, scale);
    }

    public void DrawWrappedText(string text, int x, int y, int color)
    {
        var maxChars = Math.Max(10, (Width - x * 2) / 6);
        var line = "";

        foreach (var word in text.Split(' '))
        {
            if ((line + " " + word).Length > maxChars)
            {
                DrawText(line, x, y, color);
                y += 12;
                line = word;
            }
            else
            {
                line = string.IsNullOrEmpty(line) ? word : line + " " + word;
            }
        }

        if (!string.IsNullOrWhiteSpace(line))
            DrawText(line, x, y, color);
    }

    public void DrawText(string text, int x, int y, int color)
    {
        DrawTextScaled(text, x, y, color, 1);
    }

    public void DrawTextScaled(string text, int x, int y, int color, int scale)
    {
        foreach (var ch in text)
        {
            DrawCharScaled(Font5x7.Normalize(ch), x, y, color, scale);
            x += 6 * scale;
        }
    }

    private void DrawCharScaled(char ch, int x, int y, int color, int scale)
    {
        var glyph = Font5x7.Get(ch);

        for (var col = 0; col < 5; col++)
        {
            var bits = glyph[col];

            for (var row = 0; row < 7; row++)
            {
                if (((bits >> row) & 1) == 0)
                    continue;

                for (var sy = 0; sy < scale; sy++)
                    for (var sx = 0; sx < scale; sx++)
                        SetPixel(x + col * scale + sx, y + row * scale + sy, color);
            }
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
    }
}

internal static class Font5x7
{
    private static readonly Dictionary<char, byte[]> Map = new()
    {
        [' '] = [0,0,0,0,0],
        ['0'] = [0x3E,0x51,0x49,0x45,0x3E],
        ['1'] = [0x00,0x42,0x7F,0x40,0x00],
        ['2'] = [0x42,0x61,0x51,0x49,0x46],
        ['3'] = [0x21,0x41,0x45,0x4B,0x31],
        ['4'] = [0x18,0x14,0x12,0x7F,0x10],
        ['5'] = [0x27,0x45,0x45,0x45,0x39],
        ['6'] = [0x3C,0x4A,0x49,0x49,0x30],
        ['7'] = [0x01,0x71,0x09,0x05,0x03],
        ['8'] = [0x36,0x49,0x49,0x49,0x36],
        ['9'] = [0x06,0x49,0x49,0x29,0x1E],
        ['A'] = [0x7E,0x11,0x11,0x11,0x7E],
        ['B'] = [0x7F,0x49,0x49,0x49,0x36],
        ['C'] = [0x3E,0x41,0x41,0x41,0x22],
        ['D'] = [0x7F,0x41,0x41,0x22,0x1C],
        ['E'] = [0x7F,0x49,0x49,0x49,0x41],
        ['F'] = [0x7F,0x09,0x09,0x09,0x01],
        ['G'] = [0x3E,0x41,0x49,0x49,0x7A],
        ['H'] = [0x7F,0x08,0x08,0x08,0x7F],
        ['I'] = [0x00,0x41,0x7F,0x41,0x00],
        ['J'] = [0x20,0x40,0x41,0x3F,0x01],
        ['K'] = [0x7F,0x08,0x14,0x22,0x41],
        ['L'] = [0x7F,0x40,0x40,0x40,0x40],
        ['Ł'] = [0x7F,0x48,0x50,0x60,0x40],
        ['M'] = [0x7F,0x02,0x0C,0x02,0x7F],
        ['N'] = [0x7F,0x04,0x08,0x10,0x7F],
        ['O'] = [0x3E,0x41,0x41,0x41,0x3E],
        ['P'] = [0x7F,0x09,0x09,0x09,0x06],
        ['Q'] = [0x3E,0x41,0x51,0x21,0x5E],
        ['R'] = [0x7F,0x09,0x19,0x29,0x46],
        ['S'] = [0x46,0x49,0x49,0x49,0x31],
        ['T'] = [0x01,0x01,0x7F,0x01,0x01],
        ['U'] = [0x3F,0x40,0x40,0x40,0x3F],
        ['V'] = [0x1F,0x20,0x40,0x20,0x1F],
        ['W'] = [0x7F,0x20,0x18,0x20,0x7F],
        ['X'] = [0x63,0x14,0x08,0x14,0x63],
        ['Y'] = [0x03,0x04,0x78,0x04,0x03],
        ['Z'] = [0x61,0x51,0x49,0x45,0x43],
        ['.'] = [0x00,0x60,0x60,0x00,0x00],
        ['-'] = [0x08,0x08,0x08,0x08,0x08],
        ['_'] = [0x40,0x40,0x40,0x40,0x40],
        [':'] = [0x00,0x36,0x36,0x00,0x00],
        ['/'] = [0x20,0x10,0x08,0x04,0x02],
    };

    public static char Normalize(char c)
    {
        c = char.ToUpperInvariant(c);

        return c switch
        {
            'Ł' => 'Ł',
            'Ą' => 'A',
            'Ć' => 'C',
            'Ę' => 'E',
            'Ń' => 'N',
            'Ó' => 'O',
            'Ś' => 'S',
            'Ź' => 'Z',
            'Ż' => 'Z',
            _ => c
        };
    }

    public static byte[] Get(char c)
    {
        c = Normalize(c);
        return Map.TryGetValue(c, out var g) ? g : Map[' '];
    }
}
