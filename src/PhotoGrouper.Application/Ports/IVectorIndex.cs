using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.Application.Ports;

/// <summary>
/// Finds the nearest embeddings to each other.
/// </summary>
/// <remarks>
/// A port with exactly one implementation today, which is exact and brute force. That is a
/// deliberate choice rather than a placeholder.
///
/// An approximate index exists to avoid an all-pairs comparison. Here the all-pairs work happens
/// once, in the background, when the library is first clustered, and at the scale this
/// application targets it takes a couple of minutes. An approximate index would turn that into
/// seconds, on an operation that runs almost never, in exchange for recall tuning, a rebuild
/// after every scan, and an index file that has to be kept consistent with the database. Nothing
/// else needs it: searching for a person compares against a few hundred person centroids, not
/// against every face.
///
/// The port exists so that judgement can be revisited without disturbing anything above it. If a
/// library ever grows past the point where exact search is comfortable, an approximate
/// implementation drops in here and must pass the same contract tests, within a stated recall
/// tolerance.
/// </remarks>
public interface IVectorIndex
{
    /// <summary>
    /// Finds the nearest neighbours of every vector.
    /// </summary>
    /// <param name="vectors">Unit-length vectors, all of the same length.</param>
    /// <param name="neighbourCount">How many neighbours to keep per vector.</param>
    /// <param name="minimumSimilarity">Neighbours below this cosine similarity are discarded.</param>
    /// <returns>For each input, its neighbours in descending order of similarity.</returns>
    Task<IReadOnlyList<Neighbour[]>> FindNeighboursAsync(
        IReadOnlyList<FaceEmbedding> vectors,
        int neighbourCount,
        float minimumSimilarity,
        IProgressSink? progress,
        CancellationToken ct);
}

/// <param name="Index">Position of the neighbour within the input list.</param>
/// <param name="Similarity">Cosine similarity, which for unit vectors is the dot product.</param>
public readonly record struct Neighbour(int Index, float Similarity);
