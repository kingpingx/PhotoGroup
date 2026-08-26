using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Common;
using PhotoGrouper.Domain.Faces;

namespace PhotoGrouper.Infrastructure.Vision;

/// <summary>Adapts the OpenCV-backed aligner to the application's port.</summary>
public sealed class OpenCvFaceAligner : IFaceAligner
{
    public ImageBuffer Align(ImageBuffer image, FaceLandmarks landmarks, AlignmentSpec spec) =>
        FaceAligner.Align(image, landmarks, spec);

    public float MeasureSharpness(ImageBuffer alignedFace) =>
        FaceAligner.MeasureSharpness(alignedFace);
}
