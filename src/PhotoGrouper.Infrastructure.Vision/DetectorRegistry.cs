using PhotoGrouper.Application.Ports;

namespace PhotoGrouper.Infrastructure.Vision;

/// <summary>
/// The detectors this build knows about, and how to bring one into being.
/// </summary>
/// <remarks>
/// Detectors and embedders are registered independently rather than as fixed pairs. An embedder
/// does not care which detector found a face, only that it arrives aligned, so pairing them at
/// the point of use is what allows the detector to be changed without re-embedding, and a
/// custom embedder to be added without touching detection at all.
///
/// This is the extension point the design turns on: adding a detector means adding an entry
/// here, with nothing above it aware that the set has changed.
/// </remarks>
public sealed class DetectorRegistry(ModelStore models, OnnxSessionFactory sessionFactory)
{
    private static readonly IReadOnlyList<DetectorRegistration> Registrations =
    [
        new(
            YuNetDetector.Provider,
            YuNetDetector.Model,
            (path, factory) => YuNetDetector.Load(path, factory)),
        new(
            ScrfdDetector.Provider,
            ScrfdDetector.Model,
            (path, factory) => ScrfdDetector.Load(path, factory)),
    ];

    /// <summary>
    /// The detector used when the user has not chosen one.
    /// </summary>
    /// <remarks>
    /// YuNet, because it is roughly four times faster and permissively licensed. SCRFD finds
    /// more of the difficult faces and is available from settings, but making the slower,
    /// non-commercially-licensed model the default is not a decision to take on a user's behalf.
    /// </remarks>
    public static string DefaultDetectorId => YuNetDetector.Provider.Id;

    public static IReadOnlyList<ProviderInfo> Available => [.. Registrations.Select(r => r.Info)];

    public static ModelDescriptor ModelFor(string detectorId) =>
        Find(detectorId).Model;

    public static ProviderInfo InfoFor(string detectorId) => Find(detectorId).Info;

    public bool IsReady(string detectorId) => models.IsAvailable(Find(detectorId).Model);

    /// <summary>
    /// Creates a detector, downloading its model first if necessary.
    /// </summary>
    /// <remarks>
    /// The caller owns the returned instance. Detectors hold a native inference session and a
    /// model's worth of memory, so one is created per processing run rather than per image.
    /// </remarks>
    public async Task<IFaceDetector> CreateAsync(
        string detectorId,
        IProgress<double>? downloadProgress,
        CancellationToken ct)
    {
        var registration = Find(detectorId);
        var path = await models
            .EnsureAvailableAsync(registration.Model, downloadProgress, ct)
            .ConfigureAwait(false);

        return registration.Create(path, sessionFactory);
    }

    private static DetectorRegistration Find(string detectorId) =>
        Registrations.FirstOrDefault(r => r.Info.Id == detectorId)
        ?? throw new ArgumentException(
            $"No detector is registered with id '{detectorId}'. "
            + $"Known ids: {string.Join(", ", Registrations.Select(r => r.Info.Id))}.",
            nameof(detectorId));

    private sealed record DetectorRegistration(
        ProviderInfo Info,
        ModelDescriptor Model,
        Func<string, OnnxSessionFactory, IFaceDetector> Create);
}
