namespace PhotoGrouper.Domain.Photos;

/// <summary>
/// How far a photo has progressed through the indexing pipeline.
/// </summary>
/// <remarks>
/// This is what makes scanning resumable. Each stage persists its result and advances
/// the state, so a scan interrupted at minute 40 of 45 continues from where it stopped
/// rather than restarting. Without it, a crash late in a long scan would be unrecoverable.
/// </remarks>
public enum PhotoState
{
    /// <summary>Discovered on disk; nothing has been read from it yet.</summary>
    New = 0,

    /// <summary>Dimensions and EXIF metadata have been read.</summary>
    Decoded = 1,

    /// <summary>Face detection has run and its results are stored.</summary>
    Detected = 2,

    /// <summary>Every detected face has an embedding for the active embedder.</summary>
    Embedded = 3,

    /// <summary>
    /// The file could not be processed. Held so a corrupt or unreadable file is skipped
    /// permanently instead of being retried on every scan.
    /// </summary>
    Failed = 99,
}
