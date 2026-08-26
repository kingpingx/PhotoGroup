using System.Numerics.Tensors;
using PhotoGrouper.Application.Ports;

namespace PhotoGrouper.Infrastructure.Vision;

/// <summary>
/// Exact nearest-neighbour search over face embeddings.
/// </summary>
/// <remarks>
/// Compares every vector against every other. That sounds untenable and is not, because the
/// vectors are unit length, so cosine similarity is a dot product, and a dot product over a
/// hundred thousand vectors of five hundred and twelve dimensions is arithmetic the hardware is
/// extremely good at.
///
/// Two decisions keep it practical. The comparison runs through the vectorised primitives in the
/// runtime, which use the widest instructions the processor offers rather than a scalar loop. And
/// the work is done in tiles, keeping only the best few neighbours of each row, so the full
/// similarity matrix is never held: for a hundred thousand faces that matrix would be forty
/// gigabytes, while the neighbour lists are a few tens of megabytes.
/// </remarks>
public sealed class BruteForceVectorIndex : IVectorIndex
{
    /// <summary>
    /// Rows processed per tile.
    /// </summary>
    /// <remarks>
    /// Chosen so a tile's working set stays within processor cache while still giving the
    /// parallel loop enough work per iteration to be worth dispatching.
    /// </remarks>
    private const int TileSize = 512;

    public Task<IReadOnlyList<Neighbour[]>> FindNeighboursAsync(
        IReadOnlyList<FaceEmbedding> vectors,
        int neighbourCount,
        float minimumSimilarity,
        IProgressSink? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vectors);

        if (vectors.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<Neighbour[]>>([]);
        }

        if (neighbourCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(neighbourCount));
        }

        return Task.Run<IReadOnlyList<Neighbour[]>>(
            () => Compute(vectors, neighbourCount, minimumSimilarity, progress, ct), ct);
    }

    private static Neighbour[][] Compute(
        IReadOnlyList<FaceEmbedding> vectors,
        int neighbourCount,
        float minimumSimilarity,
        IProgressSink? progress,
        CancellationToken ct)
    {
        var count = vectors.Count;
        var dimensions = vectors[0].Vector.Length;

        // Copied into one contiguous block so that each row is a straight slice. Comparing
        // against an array of separate arrays defeats prefetching, and at this volume that costs
        // considerably more than the copy.
        var arena = new float[(long)count * dimensions];
        for (var i = 0; i < count; i++)
        {
            var vector = vectors[i].Vector;
            if (vector.Length != dimensions)
            {
                throw new ArgumentException(
                    $"Vector {i} has {vector.Length} dimensions; {dimensions} was expected. "
                    + "Vectors from different embedders cannot be compared.",
                    nameof(vectors));
            }

            vector.CopyTo(arena.AsSpan(i * dimensions));
        }

        var results = new Neighbour[count][];
        var completed = 0;

        Parallel.For(
            0,
            (count + TileSize - 1) / TileSize,
            new ParallelOptions { CancellationToken = ct },
            () => new float[count],
            (tile, _, similarities) =>
            {
                var start = tile * TileSize;
                var end = Math.Min(start + TileSize, count);

                for (var row = start; row < end; row++)
                {
                    var query = arena.AsSpan(row * dimensions, dimensions);

                    for (var other = 0; other < count; other++)
                    {
                        similarities[other] = TensorPrimitives.Dot(
                            query, arena.AsSpan(other * dimensions, dimensions));
                    }

                    results[row] = SelectTop(similarities, row, neighbourCount, minimumSimilarity);
                }

                var done = Interlocked.Add(ref completed, end - start);
                progress?.Report(new ProgressUpdate("Comparing faces", done, count));

                return similarities;
            },
            _ => { });

        return results;
    }

    /// <summary>
    /// Keeps the strongest neighbours of one row.
    /// </summary>
    /// <remarks>
    /// A running minimum over a small array rather than a sort of the whole row. Sorting a hundred
    /// thousand similarities to keep twenty of them would cost more than computing them did.
    /// </remarks>
    private static Neighbour[] SelectTop(
        float[] similarities, int self, int neighbourCount, float minimumSimilarity)
    {
        Span<Neighbour> best = neighbourCount <= 64
            ? stackalloc Neighbour[neighbourCount]
            : new Neighbour[neighbourCount];

        var filled = 0;
        var weakest = float.MaxValue;
        var weakestSlot = 0;

        for (var i = 0; i < similarities.Length; i++)
        {
            // A vector is always its own nearest neighbour, at similarity one. Including it would
            // waste a slot and make every face appear to have one more match than it has.
            if (i == self)
            {
                continue;
            }

            var similarity = similarities[i];
            if (similarity < minimumSimilarity)
            {
                continue;
            }

            if (filled < neighbourCount)
            {
                best[filled] = new Neighbour(i, similarity);
                filled++;

                if (filled == neighbourCount)
                {
                    (weakest, weakestSlot) = FindWeakest(best);
                }

                continue;
            }

            if (similarity > weakest)
            {
                best[weakestSlot] = new Neighbour(i, similarity);
                (weakest, weakestSlot) = FindWeakest(best);
            }
        }

        var kept = best[..filled].ToArray();
        Array.Sort(kept, (a, b) => b.Similarity.CompareTo(a.Similarity));
        return kept;
    }

    private static (float Similarity, int Slot) FindWeakest(ReadOnlySpan<Neighbour> neighbours)
    {
        var weakest = float.MaxValue;
        var slot = 0;

        for (var i = 0; i < neighbours.Length; i++)
        {
            if (neighbours[i].Similarity < weakest)
            {
                weakest = neighbours[i].Similarity;
                slot = i;
            }
        }

        return (weakest, slot);
    }
}
