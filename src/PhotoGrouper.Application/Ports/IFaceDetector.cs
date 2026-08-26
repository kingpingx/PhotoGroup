using PhotoGrouper.Domain.Common;
using PhotoGrouper.Domain.Faces;

namespace PhotoGrouper.Application.Ports;

/// <summary>
/// Finds faces in an image.
/// </summary>
/// <remarks>
/// Takes an <see cref="ImageBuffer"/> rather than any library's image type. A port typed in
/// terms of OpenCV's Mat would pull a native imaging library into the application layer and
/// make every use case that touches detection untestable without it.
///
/// Implementations must return landmarks in the canonical order documented on
/// <see cref="FaceLandmarks"/>, reordering from whatever their model emits. That normalisation
/// belongs here, at the adapter boundary, because a wrong order produces embeddings that are
/// silently useless rather than an error anyone would notice.
/// </remarks>
public interface IFaceDetector : IDisposable
{
    ProviderInfo Info { get; }

    /// <summary>
    /// Detects faces in an upright image.
    /// </summary>
    /// <remarks>
    /// The caller is responsible for having applied EXIF orientation. Detecting on unrotated
    /// pixels finds nothing useful in a portrait phone photo, since the faces are sideways.
    /// </remarks>
    IReadOnlyList<DetectedFace> Detect(ImageBuffer image);
}

/// <summary>Converts an aligned face crop into a comparable vector.</summary>
/// <remarks>
/// Registered separately from detectors, and paired with one only at the point of use. Keeping
/// them independent is what allows the detector to change without re-embedding, and a custom
/// embedder to be added without touching detection.
/// </remarks>
public interface IFaceEmbedder : IDisposable
{
    ProviderInfo Info { get; }

    int Dimensions { get; }

    /// <summary>The template the caller must align crops to before calling <see cref="Embed"/>.</summary>
    AlignmentSpec Alignment { get; }

    /// <summary>
    /// Embeds a batch of aligned crops.
    /// </summary>
    /// <remarks>
    /// Batched in the contract even though an implementation may loop internally. Inference on
    /// a GPU is dominated by per-call overhead at this size, so a one-at-a-time API would leave
    /// most of the hardware idle and there would be no way for a caller to do better.
    /// </remarks>
    float[][] Embed(IReadOnlyList<ImageBuffer> alignedFaces);
}

/// <summary>Identity and behaviour of a detector or embedder.</summary>
/// <param name="Id">Stable identifier, persisted against every face and vector it produces.</param>
/// <param name="Version">Bumped when output changes enough to invalidate what is already stored.</param>
/// <param name="DisplayName">What the settings screen shows.</param>
/// <param name="Notes">Anything the user should know before choosing it, such as licence terms.</param>
public sealed record ProviderInfo(string Id, string Version, string DisplayName, string? Notes = null);

/// <summary>
/// How an embedder expects its input to be prepared.
/// </summary>
/// <remarks>
/// Every value here is a way to get embeddings badly wrong without producing an error. Wrong
/// channel order, wrong normalisation, or a mismatched template all yield a perfectly
/// well-formed vector that simply does not describe the face. Making them explicit data on the
/// provider, rather than constants buried in an adapter, is what allows a custom embedder to
/// declare its own and be aligned correctly by shared code.
/// </remarks>
/// <param name="Size">Side length of the square crop, in pixels.</param>
/// <param name="ReferencePoints">Where the five landmarks must land, in canonical order.</param>
/// <param name="Mean">Subtracted from each channel before scaling.</param>
/// <param name="Std">Each channel is divided by this after the mean is subtracted.</param>
/// <param name="Rgb">True when channels must be RGB; false for BGR.</param>
public sealed record AlignmentSpec(
    int Size,
    Point2[] ReferencePoints,
    float Mean,
    float Std,
    bool Rgb)
{
    /// <summary>
    /// The ArcFace 112 by 112 template.
    /// </summary>
    /// <remarks>
    /// These specific numbers are the de facto standard for ArcFace-family models. The weights
    /// were trained on crops aligned to them, so departing from them degrades accuracy without
    /// any visible sign that anything is wrong.
    /// </remarks>
    public static AlignmentSpec ArcFace112 { get; } = new(
        Size: 112,
        ReferencePoints:
        [
            new Point2(38.2946f, 51.6963f),
            new Point2(73.5318f, 51.5014f),
            new Point2(56.0252f, 71.7366f),
            new Point2(41.5493f, 92.3655f),
            new Point2(70.7299f, 92.2041f),
        ],
        Mean: 127.5f,
        Std: 127.5f,
        Rgb: true);
}
