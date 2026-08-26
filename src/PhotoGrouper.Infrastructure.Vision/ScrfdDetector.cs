using Microsoft.ML.OnnxRuntime;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Common;
using PhotoGrouper.Domain.Faces;

namespace PhotoGrouper.Infrastructure.Vision;

/// <summary>
/// Face detection with SCRFD-10GF, the detector shipped alongside ArcFace in InsightFace's
/// buffalo_l pack.
/// </summary>
/// <remarks>
/// Substantially better than YuNet at small and turned-away faces, and correspondingly slower:
/// measured on this project's reference hardware at roughly thirty-five milliseconds per image
/// against YuNet's eight, both on the GPU.
///
/// The model emits nine tensors, three per feature stride: a confidence score per anchor, four
/// distances describing the box, and ten describing the landmarks. None of it is usable until
/// it is paired with the right anchor centre and mapped out of letterboxed space, which is what
/// this class exists to do.
/// </remarks>
public sealed class ScrfdDetector : IFaceDetector
{
    public static readonly ModelDescriptor Model = new(
        FileName: "det_10g.onnx",
        Url: "https://huggingface.co/deepghs/insightface/resolve/main/buffalo_l/det_10g.onnx",
        Sha256: "5838F7FE053675B1C7A08B633DF49E7AF5495CEE0493C7DCF6697200B85B5B91",
        DisplayName: "SCRFD-10GF face detector",
        Licence: "InsightFace pretrained models: non-commercial research use only.");

    public static readonly ProviderInfo Provider = new(
        Id: "insightface.scrfd.10g",
        Version: "1",
        DisplayName: "SCRFD-10GF",
        Notes: "Best recall on small and profile faces. Roughly four times slower than YuNet. "
               + "Weights are licensed for non-commercial research use only.");

    /// <summary>
    /// The only input size this detector runs at.
    /// </summary>
    /// <remarks>
    /// The model declares a dynamic input, but DirectML fails inside a reshape when given
    /// anything other than 640 by 640, so the size is fixed rather than merely defaulted.
    /// Treating it as adjustable would produce a runtime fault on the GPU path only.
    /// </remarks>
    public const int InputSize = 640;

    private static readonly int[] Strides = [8, 16, 32];
    private const int AnchorsPerCell = 2;
    private const int OutputsPerKind = 3;

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string[] _outputNames;
    private readonly float _scoreThreshold;
    private readonly float _nmsThreshold;
    private readonly Dictionary<int, Point2[]> _anchorCache = [];
    private readonly object _gate = new();

    private ScrfdDetector(InferenceSession session, float scoreThreshold, float nmsThreshold)
    {
        _session = session;
        _scoreThreshold = scoreThreshold;
        _nmsThreshold = nmsThreshold;

        _inputName = session.InputMetadata.Keys.First();
        _outputNames = [.. session.OutputMetadata.Keys];

        if (_outputNames.Length != OutputsPerKind * 3)
        {
            throw new InvalidOperationException(
                $"Expected {OutputsPerKind * 3} outputs from SCRFD but the model declares {_outputNames.Length}. "
                + "This adapter is written for the 10GF variant with keypoints.");
        }

        foreach (var stride in Strides)
        {
            var cells = InputSize / stride;
            _anchorCache[stride] = DetectionMath.BuildAnchorCentres(InputSize, stride, AnchorsPerCell);
            _ = cells;
        }
    }

    public ProviderInfo Info => Provider;

    public bool UsesGpu { get; private init; }

    /// <param name="scoreThreshold">
    /// Deliberately below the quality gate applied afterwards, so marginal detections reach the
    /// gate where they can be counted and explained rather than disappearing inside the model.
    /// </param>
    public static ScrfdDetector Load(
        string modelPath,
        OnnxSessionFactory sessionFactory,
        float scoreThreshold = 0.5f,
        float nmsThreshold = 0.4f)
    {
        ArgumentNullException.ThrowIfNull(sessionFactory);

        var session = sessionFactory.Create(modelPath);
        return new ScrfdDetector(session, scoreThreshold, nmsThreshold)
        {
            UsesGpu = sessionFactory.LastSessionUsedGpu,
        };
    }

    public IReadOnlyList<DetectedFace> Detect(ImageBuffer image)
    {
        ArgumentNullException.ThrowIfNull(image);

        var letterbox = Letterbox.Fit(image.Width, image.Height, InputSize);
        var tensor = TensorPreprocessor.ToNchw(image, letterbox, PreprocessSpec.Scrfd);

        var boxes = new List<FaceBox>();
        var landmarks = new List<FaceLandmarks>();

        // An InferenceSession is documented as thread-safe for Run, but the DirectML provider
        // serialises internally anyway and the pipeline batches through a single inference
        // thread. Holding the lock keeps behaviour identical on both providers.
        lock (_gate)
        {
            using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, tensor)]);
            var outputs = results.ToArray();

            for (var level = 0; level < Strides.Length; level++)
            {
                var stride = Strides[level];
                var scores = outputs[level].AsTensor<float>();
                var boxDeltas = outputs[level + OutputsPerKind].AsTensor<float>();
                var kpsDeltas = outputs[level + (OutputsPerKind * 2)].AsTensor<float>();
                var centres = _anchorCache[stride];

                DecodeLevel(stride, centres, scores, boxDeltas, kpsDeltas, boxes, landmarks);
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
        Point2[] centres,
        Microsoft.ML.OnnxRuntime.Tensors.Tensor<float> scores,
        Microsoft.ML.OnnxRuntime.Tensors.Tensor<float> boxDeltas,
        Microsoft.ML.OnnxRuntime.Tensors.Tensor<float> kpsDeltas,
        List<FaceBox> boxes,
        List<FaceLandmarks> landmarks)
    {
        var count = Math.Min(centres.Length, (int)scores.Length);
        Span<float> offsets = stackalloc float[FaceLandmarks.ValueCount];

        for (var i = 0; i < count; i++)
        {
            var score = scores.GetValue(i);
            if (score < _scoreThreshold)
            {
                continue;
            }

            var centre = centres[i];
            var b = i * 4;

            // Predictions are in units of the feature stride, not pixels. Omitting this scaling
            // yields boxes correctly centred but a fraction of their true size, which looks like
            // the detector finding only the middle of every face.
            boxes.Add(DetectionMath.DistanceToBox(
                centre,
                boxDeltas.GetValue(b) * stride,
                boxDeltas.GetValue(b + 1) * stride,
                boxDeltas.GetValue(b + 2) * stride,
                boxDeltas.GetValue(b + 3) * stride,
                score));

            var k = i * FaceLandmarks.ValueCount;
            for (var j = 0; j < FaceLandmarks.ValueCount; j++)
            {
                offsets[j] = kpsDeltas.GetValue(k + j) * stride;
            }

            landmarks.Add(DetectionMath.DistanceToLandmarks(centre, offsets));
        }
    }

    public void Dispose() => _session.Dispose();
}
