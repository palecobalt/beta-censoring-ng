namespace CensorCore;

/// <summary>
/// The matches found in an image by the model, without censoring it.
/// </summary>
public class DetectionResult
{
    public DetectionResult(int width, int height, List<Classification> results)
    {
        Width = width;
        Height = height;
        Results = results;
    }

    /// <summary>
    /// Width of the image in pixels. Match boxes use the image's pixel coordinates.
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// Height of the image in pixels.
    /// </summary>
    public int Height { get; }

    public List<Classification> Results { get; }
}
