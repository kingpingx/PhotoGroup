using FluentAssertions;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Common;
using PhotoGrouper.Domain.Faces;
using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.Photos;

namespace PhotoGrouper.Contracts.Tests;

/// <summary>
/// The behaviour every embedding store must exhibit.
/// </summary>
/// <remarks>
/// Exactness gets more attention here than anywhere else in these contracts. Every grouping
/// decision the application makes is a dot product of two of these vectors, so a value degraded in
/// storage does not fail, it shifts a similarity slightly, and a face that should have joined a
/// person quietly does not. Storing them as single-precision floats and reading them back
/// unchanged is therefore a requirement, not a nicety.
/// </remarks>
public abstract class EmbeddingRepositoryContract
{
    protected const string EmbedderA = "test.embedder.a";
    protected const string EmbedderB = "test.embedder.b";
    protected const string Detector = "test.detector";

    protected abstract Task<EmbeddingContext> CreateAsync();

    /// <param name="Faces">Needed because embeddings reference faces, which reference photos.</param>
    public sealed record EmbeddingContext(
        IEmbeddingRepository Embeddings, IFaceRepository Faces, IPhotoWriter Photos);

    private static readonly FaceLandmarks Landmarks = new(
        new Point2(30, 40), new Point2(70, 40), new Point2(50, 60),
        new Point2(35, 80), new Point2(65, 80));

    private static float[] Vector(int dimensions, float seed) =>
        [.. Enumerable.Range(0, dimensions).Select(i => (seed + i) / 1000f)];

    private async Task<FaceId> AddFaceAsync(EmbeddingContext context, string path = @"D:\photos\a.jpg")
    {
        var photo = new Photo(PhotoId.New(), path, 1000, DateTimeOffset.UnixEpoch);
        await context.Photos.UpsertAsync(photo, default);

        var face = new Face(
            FaceId.New(), photo.Id, Detector, "1",
            new FaceBox(0, 0, 100, 100, 0.9f), Landmarks);

        await context.Faces.BulkInsertAsync([face], default);
        return face.Id;
    }

    [Fact]
    public async Task An_empty_store_holds_nothing()
    {
        var context = await CreateAsync();

        (await context.Embeddings.CountAsync(EmbedderA, default)).Should().Be(0);
    }

    [Fact]
    public async Task A_stored_vector_reads_back_bit_for_bit()
    {
        // Not approximately. These values feed every similarity comparison the app makes, and a
        // vector degraded in storage costs accuracy silently rather than failing.
        var context = await CreateAsync();
        var face = await AddFaceAsync(context);
        var vector = Vector(512, 3.14159f);

        await context.Embeddings.BulkUpsertAsync(EmbedderA, "1", [new FaceEmbedding(face, vector)], default);

        (await context.Embeddings.GetAsync(face, EmbedderA, default)).Should().Equal(vector);
    }

    [Fact]
    public async Task A_missing_vector_reads_back_as_nothing()
    {
        var context = await CreateAsync();
        var face = await AddFaceAsync(context);

        (await context.Embeddings.GetAsync(face, EmbedderA, default)).Should().BeNull();
    }

    [Fact]
    public async Task Two_embedders_can_hold_vectors_for_the_same_face()
    {
        // What makes an embedder swappable without re-detecting: the faces stay put and only their
        // vectors are recomputed, with both sets available for comparison meanwhile.
        var context = await CreateAsync();
        var face = await AddFaceAsync(context);

        await context.Embeddings.BulkUpsertAsync(EmbedderA, "1", [new FaceEmbedding(face, Vector(8, 1))], default);
        await context.Embeddings.BulkUpsertAsync(EmbedderB, "1", [new FaceEmbedding(face, Vector(4, 2))], default);

        (await context.Embeddings.GetAsync(face, EmbedderA, default)).Should().HaveCount(8);
        (await context.Embeddings.GetAsync(face, EmbedderB, default)).Should().HaveCount(4);
    }

    [Fact]
    public async Task Re_embedding_a_face_replaces_its_vector()
    {
        var context = await CreateAsync();
        var face = await AddFaceAsync(context);

        await context.Embeddings.BulkUpsertAsync(EmbedderA, "1", [new FaceEmbedding(face, Vector(8, 1))], default);
        await context.Embeddings.BulkUpsertAsync(EmbedderA, "2", [new FaceEmbedding(face, Vector(8, 9))], default);

        (await context.Embeddings.GetAsync(face, EmbedderA, default)).Should().Equal(Vector(8, 9));
        (await context.Embeddings.CountAsync(EmbedderA, default)).Should().Be(1);
    }

    [Fact]
    public async Task Faces_without_a_vector_can_be_listed()
    {
        // How the embedding stage finds its work, and what makes it resumable: an interrupted run
        // simply leaves the remainder outstanding.
        var context = await CreateAsync();
        var first = await AddFaceAsync(context, @"D:\photos\1.jpg");
        var second = await AddFaceAsync(context, @"D:\photos\2.jpg");

        await context.Embeddings.BulkUpsertAsync(EmbedderA, "1", [new FaceEmbedding(first, Vector(8, 1))], default);

        var pending = await context.Embeddings.GetFacesMissingEmbeddingAsync(EmbedderA, Detector, 100, default);

        pending.Should().ContainSingle().Which.Should().Be(second);
    }

    [Fact]
    public async Task The_pending_list_respects_its_limit()
    {
        var context = await CreateAsync();
        for (var i = 0; i < 10; i++)
        {
            await AddFaceAsync(context, $@"D:\photos\{i}.jpg");
        }

        (await context.Embeddings.GetFacesMissingEmbeddingAsync(EmbedderA, Detector, 4, default))
            .Should().HaveCount(4);
    }

    [Fact]
    public async Task A_face_embedded_by_another_embedder_is_still_pending_for_this_one()
    {
        var context = await CreateAsync();
        var face = await AddFaceAsync(context);
        await context.Embeddings.BulkUpsertAsync(EmbedderB, "1", [new FaceEmbedding(face, Vector(8, 1))], default);

        (await context.Embeddings.GetFacesMissingEmbeddingAsync(EmbedderA, Detector, 100, default))
            .Should().ContainSingle();
    }

    [Fact]
    public async Task Streaming_returns_every_vector_in_a_stable_order()
    {
        var context = await CreateAsync();
        var faces = new List<FaceId>();
        for (var i = 0; i < 25; i++)
        {
            faces.Add(await AddFaceAsync(context, $@"D:\photos\{i:D3}.jpg"));
        }

        await context.Embeddings.BulkUpsertAsync(
            EmbedderA, "1", [.. faces.Select(f => new FaceEmbedding(f, Vector(8, 1)))], default);

        var first = await Collect(context);
        var second = await Collect(context);

        first.Should().HaveCount(25);
        second.Should().Equal(first, "clustering fills a fixed arena as rows arrive and needs a settled order");

        static async Task<List<FaceId>> Collect(EmbeddingContext context)
        {
            var ids = new List<FaceId>();
            await foreach (var e in context.Embeddings.StreamByEmbedderAsync(EmbedderA, Detector, default))
            {
                ids.Add(e.FaceId);
            }

            return ids;
        }
    }

    [Fact]
    public async Task Streaming_excludes_faces_from_an_inactive_detector()
    {
        // Faces belonging to a previous detector are retained so a switch is reversible. Feeding
        // them to clustering would group the same person twice, once per detector.
        var context = await CreateAsync();
        var face = await AddFaceAsync(context);
        await context.Embeddings.BulkUpsertAsync(EmbedderA, "1", [new FaceEmbedding(face, Vector(8, 1))], default);

        await context.Faces.SetActiveDetectorAsync("some.other.detector", default);

        var streamed = new List<FaceEmbedding>();
        await foreach (var e in context.Embeddings.StreamByEmbedderAsync(EmbedderA, Detector, default))
        {
            streamed.Add(e);
        }

        streamed.Should().BeEmpty();
    }

    [Fact]
    public async Task Discarding_an_embedders_vectors_leaves_the_others_alone()
    {
        var context = await CreateAsync();
        var face = await AddFaceAsync(context);
        await context.Embeddings.BulkUpsertAsync(EmbedderA, "1", [new FaceEmbedding(face, Vector(8, 1))], default);
        await context.Embeddings.BulkUpsertAsync(EmbedderB, "1", [new FaceEmbedding(face, Vector(8, 2))], default);

        await context.Embeddings.DeleteByEmbedderAsync(EmbedderA, default);

        (await context.Embeddings.CountAsync(EmbedderA, default)).Should().Be(0);
        (await context.Embeddings.CountAsync(EmbedderB, default)).Should().Be(1);
    }

    [Fact]
    public async Task Storing_an_empty_batch_is_harmless()
    {
        var context = await CreateAsync();

        await context.Embeddings.BulkUpsertAsync(EmbedderA, "1", [], default);

        (await context.Embeddings.CountAsync(EmbedderA, default)).Should().Be(0);
    }

    [Fact]
    public async Task A_realistically_sized_batch_stores_correctly()
    {
        var context = await CreateAsync();
        var faces = new List<FaceId>();
        for (var i = 0; i < 200; i++)
        {
            faces.Add(await AddFaceAsync(context, $@"D:\photos\{i:D4}.jpg"));
        }

        await context.Embeddings.BulkUpsertAsync(
            EmbedderA, "1", [.. faces.Select((f, i) => new FaceEmbedding(f, Vector(512, i)))], default);

        (await context.Embeddings.CountAsync(EmbedderA, default)).Should().Be(200);
        (await context.Embeddings.GetAsync(faces[57], EmbedderA, default)).Should().Equal(Vector(512, 57));
    }
}
