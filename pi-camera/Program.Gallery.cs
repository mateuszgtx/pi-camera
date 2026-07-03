using pi_camera.Services;

namespace pi_camera;

public static partial class Program
{
    private const string GalleryPreviewSuffix = ".preview.jpg";

    private static bool IsGalleryPreviewFile(string path)
    {
        return Path.GetFileName(path).EndsWith(GalleryPreviewSuffix, StringComparison.OrdinalIgnoreCase);
    }

    private static string GalleryPreviewPathFor(string mediaPath)
    {
        return mediaPath + GalleryPreviewSuffix;
    }

    private static bool IsPhotoFile(string path)
    {
        if (IsGalleryPreviewFile(path))
            return false;

        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".dng" or ".raw";
    }

    private static bool IsRawPhotoFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".dng" or ".raw";
    }

    private static bool IsVideoFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".avi" or ".mp4" or ".mjpeg" or ".rawmjpeg";
    }

    private static bool IsMediaFile(string path)
    {
        return !IsGalleryPreviewFile(path) && (IsPhotoFile(path) || IsVideoFile(path));
    }

    private static string ContentTypeFor(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".bmp" => "image/bmp",
            ".dng" => "image/x-adobe-dng",
            ".raw" => "application/octet-stream",
            ".avi" => "video/x-msvideo",
            ".mp4" => "video/mp4",
            ".mjpeg" or ".rawmjpeg" => "video/x-motion-jpeg",
            _ => "image/jpeg"
        };
    }

    private static bool TryFindRawCompanionImage(string mediaPath, out string companionPath)
    {
        var dir = Path.GetDirectoryName(mediaPath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(mediaPath);

        foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".bmp" })
        {
            var candidate = Path.Combine(dir, stem + ext);
            if (File.Exists(candidate) && !IsGalleryPreviewFile(candidate))
            {
                companionPath = candidate;
                return true;
            }
        }

        companionPath = string.Empty;
        return false;
    }

    private static bool TryGetGalleryDisplayImagePath(string mediaPath, out string displayPath)
    {
        if (IsRawPhotoFile(mediaPath))
        {
            var previewPath = GalleryPreviewPathFor(mediaPath);
            if (File.Exists(previewPath))
            {
                displayPath = previewPath;
                return true;
            }

            if (TryFindRawCompanionImage(mediaPath, out var companionPath))
            {
                displayPath = companionPath;
                return true;
            }

            displayPath = string.Empty;
            return false;
        }

        displayPath = mediaPath;
        return true;
    }

    private static void TrySaveCurrentFrameGalleryPreview(string mediaPath)
    {
        try
        {
            TrySaveCurrentPreviewFrame(GalleryPreviewPathFor(mediaPath), "jpg");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[GALLERY PREVIEW] " + ex.Message);
        }
    }
}
