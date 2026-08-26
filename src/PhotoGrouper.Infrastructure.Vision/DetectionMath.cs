using PhotoGrouper.Domain.Common;
using PhotoGrouper.Domain.Faces;

namespace PhotoGrouper.Infrastructure.Vision;

/// <summary>
/// The decoding steps every anchor-based face detector needs.
/// </summary>
/// <remarks>
/// Shared between the detectors because the shapes differ but the ideas do not: both lay a grid
/// of anchor points over the image at several strides, predict offsets from those points, and
/// leave the caller to suppress the resulting pile of overlapping boxes. Writing this once means
/// a defect here is found by either detector's tests.
/// </remarks>
public static class DetectionMath
{
    /// <summary>
    /// Anchor centres for one feature stride, in model input pixels.
    /// </summary>
    /// <remarks>
    /// Ordering is row-major, and where a stride carries several anchors per cell they are
    /// consecutive at the same centre. This must match the order the model flattens its output
    /// in; a mismatch pairs every prediction with the wrong anchor and scatters boxes across the
    /// image in a way that looks like a broken model rather than a transposed loop.
    /// </remarks>
    public static Point2[] BuildAnchorCentres(int inputSize, int stride, int anchorsPerCell)
    {
        var cells = inputSize / stride;
        var centres = new Point2[cells * cells * anchorsPerCell];

        var index = 0;
        for (var row = 0; row < cells; row++)
        {
            for (var column = 0; column < cells; column++)
            {
                var x = column * stride;
                var y = row * stride;

                for (var anchor = 0; anchor < anchorsPerCell; anchor++)
                {
                    centres[index++] = new Point2(x, y);
                }
            }
        }

        return centres;
    }

    /// <summary>
    /// Converts distances from an anchor centre to its four edges into a box.
    /// </summary>
    /// <remarks>
    /// The representation SCRFD predicts in: rather than a centre and a size, each prediction
    /// gives how far the anchor sits from the left, top, right and bottom of the face. Values
    /// arrive already multiplied by the stride.
    /// </remarks>
    public static FaceBox DistanceToBox(Point2 centre, float left, float top, float right, float bottom, float score)
    {
        var x1 = centre.X - left;
        var y1 = centre.Y - top;
        var x2 = centre.X + right;
        var y2 = centre.Y + bottom;

        return new FaceBox(x1, y1, x2 - x1, y2 - y1, score);
    }

    /// <summary>Converts five landmark offsets from an anchor centre into absolute points.</summary>
    public static FaceLandmarks DistanceToLandmarks(Point2 centre, ReadOnlySpan<float> offsets)
    {
        if (offsets.Length < FaceLandmarks.ValueCount)
        {
            throw new ArgumentException(
                $"Expected {FaceLandmarks.ValueCount} landmark offsets.", nameof(offsets));
        }

        Span<float> absolute = stackalloc float[FaceLandmarks.ValueCount];
        for (var i = 0; i < FaceLandmarks.ValueCount; i += 2)
        {
            absolute[i] = centre.X + offsets[i];
            absolute[i + 1] = centre.Y + offsets[i + 1];
        }

        return FaceLandmarks.FromFloats(absolute);
    }

    /// <summary>
    /// Keeps the strongest of each group of overlapping boxes.
    /// </summary>
    /// <remarks>
    /// A detector fires on every anchor near a face, so one face typically yields a dozen boxes
    /// that differ only slightly. Without suppression a single person would be stored as a dozen
    /// faces, each embedded and clustered separately, and the People page would fill with
    /// duplicates of the same person.
    ///
    /// Implemented directly rather than through OpenCV's helper so it stays available to any
    /// detector regardless of which inference stack it uses.
    /// </remarks>
    public static List<int> NonMaximumSuppression(
        IReadOnlyList<FaceBox> boxes, float overlapThreshold, int maximumKept = 5000)
    {
        var order = Enumerable.Range(0, boxes.Count)
            .OrderByDescending(i => boxes[i].Score)
            .ToArray();

        var suppressed = new bool[boxes.Count];
        var kept = new List<int>();

        foreach (var candidate in order)
        {
            if (suppressed[candidate])
            {
                continue;
            }

            kept.Add(candidate);
            if (kept.Count >= maximumKept)
            {
                break;
            }

            foreach (var other in order)
            {
                if (!suppressed[other]
                    && other != candidate
                    && boxes[candidate].IntersectionOverUnion(boxes[other]) > overlapThreshold)
                {
                    suppressed[other] = true;
                }
            }
        }

        return kept;
    }
}
