using OpenCvSharp;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Common;
using PhotoGrouper.Domain.Faces;
using PhotoGrouper.Infrastructure.Imaging;

namespace PhotoGrouper.Infrastructure.Vision;

/// <summary>
/// Cuts a face out of a photograph and squares it up for an embedder.
/// </summary>
/// <remarks>
/// Not a crop. A plain crop of the bounding box leaves the face at whatever angle and position it
/// happened to occupy, and an embedder trained on aligned faces will read two photographs of the
/// same person, tilted differently, as two different people. Warping the landmarks onto a fixed
/// template removes pose from the comparison so that identity is what remains.
///
/// The single transform does the rotating, scaling, translating and cropping together, which
/// matters for quality as well as speed: doing them in stages resamples the pixels several times
/// and softens exactly the fine detail an embedder depends on.
/// </remarks>
public static class FaceAligner
{
    /// <summary>
    /// Produces the aligned crop for one face.
    /// </summary>
    /// <param name="image">The full photograph, upright.</param>
    /// <param name="landmarks">The five points, in this application's canonical order.</param>
    /// <param name="spec">The template and size the embedder expects.</param>
    public static ImageBuffer Align(ImageBuffer image, FaceLandmarks landmarks, AlignmentSpec spec)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(spec);

        var transform = SimilarityTransform.Solve(landmarks.ToArray(), spec.ReferencePoints);

        using var source = MatBridge.ToMat(image);
        // Built from a rectangular array so the result is unambiguously a 2x3 matrix of doubles.
        // Flattening to a vector and reshaping produces a Mat that OpenCV rejects, because the
        // channel count rather than the column count ends up carrying the width.
        using var affine = Mat.FromArray(transform.ToAffineMatrix());
        using var aligned = new Mat();

        Cv2.WarpAffine(
            source,
            aligned,
            affine,
            new OpenCvSharp.Size(spec.Size, spec.Size),
            // Replicating the edge pixels rather than filling with black, because a face near the
            // border of a photograph would otherwise acquire a hard black band that the embedder
            // reads as part of the person.
            flags: InterpolationFlags.Linear,
            borderMode: BorderTypes.Replicate);

        return MatBridge.ToImageBuffer(aligned);
    }

    /// <summary>
    /// Measures how sharp an aligned crop is, as the variance of its Laplacian.
    /// </summary>
    /// <remarks>
    /// A blurred face still yields a confident detection and a well-formed embedding, but one that
    /// sits closer to other blurred faces than to sharp photographs of the same person. Blur is
    /// therefore a quiet source of wrong groupings, and measuring it lets the quality gate exclude
    /// the worst before they reach clustering.
    ///
    /// The value has no absolute meaning; it scales with contrast and resolution. It is comparable
    /// only between crops of the same size, which is exactly how it is used here.
    /// </remarks>
    public static float MeasureSharpness(ImageBuffer alignedFace)
    {
        ArgumentNullException.ThrowIfNull(alignedFace);

        using var mat = MatBridge.ToMat(alignedFace);
        using var grey = new Mat();
        Cv2.CvtColor(mat, grey, ColorConversionCodes.BGR2GRAY);

        using var laplacian = new Mat();
        Cv2.Laplacian(grey, laplacian, MatType.CV_64F);

        Cv2.MeanStdDev(laplacian, out _, out var deviation);
        var standardDeviation = deviation.Val0;

        return (float)(standardDeviation * standardDeviation);
    }
}
