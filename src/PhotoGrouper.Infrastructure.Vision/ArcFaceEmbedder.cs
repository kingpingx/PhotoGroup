using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Common;

namespace PhotoGrouper.Infrastructure.Vision;

/// <summary>
/// Turns an aligned face crop into a 512-dimensional vector using ArcFace.
/// </summary>
/// <remarks>
/// The heart of the application. Two vectors from this model are close when they are the same
/// person and far apart when they are not, and every grouping the app performs rests on that
/// property holding.
///
/// It is also the component with the least tolerance for small mistakes. Wrong channel order,
/// wrong normalisation, a mirrored alignment or a skipped unit-length step all produce a
/// well-formed vector of 512 floats that simply does not describe the face. Nothing raises an
/// error; clustering merely stops working, and the cause is invisible from the outside. That is
/// why the preprocessing constants live in the provider's alignment spec rather than being
/// scattered here, and why the tests pin exact values rather than approximate behaviour.
/// </remarks>
public sealed class ArcFaceEmbedder : IFaceEmbedder
{
    public static readonly ModelDescriptor Model = new(
        FileName: "w600k_r50.onnx",
        Url: "https://huggingface.co/deepghs/insightface/resolve/main/buffalo_l/w600k_r50.onnx",
        Sha256: "4C06341C33C2CA1F86781DAB0E829F88AD5B64BE9FBA56E56BC9EBDEFC619E43",
        DisplayName: "ArcFace R50 (WebFace600K)",
        Licence: "InsightFace pretrained models: non-commercial research use only.");

    public static readonly ProviderInfo Provider = new(
        Id: "insightface.arcface.w600k_r50",
        Version: "1",
        DisplayName: "ArcFace R50",
        Notes: "512-dimensional embeddings. Weights are licensed for non-commercial research use only.");

    /// <summary>Length of the vectors this model produces.</summary>
    public const int EmbeddingDimensions = 512;

    /// <summary>
    /// How many crops are sent to the model at once.
    /// </summary>
    /// <remarks>
    /// One, and not by preference. The model declares a dynamic batch dimension, but the DirectML
    /// provider fails inside a batch normalisation node for any batch above one on the hardware
    /// this was developed against, and was unreliable rather than merely slow before failing
    /// outright. Measurement also showed no benefit worth pursuing: batching helps on the CPU
    /// path, which is already the slower of the two.
    ///
    /// The batch shape is kept in the port's contract regardless. It costs nothing here, and an
    /// embedder that can exploit it should not be prevented from doing so by this one's limits.
    /// </remarks>
    private const int GpuBatchSize = 1;

    private const int CpuBatchSize = 8;

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly int _batchSize;
    private readonly object _gate = new();

    private ArcFaceEmbedder(InferenceSession session, bool usesGpu)
    {
        _session = session;
        _inputName = session.InputMetadata.Keys.First();
        _batchSize = usesGpu ? GpuBatchSize : CpuBatchSize;
        UsesGpu = usesGpu;
    }

    public ProviderInfo Info => Provider;

    public int Dimensions => EmbeddingDimensions;

    public AlignmentSpec Alignment => AlignmentSpec.ArcFace112;

    public bool UsesGpu { get; }

    public static ArcFaceEmbedder Load(string modelPath, OnnxSessionFactory sessionFactory)
    {
        ArgumentNullException.ThrowIfNull(sessionFactory);

        var session = sessionFactory.Create(modelPath);
        return new ArcFaceEmbedder(session, sessionFactory.LastSessionUsedGpu);
    }

    public float[][] Embed(IReadOnlyList<ImageBuffer> alignedFaces)
    {
        ArgumentNullException.ThrowIfNull(alignedFaces);

        var results = new float[alignedFaces.Count][];

        for (var start = 0; start < alignedFaces.Count; start += _batchSize)
        {
            var count = Math.Min(_batchSize, alignedFaces.Count - start);
            var tensor = BuildBatch(alignedFaces, start, count);

            lock (_gate)
            {
                using var outputs = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, tensor)]);
                var embeddings = outputs.First().AsTensor<float>();

                for (var i = 0; i < count; i++)
                {
                    var vector = new float[EmbeddingDimensions];
                    for (var d = 0; d < EmbeddingDimensions; d++)
                    {
                        vector[d] = embeddings[i, d];
                    }

                    // Normalised to unit length so that cosine similarity is a plain dot product.
                    // Every comparison downstream assumes this; without it, similarity would track
                    // how brightly lit a face was as much as who it belonged to.
                    Normalize(vector);
                    results[start + i] = vector;
                }
            }
        }

        return results;
    }

    private DenseTensor<float> BuildBatch(IReadOnlyList<ImageBuffer> faces, int start, int count)
    {
        var spec = Alignment;
        var size = spec.Size;
        var tensor = new DenseTensor<float>([count, 3, size, size]);
        var span = tensor.Buffer.Span;
        var planeStride = size * size;

        for (var index = 0; index < count; index++)
        {
            var face = faces[start + index];

            if (face.Width != size || face.Height != size)
            {
                throw new ArgumentException(
                    $"Faces must already be aligned to {size}x{size}; received {face.Width}x{face.Height}.",
                    nameof(faces));
            }

            var pixels = face.Pixels.Span;
            var imageOffset = index * 3 * planeStride;

            for (var y = 0; y < size; y++)
            {
                var row = y * face.Stride;

                for (var x = 0; x < size; x++)
                {
                    var p = row + (x * 3);

                    // The aligned crop is BGR; this model wants RGB. Which byte lands in plane
                    // zero is the whole of the channel-order question, and reversing it produces
                    // vectors that are stable, plausible, and useless for matching people.
                    float first = pixels[p + 2];
                    float second = pixels[p + 1];
                    float third = pixels[p];

                    if (!spec.Rgb)
                    {
                        (first, third) = (third, first);
                    }

                    var offset = imageOffset + (y * size) + x;
                    span[offset] = (first - spec.Mean) / spec.Std;
                    span[offset + planeStride] = (second - spec.Mean) / spec.Std;
                    span[offset + (2 * planeStride)] = (third - spec.Mean) / spec.Std;
                }
            }
        }

        return tensor;
    }

    /// <summary>Scales a vector to unit length in place.</summary>
    internal static void Normalize(float[] vector)
    {
        double sumOfSquares = 0;
        foreach (var value in vector)
        {
            sumOfSquares += (double)value * value;
        }

        if (sumOfSquares <= double.Epsilon)
        {
            return;
        }

        var inverse = (float)(1.0 / Math.Sqrt(sumOfSquares));
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] *= inverse;
        }
    }

    public void Dispose() => _session.Dispose();
}
