using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using PhotoGrouper.Domain.Common;
using PhotoGrouper.Infrastructure.Imaging;

namespace PhotoGrouper.Infrastructure.Vision;

/// <summary>How a model expects its input pixels prepared.</summary>
/// <param name="Mean">Subtracted from every channel value before scaling.</param>
/// <param name="Std">Every channel value is divided by this after the mean is subtracted.</param>
/// <param name="SwapToRgb">True when the model wants RGB; decoded buffers are BGR.</param>
public readonly record struct PreprocessSpec(float Mean, float Std, bool SwapToRgb)
{
    /// <summary>SCRFD and the ArcFace family: RGB, centred on 127.5.</summary>
    public static readonly PreprocessSpec Scrfd = new(Mean: 127.5f, Std: 128f, SwapToRgb: true);

    /// <summary>YuNet: raw BGR byte values, with no normalisation at all.</summary>
    /// <remarks>
    /// Genuinely different from every other model here, and worth stating explicitly. Applying
    /// the usual normalisation would feed values near zero into a network expecting zero to
    /// two hundred and fifty five, which returns confident nonsense rather than an error.
    /// </remarks>
    public static readonly PreprocessSpec YuNet = new(Mean: 0f, Std: 1f, SwapToRgb: false);
}

/// <summary>Turns decoded pixels into the tensor a detector expects.</summary>
public static class TensorPreprocessor
{
    /// <summary>
    /// Letterboxes, normalises and lays out an image as a single-batch NCHW tensor.
    /// </summary>
    /// <remarks>
    /// NCHW means all of the first channel, then all of the second, then the third, rather than
    /// the interleaved layout the decoded image uses. Feeding interleaved data to a model
    /// expecting planar produces an image the network reads as noise, and again returns a
    /// well-formed result rather than failing.
    /// </remarks>
    public static DenseTensor<float> ToNchw(ImageBuffer image, Letterbox letterbox, PreprocessSpec spec)
    {
        ArgumentNullException.ThrowIfNull(image);

        var size = letterbox.TargetSize;
        var tensor = new DenseTensor<float>([1, 3, size, size]);

        using var source = MatBridge.ToMat(image);
        using var resized = new Mat();
        Cv2.Resize(
            source,
            resized,
            new OpenCvSharp.Size(letterbox.ScaledWidth, letterbox.ScaledHeight),
            interpolation: InterpolationFlags.Linear);

        // The padded region is left at zero. Every value below writes only inside the scaled
        // area, so padding is implicitly black, which is what the models were trained to ignore.
        var buffer = MatBridge.ToImageBuffer(resized);
        var pixels = buffer.Pixels.Span;

        var planeStride = size * size;
        var mean = spec.Mean;
        var std = spec.Std;

        for (var y = 0; y < buffer.Height; y++)
        {
            var rowStart = y * buffer.Stride;
            var rowOffset = y * size;

            for (var x = 0; x < buffer.Width; x++)
            {
                var p = rowStart + (x * 3);

                // The decoded buffer is BGR. Which of these lands in plane zero is the whole of
                // the channel-order question, and getting it backwards is the single easiest way
                // to produce embeddings that are subtly and permanently wrong.
                float first = pixels[p + 2];
                float second = pixels[p + 1];
                float third = pixels[p];

                if (!spec.SwapToRgb)
                {
                    (first, third) = (third, first);
                }

                var index = rowOffset + x;
                tensor.Buffer.Span[index] = (first - mean) / std;
                tensor.Buffer.Span[planeStride + index] = (second - mean) / std;
                tensor.Buffer.Span[(2 * planeStride) + index] = (third - mean) / std;
            }
        }

        return tensor;
    }
}
