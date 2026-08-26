using FluentAssertions;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Infrastructure.Vision;

namespace PhotoGrouper.Infrastructure.Tests;

/// <summary>
/// Covers nearest-neighbour search over embeddings.
/// </summary>
/// <remarks>
/// The tiled, parallel, vectorised implementation is considerably more intricate than the
/// definition it implements, so most of these tests compare it against a plain nested loop over
/// the same data. That comparison is the contract any future approximate index would also have to
/// satisfy, within a stated recall tolerance.
/// </remarks>
public sealed class BruteForceVectorIndexTests
{
    private readonly IVectorIndex _index = new BruteForceVectorIndex();

    /// <summary>Builds unit-length vectors clustered around a number of centres.</summary>
    private static List<FaceEmbedding> MakeVectors(int count, int dimensions, int centres, int seed, float spread = 0.15f)
    {
        var random = new Random(seed);

        var anchors = Enumerable.Range(0, centres)
            .Select(_ => Normalize([.. Enumerable.Range(0, dimensions).Select(_ => (float)(random.NextDouble() - 0.5))]))
            .ToList();

        return [.. Enumerable.Range(0, count).Select(i =>
        {
            var anchor = anchors[i % centres];
            var vector = new float[dimensions];
            for (var d = 0; d < dimensions; d++)
            {
                vector[d] = anchor[d] + (float)((random.NextDouble() - 0.5) * spread);
            }

            return new FaceEmbedding(FaceId.New(), Normalize(vector));
        })];
    }

    private static float[] Normalize(float[] vector)
    {
        var length = MathF.Sqrt(vector.Sum(v => v * v));
        return length <= 0 ? vector : [.. vector.Select(v => v / length)];
    }

    /// <summary>The definition, written as plainly as possible.</summary>
    private static Neighbour[] NaiveNeighbours(
        IReadOnlyList<FaceEmbedding> vectors, int self, int count, float minimum)
    {
        return [.. Enumerable.Range(0, vectors.Count)
            .Where(i => i != self)
            .Select(i => new Neighbour(i, Dot(vectors[self].Vector, vectors[i].Vector)))
            .Where(n => n.Similarity >= minimum)
            .OrderByDescending(n => n.Similarity)
            .Take(count)];
    }

    private static float Dot(float[] a, float[] b)
    {
        var sum = 0f;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    [Fact]
    public async Task An_empty_input_yields_no_results() =>
        (await _index.FindNeighboursAsync([], 5, 0f, null, default)).Should().BeEmpty();

    [Fact]
    public async Task A_single_vector_has_no_neighbours()
    {
        var result = await _index.FindNeighboursAsync(MakeVectors(1, 8, 1, seed: 1), 5, -1f, null, default);

        result.Should().ContainSingle();
        result[0].Should().BeEmpty();
    }

    [Fact]
    public async Task A_vector_is_never_its_own_neighbour()
    {
        // It would always rank first, at similarity one, wasting a slot and making every face
        // appear to have one more match than it does.
        var vectors = MakeVectors(20, 16, 2, seed: 2);

        var result = await _index.FindNeighboursAsync(vectors, 5, -1f, null, default);

        for (var i = 0; i < result.Count; i++)
        {
            result[i].Should().NotContain(n => n.Index == i);
        }
    }

    [Fact]
    public async Task Results_match_a_plain_nested_loop()
    {
        var vectors = MakeVectors(200, 32, 4, seed: 3);
        const int k = 10;
        const float minimum = 0.2f;

        var actual = await _index.FindNeighboursAsync(vectors, k, minimum, null, default);

        for (var i = 0; i < vectors.Count; i++)
        {
            var expected = NaiveNeighbours(vectors, i, k, minimum);

            actual[i].Select(n => n.Index).Should().Equal(
                expected.Select(n => n.Index),
                $"vector {i} must have the same neighbours as the definition, in the same order");
        }
    }

    [Fact]
    public async Task Similarities_match_a_plain_nested_loop()
    {
        var vectors = MakeVectors(120, 64, 3, seed: 4);

        var actual = await _index.FindNeighboursAsync(vectors, 5, -1f, null, default);

        for (var i = 0; i < vectors.Count; i++)
        {
            var expected = NaiveNeighbours(vectors, i, 5, -1f);
            for (var j = 0; j < expected.Length; j++)
            {
                actual[i][j].Similarity.Should().BeApproximately(expected[j].Similarity, 0.0001f);
            }
        }
    }

    [Fact]
    public async Task Neighbours_come_back_strongest_first()
    {
        var result = await _index.FindNeighboursAsync(MakeVectors(60, 32, 3, seed: 5), 8, -1f, null, default);

        foreach (var neighbours in result)
        {
            neighbours.Select(n => n.Similarity).Should().BeInDescendingOrder();
        }
    }

    [Fact]
    public async Task Weak_matches_are_excluded()
    {
        var vectors = MakeVectors(80, 32, 4, seed: 6);
        const float minimum = 0.5f;

        var result = await _index.FindNeighboursAsync(vectors, 20, minimum, null, default);

        result.SelectMany(n => n).Should().OnlyContain(n => n.Similarity >= minimum);
    }

    [Fact]
    public async Task No_more_than_the_requested_number_are_returned()
    {
        var result = await _index.FindNeighboursAsync(MakeVectors(100, 16, 1, seed: 7), 7, -1f, null, default);

        result.Should().OnlyContain(n => n.Length <= 7);
    }

    [Fact]
    public async Task Faces_of_the_same_person_find_each_other()
    {
        // The property the whole application rests on, stated at the level of vectors: members of
        // a tight group should list other members ahead of anything outside it.
        var vectors = MakeVectors(60, 64, centres: 3, seed: 8, spread: 0.05f);

        var result = await _index.FindNeighboursAsync(vectors, 5, -1f, null, default);

        for (var i = 0; i < vectors.Count; i++)
        {
            var ownGroup = i % 3;
            result[i].Should().OnlyContain(n => n.Index % 3 == ownGroup,
                "a tightly grouped vector's nearest neighbours should all be from its own group");
        }
    }

    [Fact]
    public async Task Vectors_of_differing_length_are_rejected()
    {
        // Vectors from two embedders cannot be compared, and a silent comparison would produce
        // similarity scores that mean nothing.
        List<FaceEmbedding> mixed =
        [
            new(FaceId.New(), new float[8]),
            new(FaceId.New(), new float[16]),
        ];

        var act = async () => await _index.FindNeighboursAsync(mixed, 2, -1f, null, default);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Progress_is_reported()
    {
        var progress = new RecordingSink();

        await _index.FindNeighboursAsync(MakeVectors(1500, 32, 5, seed: 9), 5, 0f, progress, default);

        progress.Updates.Should().NotBeEmpty();
        progress.Updates[^1].Completed.Should().Be(1500);
    }

    [Fact]
    public async Task A_realistic_number_of_faces_completes_in_reasonable_time()
    {
        // Ten thousand faces of the real embedding width. The full similarity matrix at this size
        // would be four hundred megabytes and at a hundred thousand faces forty gigabytes, which
        // is why only the strongest few neighbours per row are ever held.
        var vectors = MakeVectors(10_000, 512, centres: 200, seed: 10);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await _index.FindNeighboursAsync(vectors, 20, 0.3f, null, default);
        watch.Stop();

        result.Should().HaveCount(10_000);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromMinutes(2));
    }

    private sealed class RecordingSink : IProgressSink
    {
        public List<ProgressUpdate> Updates { get; } = [];

        public void Report(ProgressUpdate update) => Updates.Add(update);
    }
}
