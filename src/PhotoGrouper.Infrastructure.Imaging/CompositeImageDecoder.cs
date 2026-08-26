using PhotoGrouper.Application.Ports;

namespace PhotoGrouper.Infrastructure.Imaging;

/// <summary>Routes each file to the decoder that handles its format.</summary>
/// <remarks>
/// The extension point for new formats: adding RAW means registering another decoder here, with
/// nothing above this file aware that the set changed.
/// </remarks>
public sealed class CompositeImageDecoder(IReadOnlyList<IImageDecoder> decoders) : IImageDecoder
{
    /// <summary>The decoders this app ships with, in priority order.</summary>
    public static CompositeImageDecoder CreateDefault() =>
        new([new OpenCvImageDecoder(), new HeifImageDecoder()]);

    public bool CanDecode(string path) => decoders.Any(d => d.CanDecode(path));

    public Task<DecodedImage?> DecodeAsync(string path, int? maxLongEdge, CancellationToken ct) =>
        Select(path) is { } decoder
            ? decoder.DecodeAsync(path, maxLongEdge, ct)
            : Task.FromResult<DecodedImage?>(null);

    public Task<ImageMetadata?> ReadMetadataAsync(string path, CancellationToken ct) =>
        Select(path) is { } decoder
            ? decoder.ReadMetadataAsync(path, ct)
            : Task.FromResult<ImageMetadata?>(null);

    private IImageDecoder? Select(string path) => decoders.FirstOrDefault(d => d.CanDecode(path));
}
