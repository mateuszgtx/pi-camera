using System;
using System.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace pi_camera;

public static partial class Program
{
    private static int _vhsFrameSeed;

    private static bool IsVhsLook() => IsVhsLook(_lookPreset);

    private static bool IsVhsLook(string? preset)
        => string.Equals(preset, "VHS", StringComparison.OrdinalIgnoreCase);

    private static int NextVhsSeed()
    {
        unchecked
        {
            return Interlocked.Increment(ref _vhsFrameSeed);
        }
    }

    private static byte[] BuildVhsFrame(
        byte[] rgb,
        int width,
        int height,
        int seed,
        int glitchFrequency = -1,
        int quality = -1,
        int scanlines = -1,
        int noiseAmount = -1,
        int wobble = -1)
    {
        if (width <= 0 || height <= 0 || rgb.Length < width * height * 3)
            return rgb;

        glitchFrequency = glitchFrequency < 0 ? _vhsGlitchFrequency : Math.Clamp(glitchFrequency, 0, 10);
        quality = quality < 0 ? _vhsQuality : Math.Clamp(quality, 0, 10);
        scanlines = scanlines < 0 ? _vhsScanlines : Math.Clamp(scanlines, 0, 10);
        noiseAmount = noiseAmount < 0 ? _vhsNoise : Math.Clamp(noiseAmount, 0, 10);
        wobble = wobble < 0 ? _vhsWobble : Math.Clamp(wobble, 0, 10);

        var output = new byte[width * height * 3];
        var degraded = (10 - quality) / 10.0;
        var scanlineAmount = scanlines / 10.0;
        var noiseLevel = noiseAmount / 10.0;
        var wobbleLevel = wobble / 10.0;

        // Higher quality keeps more detail. Lower quality pushes color bleeding,
        // smear, noise and tape instability further.
        var chromaOffset = Math.Clamp((int)Math.Round(1 + (width / 260.0) * (0.25 + degraded * 1.55)), 1, 8);
        var smearWeight = Math.Clamp((int)Math.Round(12 - degraded * 8), 3, 12);
        var glitchActive = IsVhsGlitchFrame(seed, glitchFrequency);
        var tearY = PositiveMod(seed * 37 + 113, Math.Max(1, height));
        var tearHeight = glitchActive ? 1 + PositiveMod(seed * 11, Math.Max(2, height / 28)) : 0;
        var bottomTracking = Math.Max(3, height / 24);
        var scanlinePeriod = scanlines >= 8 ? 2 : scanlines >= 5 ? 3 : 4;
        var scanlineStrength = 0.04 + scanlineAmount * (0.24 + degraded * 0.08);
        var baseWaveA = wobbleLevel * (0.6 + degraded * 2.4);
        var baseWaveB = wobbleLevel * (0.35 + degraded * 1.2);

        // These values depend only on the frame settings, not on individual
        // pixels. Keeping them outside the inner loop removes millions of
        // repeated floating-point operations per second on a Raspberry Pi.
        var noiseRange = (int)Math.Round(4 + noiseLevel * 28 + degraded * 8);
        var chromaNoiseRange = (int)Math.Round(noiseLevel * 8 + degraded * 3);
        var redBoost = 1.0 + 0.02 + degraded * 0.05;
        var greenMul = 1.0 - degraded * 0.07;
        var blueMul = 1.0 - degraded * 0.18;
        var blueShift = -2 - degraded * 8;
        var snowModulo = glitchActive
            ? Math.Clamp(2200 - noiseAmount * 130, 650, 2200)
            : noiseAmount <= 0 ? 50000 : Math.Clamp(18000 - noiseAmount * 1400 + quality * 250, 2800, 50000);

        for (var y = 0; y < height; y++)
        {
            var wave = Math.Sin((y + seed * 3) * 0.055) * baseWaveA + Math.Sin(y * 0.17 + seed * 0.71) * baseWaveB;
            var shift = (int)Math.Round(wave);

            if (glitchActive && Math.Abs(y - tearY) <= tearHeight)
            {
                var dir = (seed & 1) == 0 ? 1 : -1;
                var maxTear = Math.Clamp(width / 14 + (int)Math.Round(width / 45.0 * degraded), 10, 44);
                shift += dir * (4 + PositiveMod(seed + y * 3, maxTear));
            }

            if (glitchActive && y >= height - bottomTracking)
            {
                var dir = ((seed / 3) & 1) == 0 ? 1 : -1;
                shift += dir * (2 + PositiveMod(seed + y, Math.Clamp(width / 36, 3, 16)));
            }

            var scanlineOn = scanlines > 0 && (y % scanlinePeriod) == scanlinePeriod - 1;
            var scanline = scanlineOn ? 1.0 - scanlineStrength : 1.0;
            var dropoutLimit = 1 + (int)Math.Round(noiseLevel * 3 + degraded * 2);
            var dropout = glitchActive && HashNoise(0, y, seed, 360) < dropoutLimit;
            var headSwitching = glitchActive && y >= height - bottomTracking && HashNoise(7, y, seed, 5) == 0;

            var rowOffset = y * width * 3;

            for (var x = 0; x < width; x++)
            {
                var sx = Wrap(x + shift, width);
                var rIndex = rowOffset + Wrap(sx + chromaOffset, width) * 3;
                var gIndex = rowOffset + sx * 3;
                var bIndex = rowOffset + Wrap(sx - chromaOffset, width) * 3;

                var r = rgb[rIndex];
                var g = rgb[gIndex + 1];
                var b = rgb[bIndex + 2];

                // Lekki poziomy smear jak composite. Przy wysokiej jakości jest subtelny.
                var sx2 = Wrap(sx - 1, width);
                var i2 = rowOffset + sx2 * 3;
                r = (byte)((r * smearWeight + rgb[i2]) / (smearWeight + 1));
                g = (byte)((g * smearWeight + rgb[i2 + 1]) / (smearWeight + 1));
                b = (byte)((b * smearWeight + rgb[i2 + 2]) / (smearWeight + 1));

                var noise = noiseAmount <= 0 ? 0 : HashNoise(x, y, seed, noiseRange * 2 + 1) - noiseRange;
                var chromaNoise = chromaNoiseRange <= 0 ? 0 : HashNoise(x / 2, y, seed + 97, chromaNoiseRange * 2 + 1) - chromaNoiseRange;

                var rr = ClampByte((int)Math.Round((r * redBoost + 3 + noise) * scanline));
                var gg = ClampByte((int)Math.Round((g * greenMul + noise * 0.35) * scanline));
                var bb = ClampByte((int)Math.Round((b * blueMul + blueShift + noise * 0.45 + chromaNoise) * scanline));

                if (dropout && HashNoise(x, y, seed + 211, 100) < 28 + noiseAmount * 4)
                {
                    var v = 170 + HashNoise(x, y, seed + 17, 80);
                    rr = gg = bb = ClampByte(v);
                }

                if (headSwitching)
                {
                    rr = ClampByte(rr + 18 + noiseAmount);
                    gg = ClampByte(gg + 13 + noiseAmount);
                    bb = ClampByte(bb + 8 + noiseAmount);
                }

                // Sporadyczne czarne/śnieżne kropeczki. Osobny suwak noise kontroluje ilość.
                var snow = HashNoise(x, y, seed + 409, snowModulo);
                if (snow == 0)
                {
                    rr = gg = bb = 255;
                }
                else if (snow == 1 && noiseAmount >= 5)
                {
                    rr = gg = bb = 0;
                }

                var o = rowOffset + x * 3;
                output[o] = (byte)rr;
                output[o + 1] = (byte)gg;
                output[o + 2] = (byte)bb;
            }
        }

        return output;
    }

    private static void ApplyVhsEffectToImage(Image<Rgb24> image)
    {
        if (!IsVhsLook())
            return;

        var source = new byte[image.Width * image.Height * 3];

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < image.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                var offset = y * image.Width * 3;

                for (var x = 0; x < image.Width; x++)
                {
                    source[offset + x * 3] = row[x].R;
                    source[offset + x * 3 + 1] = row[x].G;
                    source[offset + x * 3 + 2] = row[x].B;
                }
            }
        });

        var vhs = BuildVhsFrame(source, image.Width, image.Height, NextVhsSeed(), _vhsGlitchFrequency, _vhsQuality, _vhsScanlines, _vhsNoise, _vhsWobble);

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < image.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                var offset = y * image.Width * 3;

                for (var x = 0; x < image.Width; x++)
                {
                    var i = offset + x * 3;
                    row[x] = new Rgb24(vhs[i], vhs[i + 1], vhs[i + 2]);
                }
            }
        });
    }


    private static bool IsVhsGlitchFrame(int seed, int frequency)
    {
        frequency = Math.Clamp(frequency, 0, 10);
        if (frequency <= 0)
            return false;

        // 1 = very rare, 10 = frequent. With a 20 FPS preview this is roughly
        // every 9-11 seconds at 1-2, every few seconds in the middle, and near
        // constant short bursts at the highest values.
        var cycleFrames = Math.Clamp(260 - frequency * 22, 28, 260);
        var burstFrames = Math.Clamp(1 + frequency / 4, 1, 4);
        var phase = PositiveMod(seed, cycleFrames);
        return phase < burstFrames;
    }

    private static int PositiveMod(int value, int modulo)
    {
        if (modulo <= 1)
            return 0;

        var result = value % modulo;
        return result < 0 ? result + modulo : result;
    }

    private static int Wrap(int value, int max)
    {
        if (max <= 1)
            return 0;

        value %= max;
        return value < 0 ? value + max : value;
    }

    private static int ClampByte(int value) => Math.Clamp(value, 0, 255);

    private static int HashNoise(int x, int y, int seed, int modulo)
    {
        if (modulo <= 1)
            return 0;

        unchecked
        {
            var h = (uint)seed;
            h ^= (uint)x * 374761393u;
            h ^= (uint)y * 668265263u;
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (int)(h % (uint)modulo);
        }
    }
}
