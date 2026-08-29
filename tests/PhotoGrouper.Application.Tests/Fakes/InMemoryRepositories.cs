using System.Runtime.CompilerServices;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.Photos;

namespace PhotoGrouper.Application.Tests.Fakes;

/// <summary>In-memory photo index, keyed by path exactly as the real one is.</summary>
public sealed class InMemoryPhotoRepository : IPhotoReader, IPhotoWriter
{
    private readonly Dictionary<string, Photo> _byPath = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Counts calls to the batch write, so tests can assert that batching happens at all.</summary>
    public int BulkUpsertCalls { get; private set; }

    /// <summary>
    /// Invoked after each batch is written.
    /// </summary>
    /// <remarks>
    /// Gives cancellation tests a deterministic point to fire at: one where a batch has
    /// definitely been committed and more work definitely remains. Racing a background task
    /// against an in-memory scan is far too fast to be reliable.
    /// </remarks>
    public Action? AfterBulkUpsert { get; set; }

    public Task<Photo?> GetByIdAsync(PhotoId id, CancellationToken ct) =>
        Task.FromResult(_byPath.Values.FirstOrDefault(p => p.Id == id));

    public Task<Photo?> GetByPathAsync(string path, CancellationToken ct) =>
        Task.FromResult(_byPath.GetValueOrDefault(path));

    public Task<IReadOnlyList<Photo>> GetByStateAsync(PhotoState state, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Photo>>(_byPath.Values.Where(p => p.State == state).Take(limit).ToList());

    public Task<int> CountAsync(CancellationToken ct) => Task.FromResult(_byPath.Count);

    /// <summary>Detection records, keyed by photo and detector, mirroring the real store.</summary>
    private readonly HashSet<(PhotoId Photo, string Detector)> _detections = [];

    public Task<IReadOnlyList<Photo>> GetPhotosNeedingDetectionAsync(
        string detectorId, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Photo>>([.. NeedingDetection(detectorId).Take(limit)]);

    public Task<int> CountPhotosNeedingDetectionAsync(string detectorId, CancellationToken ct) =>
        Task.FromResult(NeedingDetection(detectorId).Count());

    private IEnumerable<Photo> NeedingDetection(string detectorId) =>
        _byPath.Values
            .Where(p => p.State != PhotoState.Failed)
            .Where(p => p.State == PhotoState.New || !_detections.Contains((p.Id, detectorId)))
            .OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase);

    public Task RecordDetectionAsync(
        PhotoId id, string detectorId, string detectorVersion, int faceCount, CancellationToken ct)
    {
        _detections.Add((id, detectorId));
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<Photo> StreamAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var photo in _byPath.Values.OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            yield return photo;
            await Task.Yield();
        }
    }

    public Task<PhotoId> UpsertAsync(Photo photo, CancellationToken ct)
    {
        _byPath[photo.Path] = photo;
        return Task.FromResult(photo.Id);
    }

    public Task BulkUpsertAsync(IReadOnlyList<Photo> photos, CancellationToken ct)
    {
        BulkUpsertCalls++;

        foreach (var photo in photos)
        {
            _byPath[photo.Path] = photo;
        }

        AfterBulkUpsert?.Invoke();
        return Task.CompletedTask;
    }

    public Task SetStateAsync(PhotoId id, PhotoState state, string? error, CancellationToken ct)
    {
        if (_byPath.Values.FirstOrDefault(p => p.Id == id) is { } photo)
        {
            photo.AdvanceTo(state);
        }

        return Task.CompletedTask;
    }

    public Task UpdateImageDetailsAsync(PhotoId id, ImageDetails details, CancellationToken ct)
    {
        RecordedDetails[id] = details;
        return Task.CompletedTask;
    }

    /// <summary>What the detection stage recorded about each image, for assertions.</summary>
    public Dictionary<PhotoId, ImageDetails> RecordedDetails { get; } = [];

    public Task UpdatePathAsync(PhotoId id, string newPath, CancellationToken ct)
    {
        if (_byPath.Values.FirstOrDefault(p => p.Id == id) is { } photo)
        {
            _byPath.Remove(photo.Path);
            photo.RelocateTo(newPath);
            _byPath[newPath] = photo;
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(PhotoId id, CancellationToken ct)
    {
        if (_byPath.Values.FirstOrDefault(p => p.Id == id) is { } photo)
        {
            _byPath.Remove(photo.Path);
        }

        return Task.CompletedTask;
    }
}

/// <summary>In-memory scan roots.</summary>
public sealed class InMemoryScanRootRepository : IScanRootRepository
{
    private readonly List<ScanRoot> _roots = [];

    public IReadOnlyList<DateTimeOffset> MarkedScans { get; } = new List<DateTimeOffset>();

    public Task<IReadOnlyList<ScanRoot>> GetAllAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ScanRoot>>(_roots.ToList());

    public Task<ScanRoot?> GetByPathAsync(string path, CancellationToken ct) =>
        Task.FromResult(_roots.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)));

    public Task AddAsync(ScanRoot root, CancellationToken ct)
    {
        _roots.Add(root);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(ScanRootId id, CancellationToken ct)
    {
        _roots.RemoveAll(r => r.Id == id);
        return Task.CompletedTask;
    }

    public Task MarkScannedAsync(ScanRootId id, DateTimeOffset whenUtc, CancellationToken ct)
    {
        ((List<DateTimeOffset>)MarkedScans).Add(whenUtc);
        return Task.CompletedTask;
    }
}

/// <summary>A clock frozen at a chosen instant.</summary>
public sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

/// <summary>Captures every progress report for inspection.</summary>
public sealed class RecordingProgressSink : IProgressSink
{
    public List<ProgressUpdate> Updates { get; } = [];

    public void Report(ProgressUpdate update) => Updates.Add(update);
}
