using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.Application.Ports;

/// <summary>
/// Storage for face embeddings.
/// </summary>
/// <remarks>
/// Kept apart from the face store because vectors behave differently from everything else. They
/// are large, their length varies by embedder, and vectors from different embedders cannot be
/// compared at all, so the embedder has to form part of the key. Holding them separately also
/// keeps the face rows small, which matters because most queries in the application read faces
/// and never touch a vector.
///
/// The arrangement is what allows an embedder to be swapped without re-detecting anything: the
/// faces stay exactly as they are and only their vectors are recomputed.
/// </remarks>
public interface IEmbeddingRepository
{
    Task BulkUpsertAsync(
        string embedderId,
        string embedderVersion,
        IReadOnlyList<FaceEmbedding> embeddings,
        CancellationToken ct);

    Task<float[]?> GetAsync(FaceId faceId, string embedderId, CancellationToken ct);

    /// <summary>Face ids that have no vector yet for this embedder.</summary>
    Task<IReadOnlyList<FaceId>> GetFacesMissingEmbeddingAsync(
        string embedderId, string detectorId, int limit, CancellationToken ct);

    Task<int> CountAsync(string embedderId, CancellationToken ct);

    /// <summary>
    /// Streams every vector for one embedder, in a stable order.
    /// </summary>
    /// <remarks>
    /// Clustering reads the whole set into one contiguous block of memory. Streaming rather than
    /// returning a list lets it fill that block as the rows arrive instead of holding the vectors
    /// twice, which at a hundred thousand faces is the difference between two hundred megabytes
    /// and four hundred.
    /// </remarks>
    IAsyncEnumerable<FaceEmbedding> StreamByEmbedderAsync(
        string embedderId, string detectorId, CancellationToken ct);

    /// <summary>Discards every vector for one embedder, after its version changes.</summary>
    Task DeleteByEmbedderAsync(string embedderId, CancellationToken ct);
}

/// <param name="Vector">Unit length, so that cosine similarity is a plain dot product.</param>
public readonly record struct FaceEmbedding(FaceId FaceId, float[] Vector);
