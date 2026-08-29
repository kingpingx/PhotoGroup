using PhotoGrouper.Application.Photos;
using PhotoGrouper.Application.Ports;

namespace PhotoGrouper.Application.UseCases;

/// <summary>
/// Works out what each photograph looks like, so near-duplicates can be found.
/// </summary>
/// <remarks>
/// A pass of its own rather than part of scanning. Scanning is deliberately cheap — names, sizes
/// and timestamps, no pixels — because it runs on every folder every time and a user adding a
/// folder should not wait minutes to see a grid. This opens and decodes every file, so it belongs
/// behind a button somebody presses when they want the answer it gives.
///
/// Resumable, like every other long pass here: only photographs without a fingerprint are read, so
/// closing the application half way through costs the batch in flight and nothing else.
/// </remarks>
public sealed class IndexPhotoSignaturesUseCase(
    IPhotoSignatureRepository signatures,
    IImageDecoder decoder)
{
    /// <summary>
    /// Long edge the image is decoded at before being fingerprinted.
    /// </summary>
    /// <remarks>
    /// The fingerprint reduces to a nine by eight grid and sharpness to forty-eight square, so
    /// nothing above this is used for either. Decoding at full resolution would multiply the cost
    /// of this pass by a hundred to produce identical numbers.
    ///
    /// Not smaller, because it must not fall below the sharpness grid: measuring fine detail on an
    /// image already reduced past that grid measures the decoder's resampling instead of the
    /// photograph.
    /// </remarks>
    public const int DecodeLongEdge = 256;

    /// <summary>Photographs held in memory at once.</summary>
    private const int BatchSize = 64;

    public async Task<SignatureIndexingResult> ExecuteAsync(IProgressSink progress, CancellationToken ct)
    {
        var total = await signatures.CountPhotosNeedingSignatureAsync(ct).ConfigureAwait(false);
        progress.Report(new ProgressUpdate("Reading photos", 0, total));

        if (total == 0)
        {
            return new SignatureIndexingResult(0, 0);
        }

        var completed = 0;
        var failed = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var batch = await signatures
                .GetPhotosNeedingSignatureAsync(BatchSize, ct)
                .ConfigureAwait(false);

            if (batch.Count == 0)
            {
                break;
            }

            var computed = new List<PhotoSignature>(batch.Count);

            foreach (var photo in batch)
            {
                ct.ThrowIfCancellationRequested();

                var decoded = await decoder
                    .DecodeAsync(photo.Path, DecodeLongEdge, ct)
                    .ConfigureAwait(false);

                if (decoded is null)
                {
                    failed++;
                    continue;
                }

                computed.Add(new PhotoSignature(
                    photo.Id,
                    PhotoSignatures.Hash(decoded.Buffer),
                    PhotoSignatures.Sharpness(decoded.Buffer)));
            }

            await signatures.BulkUpsertAsync(computed, ct).ConfigureAwait(false);

            completed += computed.Count;
            progress.Report(new ProgressUpdate("Reading photos", completed, total));

            // A batch where every file failed to decode writes nothing, so the same batch would be
            // returned again for ever. Stopping is right: the remaining files cannot be read, and
            // saying so beats looping.
            if (computed.Count == 0)
            {
                break;
            }
        }

        return new SignatureIndexingResult(completed, failed);
    }
}

/// <param name="Failed">Files that could not be decoded, and so have no fingerprint.</param>
public readonly record struct SignatureIndexingResult(int Fingerprinted, int Failed);
