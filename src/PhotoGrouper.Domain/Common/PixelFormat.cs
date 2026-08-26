namespace PhotoGrouper.Domain.Common;

/// <summary>Byte layout of the pixels inside an <see cref="ImageBuffer"/>.</summary>
public enum PixelFormat
{
    /// <summary>Three bytes per pixel, ordered blue, green, red. OpenCV's native order.</summary>
    Bgr24,

    /// <summary>Three bytes per pixel, ordered red, green, blue. What the ONNX models expect.</summary>
    Rgb24,

    /// <summary>One byte per pixel.</summary>
    Gray8,
}
