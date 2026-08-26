using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Common;
using PhotoGrouper.Domain.Faces;

namespace PhotoGrouper.Infrastructure.Vision;

/// <summary>
/// Face detection with YuNet, a very small model from the OpenCV Zoo.
/// </summary>
/// <remarks>
/// Roughly four times faster than SCRFD on this project's reference hardware, at eight
/// milliseconds per image against thirty-five, and permissively licensed. It pays for that by
/// missing more of the small and turned-away faces in a crowded photo.
///
/// Run through ONNX Runtime rather than OpenCV. OpenCvSharp does not wrap OpenCV's own
/// FaceDetectorYN class — only the legacy Eigen and LBPH recognisers — so the convenience that
/// would have justified a second inference stack does not exist, and using one stack for both
/// detectors means the DirectML fallback and warm-up behave identically for each.
///
/// Its output differs from SCRFD's in three ways that all matter: scores come as separate
/// classification and objectness tensors that must be combined, boxes are centre and size
/// rather than edge distances, and offsets are in grid cells rather than pixels.
/// </remarks>
public sealed class YuNetDetector : IFaceDetector
{
    public static readonly ModelDescriptor Model = new(
        FileName: "face_detection_yunet_2023mar.onnx",
        Url: "https://github.com/opencv/opencv_zoo/raw/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx",
        Sha256: "8F2383E4DD3CFBB4553EA8718107FC0423210DC964F9F4280604804ED2552FA4",
        DisplayName: "YuNet face detector",
        Licence: "Apache-2.0 (OpenCV Zoo)");

    public static readonly ProviderInfo Provider = new(
        Id: "opencv.yunet.2023mar",
        Version: "1",
        DisplayName: "YuNet",
        Notes: "Fast and permissively licensed. Misses more small and turned-away faces than SCRFD.");

    /// <summary>The model declares a fixed input of this size; it is not adjustable.</summary>
    public const int InputSize = 640;

    private static readonly int[] Strides = [8, 16, 32];

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly float _scoreThreshold;
    private readonly float _nmsThreshold;
    private readonly object _gate = new();

    private YuNetDetector(InferenceSession session, float scoreThreshold, float nmsThreshold)
    {
        _session = session;
        _scoreThreshold = scoreThreshold;
        _nmsThreshold = nmsThreshold;
        _inputName = session.InputMetadata.Keys.First();

        foreach (var stride in Strides)
        {
            foreach (var prefix in new[] { "cls", "obj", "bbox", "kps" })
            {
                var name = $"{prefix}_{stride}";
                if (!session.OutputMetadata.ContainsKey(name))
                {
                    throw new InvalidOperationException(
                        $"The model does not declare an output named '{name}'. "
                        + "This adapter is written for face_detection_yunet_2023mar.");
                }
            }
        }
    }

    public ProviderInfo Info => Provider;

    public bool UsesGpu { get; private init; }

    public static YuNetDetector Load(
        string modelPath,
        OnnxSessionFactory sessionFactory,
        float scoreThreshold = 0.5f,
        float nmsThreshold = 0.3f)
    {
        ArgumentNullException.ThrowIfNull(sessionFactory);

        var session = sessionFactory.Create(modelPath);
        return new YuNetDetector(session, scoreThreshold, nmsThreshold)
        {
            UsesGpu = sessionFactory.LastSessionUsedGpu,
        };
    }

    public IReadOnlyList<DetectedFace> Detect(ImageBuffer image)
    {
        ArgumentNullException.ThrowIfNull(image);

        var letterbox = Letterbox.Fit(image.Width, image.Height, InputSize);
        var tensor = TensorPreprocessor.ToNchw(image, letterbox, PreprocessSpec.YuNet);

        var boxes = new List<FaceBox>();
        var landmarks = new List<FaceLandmarks>();

        lock (_gate)
        {
            using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, tensor)]);
            var byName = results.ToDictionary(r => r.Name, r => r.AsTensor<float>());

            foreach (var stride in Strides)
            {
                DecodeLevel(
                    stride,
                    byName[$"cls_{stride}"],
                    byName[$"obj_{stride}"],
                    byName[$"bbox_{stride}"],
                    byName[$"kps_{stride}"],
                    boxes,
                    landmarks);
            }
        }

        if (boxes.Count == 0)
        {
            return [];
        }

        var kept = DetectionMath.NonMaximumSuppression(boxes, _nmsThreshold);
        var faces = new List<DetectedFace>(kept.Count);

        foreach (var index in kept)
        {
            faces.Add(new DetectedFace(
                letterbox.ToSource(boxes[index]),
                letterbox.ToSource(landmarks[index])));
        }

        return faces;
    }

    private void DecodeLevel(
        int stride,
        Tensor<float> cls,
        Tensor<float> obj,
        Tensor<float> bbox,
        Tensor<float> kps,
        List<FaceBox> boxes,
        List<FaceLandmarks> landmarks)
    {
        var cells = InputSize / stride;
        var count = cells * cells;

        Span<float> points = stackalloc float[FaceLandmarks.ValueCount];

        for (var i = 0; i < count; i++)
        {
            // Two heads: one says "this is a face", the other "there is an object here at all".
            // The geometric mean is what OpenCV's own implementation uses; taking either alone
            // produces far too many detections at one threshold and far too few at another.
            var classification = Math.Clamp(cls.GetValue(i), 0f, 1f);
            var objectness = Math.Clamp(obj.GetValue(i), 0f, 1f);
            var score = MathF.Sqrt(classification * objectness);

            if (score < _scoreThreshold)
            {
                continue;
            }

            var column = i % cells;
            var row = i / cells;
            var b = i * 4;

            // Offsets are fractions of a grid cell, added to the cell index before scaling by
            // the stride. Sizes are logarithmic, so the exponential is not optional.
            var centreX = (column + bbox.GetValue(b)) * stride;
            var centreY = (row + bbox.GetValue(b + 1)) * stride;
            var width = MathF.Exp(bbox.GetValue(b + 2)) * stride;
            var height = MathF.Exp(bbox.GetValue(b + 3)) * stride;

            boxes.Add(new FaceBox(
                centreX - (width / 2f),
                centreY - (height / 2f),
                width,
                height,
                score));

            var k = i * FaceLandmarks.ValueCount;
            for (var j = 0; j < FaceLandmarks.ValueCount; j += 2)
            {
                points[j] = (column + kps.GetValue(k + j)) * stride;
                points[j + 1] = (row + kps.GetValue(k + j + 1)) * stride;
            }

            landmarks.Add(ReadLandmarks(points));
        }
    }

    /// <summary>
    /// Maps YuNet's landmark order onto the application's canonical one.
    /// </summary>
    /// <remarks>
    /// YuNet emits the subject's right eye first, then their left. The subject's right eye is
    /// the one on the viewer's left, which is where this application's LeftEye goes: the two
    /// names describe the same point from opposite viewpoints. Reading them in the order given
    /// would mirror every alignment, producing embeddings that are well-formed and wrong.
    ///
    /// Internal so the mapping can be pinned by a test using a synthetic row, without needing a
    /// photograph of a real person as a fixture.
    /// </remarks>
    internal static FaceLandmarks ReadLandmarks(ReadOnlySpan<float> points) => new(
        LeftEye: new Point2(points[0], points[1]),
        RightEye: new Point2(points[2], points[3]),
        Nose: new Point2(points[4], points[5]),
        MouthLeft: new Point2(points[6], points[7]),
        MouthRight: new Point2(points[8], points[9]));

    public void Dispose() => _session.Dispose();
}
