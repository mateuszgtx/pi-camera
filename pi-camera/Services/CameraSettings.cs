namespace pi_camera.Services;

public enum CaptureMode
{
    Photo,
    Video
}

public enum PhotoFormat
{
    Jpg,
    Png,
    Bmp
}

public enum VideoFormat
{
    H264,
    Mjpeg
}

public enum SensorQualityMode
{
    Full12Mp,
    Binned3Mp,
    FastPreview
}

public sealed class CameraSettings
{
    public CaptureMode CaptureMode { get; set; } = CaptureMode.Photo;
    public PhotoFormat PhotoFormat { get; set; } = PhotoFormat.Jpg;
    public VideoFormat VideoFormat { get; set; } = VideoFormat.H264;
    public SensorQualityMode SensorMode { get; set; } = SensorQualityMode.Full12Mp;
    public int JpegQuality { get; set; } = 95;
    public int VideoSeconds { get; set; } = 0;
    public double PhotoEv { get; set; } = -1.0;
    public string Metering { get; set; } = "centre";

    public CameraSettings Clone() => new()
    {
        CaptureMode = CaptureMode,
        PhotoFormat = PhotoFormat,
        VideoFormat = VideoFormat,
        SensorMode = SensorMode,
        JpegQuality = JpegQuality,
        VideoSeconds = VideoSeconds,
        PhotoEv = PhotoEv,
        Metering = Metering
    };

    public (int width, int height) PhotoSize => SensorMode switch
    {
        SensorQualityMode.Full12Mp => (4056, 3040),
        SensorQualityMode.Binned3Mp => (2028, 1520),
        SensorQualityMode.FastPreview => (1280, 960),
        _ => (4056, 3040)
    };

    public string SensorLabel => SensorMode switch
    {
        SensorQualityMode.Full12Mp => "FULL 12MP",
        SensorQualityMode.Binned3Mp => "BIN 3MP",
        SensorQualityMode.FastPreview => "FAST 1MP",
        _ => "FULL 12MP"
    };
}
