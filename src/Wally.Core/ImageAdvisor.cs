namespace Wally.Core;

/// <summary>Quality verdict for an image shown on a monitor at a given fit.</summary>
public enum ImageQuality
{
    /// <summary>Image meets or exceeds the monitor resolution.</summary>
    Good,

    /// <summary>Noticeably below native; will look soft when scaled up.</summary>
    Soft,

    /// <summary>Far below native; will look blurry/blocky.</summary>
    Poor,
}

/// <summary>
/// Pure helper: judge whether an image is high enough resolution to look decent
/// on a target monitor. No Windows calls — the UI reads the image's pixel size
/// and passes it here.
/// </summary>
public static class ImageAdvisor
{
    /// <summary>
    /// Compare image pixels to monitor pixels along the limiting dimension.
    /// A fill/stretch scales the image to cover the screen, so quality is driven
    /// by the smaller of the width/height ratios.
    /// </summary>
    public static ImageQuality Assess(int imageWidth, int imageHeight, int monitorWidth, int monitorHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || monitorWidth <= 0 || monitorHeight <= 0)
        {
            return ImageQuality.Poor;
        }

        var widthRatio = (double)imageWidth / monitorWidth;
        var heightRatio = (double)imageHeight / monitorHeight;
        var limiting = Math.Min(widthRatio, heightRatio);

        if (limiting >= 0.95)
        {
            return ImageQuality.Good;
        }

        if (limiting >= 0.6)
        {
            return ImageQuality.Soft;
        }

        return ImageQuality.Poor;
    }

    /// <summary>Human-readable advice line for a quality verdict, or null when Good.</summary>
    public static string? Advice(ImageQuality quality, int imageWidth, int imageHeight, int monitorWidth, int monitorHeight)
        => quality switch
        {
            ImageQuality.Soft =>
                $"{imageWidth}×{imageHeight} on a {monitorWidth}×{monitorHeight} screen — slightly soft.",
            ImageQuality.Poor =>
                $"{imageWidth}×{imageHeight} on a {monitorWidth}×{monitorHeight} screen — too low, will look blurry.",
            _ => null,
        };
}
