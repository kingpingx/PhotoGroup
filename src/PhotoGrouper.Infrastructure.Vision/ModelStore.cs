using System.Security.Cryptography;

namespace PhotoGrouper.Infrastructure.Vision;

/// <summary>
/// Fetches and caches the model files the detectors and embedders need.
/// </summary>
/// <remarks>
/// Models are downloaded rather than shipped in the repository. The embedder alone is well over
/// a hundred megabytes, which does not belong in source control, and the licences differ from
/// the project's own.
///
/// Every download is checked against a known digest. A model file is executable input to
/// inference: a truncated download produces a load failure that at least surfaces, but a
/// substituted file would run happily and return meaningless vectors. The digest is also what
/// makes a partial download from a dropped connection self-correcting rather than permanently
/// poisoning the cache.
/// </remarks>
public sealed class ModelStore(string rootDirectory, HttpClient? httpClient = null)
{
    private readonly HttpClient _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

    public string RootDirectory { get; } = rootDirectory;

    /// <summary>True when the model is already present and passes its digest check.</summary>
    public bool IsAvailable(ModelDescriptor model)
    {
        var path = PathFor(model);
        return File.Exists(path) && Verify(path, model.Sha256);
    }

    public string PathFor(ModelDescriptor model) => Path.Combine(RootDirectory, model.FileName);

    /// <summary>
    /// Returns the local path to the model, downloading it if necessary.
    /// </summary>
    /// <exception cref="ModelUnavailableException">
    /// The model is absent and could not be fetched, or what was fetched did not match its digest.
    /// </exception>
    public async Task<string> EnsureAvailableAsync(
        ModelDescriptor model,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var path = PathFor(model);

        if (File.Exists(path))
        {
            if (Verify(path, model.Sha256))
            {
                return path;
            }

            // Present but wrong: a previous download was interrupted, or the file was replaced.
            // Removing it turns a permanently broken model into one bad startup.
            File.Delete(path);
        }

        Directory.CreateDirectory(RootDirectory);
        var temporary = path + ".part";

        try
        {
            await DownloadAsync(model, temporary, progress, ct).ConfigureAwait(false);

            if (!Verify(temporary, model.Sha256))
            {
                File.Delete(temporary);
                throw new ModelUnavailableException(
                    $"The downloaded file for {model.DisplayName} did not match its expected digest. " +
                    "The download may have been corrupted or intercepted.");
            }

            // Moved into place only once verified, so nothing ever observes a partial model at
            // the real path.
            File.Move(temporary, path, overwrite: true);
            return path;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new ModelUnavailableException(
                $"{model.DisplayName} could not be downloaded from {model.Url}. " +
                "Check the network connection, or place the file manually in the models folder.", e);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                    // Left for the next attempt to overwrite.
                }
            }
        }
    }

    private async Task DownloadAsync(
        ModelDescriptor model, string destination, IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await _http
            .GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var expectedLength = response.Content.Headers.ContentLength;

        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = new FileStream(
            destination, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);

        var buffer = new byte[128 * 1024];
        long written = 0;

        while (true)
        {
            var read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            written += read;

            if (expectedLength is > 0)
            {
                progress?.Report((double)written / expectedLength.Value);
            }
        }
    }

    private static bool Verify(string path, string expectedSha256)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <param name="Sha256">Hex-encoded digest of the expected file.</param>
/// <param name="Licence">Terms the user should be aware of, surfaced in settings.</param>
public sealed record ModelDescriptor(
    string FileName,
    string Url,
    string Sha256,
    string DisplayName,
    string Licence);

public sealed class ModelUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);
