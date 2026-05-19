namespace pi_camera.Services;

public sealed class PreviewSettings
{
    public double Ev { get; set; }
    public double Sharpness { get; set; }
    public double Contrast { get; set; }
    public double Saturation { get; set; }
    public double Brightness { get; set; }
    public int BlackLevel { get; set; }
    public double DarkLevel { get; set; }
    public int PreviewPixelSize { get; set; }
    public int PreviewColorLevels { get; set; }
    public string Denoise { get; set; } = "cdn_off";

    public PreviewSettings Clone()
    {
        return new PreviewSettings
        {
            Ev = Ev,
            Sharpness = Sharpness,
            Contrast = Contrast,
            Saturation = Saturation,
            Brightness = Brightness,
            BlackLevel = BlackLevel,
            DarkLevel = DarkLevel,
            PreviewPixelSize = PreviewPixelSize,
            PreviewColorLevels = PreviewColorLevels,
            Denoise = Denoise
        };
    }
}
