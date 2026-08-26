using System.Threading.Channels;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Faces;
using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.Photos;

namespace PhotoGrouper.Application.UseCases;

/// <summary>
/// Runs face detection over every photo that has not yet been through it.
/// </summary>
/// <remarks>
/// Structured as a bounded producer and consumer for one reason: decoding and inference have
/// very different shapes. Decoding a photo is processor-bound and embarrassingly parallel;
/// inference is a single device that wants work handed to it steadily. Running them in lockstep
/// leaves whichever is faster idle, and running decode unbounded fills memory with decoded
/// images faster than the detector can consume them.
///
/// Every photo's result is committed as it completes, and the photo's state advances with it, so
/// an interrupted run resumes from where it stopped rather than starting the library again.
/// </remarks>
public sealed class DetectFacesUseCase(
    IPhotoReader photos,
    IPhotoWriter photoWriter,
    IFaceRepository faces,
    IImageDecoder decoder,
    IThumbnailCache thumbnails)
{
    /// <summary>
    /// Long edge the image is reduced to before detection.
    /// </summary>
    /// <remarks>
    /// Detectors run at a fixed 640 pixel input, so anything beyond roughly twice that is
    /// discarded by the letterbox anyway. Decoding a twelve megapixel photo at full size to
    /// throw away ninety percent of it is the most expensive way to get the same answer.
    /// </remarks>
    public const int DetectionLongEdge = 1600;

    private const int PhotosPerBatch = 200;

    public async Task<DetectionResult> ExecuteAsync(
        IFaceDetector detector,
        FaceQuality quality,
        IProgressSink progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(detector);

        var detectorId = detector.Info.Id;
        var detectorVersion = detector.Info.Version;

        var processed = 0;
        var facesFound = 0;
        var rejected = 0;
        var failed = 0;

        while (!ct.IsCancellationRequested)
        {
            var batch = await photos.GetByStateAsync(PhotoState.New, PhotosPerBatch, ct).ConfigureAwait(false);
            if (batch.Count == 0)
            {
                break;
            }

            // Bounded so that decode cannot run ahead of detection. Each queued item holds a
            // decoded image, so an unbounded channel would let a fast disk turn into hundreds of
            // megabytes of pending work.
            var channel = Channel.CreateBounded<DecodedWork>(new BoundedChannelOptions(Environment.ProcessorCount)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

            var producer = DecodeAllAsync(batch, channel.Writer, ct);

            await foreach (var work in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();

                if (work.Image is null)
                {
                    // Recorded against the photo rather than retried. A file that will not decode
                    // now will not decode on the next run either, and retrying it forever would
                    // make every subsequent scan pay for it.
                    await photoWriter
                        .SetStateAsync(work.Photo.Id, PhotoState.Failed, work.Error ?? "Could not decode image.", ct)
                        .ConfigureAwait(false);
                    failed++;
                }
                else
                {
                    var outcome = await DetectOneAsync(
                        work, detector, detectorId, detectorVersion, quality, ct).ConfigureAwait(false);

                    facesFound += outcome.Kept;
                    rejected += outcome.Rejected;
                }

                processed++;
                progress.Report(new ProgressUpdate("Detecting faces", processed, null, work.Photo.Path));
            }

            await producer.ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        progress.Report(new ProgressUpdate("Detecting faces", processed, processed));
        return new DetectionResult(processed, facesFound, rejected, failed);
    }

    private async Task DecodeAllAsync(
        IReadOnlyList<Photo> batch, ChannelWriter<DecodedWork> writer, CancellationToken ct)
    {
        try
        {
            await Parallel.ForEachAsync(
                batch,
                new ParallelOptions
                {
                    // One core is left for the consumer, which is doing inference and database
                    // writes. Saturating every core with decode work starves it.
                    MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
                    CancellationToken = ct,
                },
                async (photo, token) =>
                {
                    try
                    {
                        var decoded = await decoder
                            .DecodeAsync(photo.Path, DetectionLongEdge, token)
                            .ConfigureAwait(false);

                        // Read on the decode workers rather than the single consumer thread,
                        // because parsing EXIF means opening the file again and that cost belongs
                        // where there is parallelism to absorb it.
                        var metadata = decoded is null
                            ? null
                            : await decoder.ReadMetadataAsync(photo.Path, token).ConfigureAwait(false);

                        await writer
                            .WriteAsync(new DecodedWork(photo, decoded, metadata), token)
                            .ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        await writer
                            .WriteAsync(new DecodedWork(photo, null, null, e.Message), token)
                            .ConfigureAwait(false);
                    }
                }).ConfigureAwait(false);
        }
        finally
        {
            writer.Complete();
        }
    }

    private async Task<(int Kept, int Rejected)> DetectOneAsync(
        DecodedWork work,
        IFaceDetector detector,
        string detectorId,
        string detectorVersion,
        FaceQuality quality,
        CancellationToken ct)
    {
        var decoded = work.Image!;
        var detected = detector.Detect(decoded.Buffer);

        var kept = new List<Face>(detected.Count);
        var rejected = 0;

        foreach (var face in detected)
        {
            // Detection ran on a reduced image, so every coordinate must be scaled back before
            // being stored. Skipping this stores boxes that are correct in shape but a fraction
            // of the size, sitting near the top-left corner of the photo.
            var box = Scale(face.Box, decoded.Scale);

            if (!quality.IsAcceptable(box))
            {
                rejected++;
                continue;
            }

            kept.Add(new Face(
                FaceId.New(),
                work.Photo.Id,
                detectorId,
                detectorVersion,
                box,
                face.Landmarks.Scale(1f / decoded.Scale)));
        }

        // Recorded now because decoding has already happened and this is the only stage that
        // pays for it. Without these the library knows a photo's size on disk but not its
        // dimensions, and nothing can relate a stored face box to the image without decoding
        // the original all over again.
        await photoWriter.UpdateImageDetailsAsync(
            work.Photo.Id,
            new ImageDetails(
                decoded.OriginalWidth,
                decoded.OriginalHeight,
                work.Metadata?.Orientation ?? 1,
                work.Metadata?.TakenUtc,
                work.Metadata?.Camera),
            ct).ConfigureAwait(false);

        // Cleared first so that re-running detection over a photo replaces its faces rather than
        // accumulating a second set beside them.
        await faces.DeleteByPhotoAsync(work.Photo.Id, detectorId, ct).ConfigureAwait(false);
        await faces.BulkInsertAsync(kept, ct).ConfigureAwait(false);

        // Generated here rather than lazily in the UI: the decoded pixels are already in hand,
        // and decoding a second time when the grid scrolls past would double the cost of the
        // most expensive stage in the pipeline.
        await thumbnails.GetOrCreateAsync(work.Photo.Id, work.Photo.Path, ct).ConfigureAwait(false);

        await photoWriter.SetStateAsync(work.Photo.Id, PhotoState.Detected, null, ct).ConfigureAwait(false);

        return (kept.Count, rejected);
    }

    private static FaceBox Scale(FaceBox box, float scale) =>
        scale >= 1f
            ? box
            : new FaceBox(box.X / scale, box.Y / scale, box.Width / scale, box.Height / scale, box.Score);

    private readonly record struct DecodedWork(
        Photo Photo, DecodedImage? Image, ImageMetadata? Metadata = null, string? Error = null);
}

/// <param name="PhotosProcessed">Photos taken through detection, whether or not faces were found.</param>
/// <param name="FacesFound">Detections that passed the quality gate and were stored.</param>
/// <param name="FacesRejected">Detections discarded as too small, too faint or too blurred.</param>
/// <param name="PhotosFailed">Files that could not be decoded and are marked so.</param>
public readonly record struct DetectionResult(
    int PhotosProcessed, int FacesFound, int FacesRejected, int PhotosFailed);
