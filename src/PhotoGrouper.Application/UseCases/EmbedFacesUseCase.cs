using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Faces;
using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.Application.UseCases;

/// <summary>
/// Computes an embedding for every detected face that lacks one.
/// </summary>
/// <remarks>
/// Kept separate from detection rather than folded into it, which is the decision that makes the
/// embedder replaceable. Detection is the expensive half and is unaffected by which embedder is
/// in use, so swapping embedders re-runs only this stage, over faces that are already found.
///
/// Faces are re-aligned from the original photograph rather than from a stored crop. Storing
/// crops would save the decode, at the cost of hundreds of megabytes of images that are useless
/// the moment an embedder wanting a different template is introduced.
/// </remarks>
public sealed class EmbedFacesUseCase(
    IFaceRepository faces,
    IEmbeddingRepository embeddings,
    IPhotoReader photos,
    IImageDecoder decoder,
    IFaceAligner aligner)
{
    private const int FacesPerBatch = 64;

    public async Task<EmbeddingResult> ExecuteAsync(
        IFaceEmbedder embedder,
        string detectorId,
        IProgressSink progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(embedder);

        var embedded = 0;
        var skipped = 0;

        while (!ct.IsCancellationRequested)
        {
            var pending = await embeddings
                .GetFacesMissingEmbeddingAsync(embedder.Info.Id, detectorId, FacesPerBatch, ct)
                .ConfigureAwait(false);

            if (pending.Count == 0)
            {
                break;
            }

            var batch = await PrepareBatchAsync(pending, detectorId, embedder, ct).ConfigureAwait(false);
            skipped += pending.Count - batch.Count;

            if (batch.Count > 0)
            {
                var vectors = embedder.Embed([.. batch.Select(item => item.Crop)]);

                await embeddings.BulkUpsertAsync(
                    embedder.Info.Id,
                    embedder.Info.Version,
                    [.. batch.Select((item, i) => new FaceEmbedding(item.FaceId, vectors[i]))],
                    ct).ConfigureAwait(false);

                embedded += batch.Count;
            }

            progress.Report(new ProgressUpdate("Recognising faces", embedded, null));
        }

        ct.ThrowIfCancellationRequested();
        progress.Report(new ProgressUpdate("Recognising faces", embedded, embedded));
        return new EmbeddingResult(embedded, skipped);
    }

    /// <remarks>
    /// Grouped by photograph so that a picture containing several faces is decoded once rather
    /// than once per face. Group shots are common enough that the difference is substantial.
    /// </remarks>
    private async Task<List<(FaceId FaceId, Domain.Common.ImageBuffer Crop)>> PrepareBatchAsync(
        IReadOnlyList<FaceId> pending,
        string detectorId,
        IFaceEmbedder embedder,
        CancellationToken ct)
    {
        // Fetched by id rather than by scanning and filtering. Streaming the whole face table once
        // per batch would make the total work grow with the square of the library size.
        var batch = await faces.GetByIdsAsync(pending, ct).ConfigureAwait(false);

        var byPhoto = new Dictionary<PhotoId, List<Face>>();
        foreach (var face in batch)
        {
            if (!byPhoto.TryGetValue(face.PhotoId, out var list))
            {
                list = [];
                byPhoto[face.PhotoId] = list;
            }

            list.Add(face);
        }

        var prepared = new List<(FaceId, Domain.Common.ImageBuffer)>(pending.Count);

        foreach (var (photoId, group) in byPhoto)
        {
            ct.ThrowIfCancellationRequested();

            var photo = await photos.GetByIdAsync(photoId, ct).ConfigureAwait(false);
            if (photo is null)
            {
                continue;
            }

            // Decoded at full resolution. Face crops are small and detail matters here in a way it
            // does not for detection: an embedder given an upscaled, soft crop returns a vector
            // that clusters with other soft crops rather than with the same person.
            var decoded = await decoder.DecodeAsync(photo.Path, null, ct).ConfigureAwait(false);
            if (decoded is null)
            {
                continue;
            }

            foreach (var face in group)
            {
                var landmarks = decoded.Scale < 1f
                    ? face.Landmarks.Scale(decoded.Scale)
                    : face.Landmarks;

                prepared.Add((face.Id, aligner.Align(decoded.Buffer, landmarks, embedder.Alignment)));
            }
        }

        return prepared;
    }
}

/// <param name="FacesEmbedded">Faces given a vector during this run.</param>
/// <param name="FacesSkipped">Faces whose photograph could no longer be read.</param>
public readonly record struct EmbeddingResult(int FacesEmbedded, int FacesSkipped);
