using PhotoGrouper.Domain.Common;
using PhotoGrouper.Domain.Faces;

namespace PhotoGrouper.Application.Ports;

/// <summary>Cuts a face out of a photograph and squares it up for an embedder.</summary>
/// <remarks>
/// A port because the implementation needs image warping, which means a native imaging library
/// that has no business being visible to a use case. Expressed in terms of the domain's pixel
/// buffer so the boundary holds.
/// </remarks>
public interface IFaceAligner
{
    /// <param name="image">The photograph, upright.</param>
    /// <param name="landmarks">The five points, in this application's canonical order.</param>
    /// <param name="spec">The template and size the target embedder expects.</param>
    ImageBuffer Align(ImageBuffer image, FaceLandmarks landmarks, AlignmentSpec spec);

    /// <summary>Sharpness of an aligned crop; higher is sharper.</summary>
    float MeasureSharpness(ImageBuffer alignedFace);
}
