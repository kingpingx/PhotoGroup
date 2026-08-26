using System.Buffers.Binary;
using PhotoGrouper.Domain.Faces;

namespace PhotoGrouper.Infrastructure.Storage.Sqlite;

/// <summary>Serialises landmarks to and from the blob column that holds them.</summary>
/// <remarks>
/// Written little-endian explicitly rather than through BitConverter's platform-dependent
/// default. The database file is expected to be portable, and a value whose interpretation
/// depends on the machine that wrote it is not.
/// </remarks>
internal static class LandmarkCodec
{
    public const int ByteCount = FaceLandmarks.ValueCount * sizeof(float);

    public static byte[] ToBytes(FaceLandmarks landmarks)
    {
        var values = landmarks.ToFloats();
        var bytes = new byte[ByteCount];

        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), values[i]);
        }

        return bytes;
    }

    public static FaceLandmarks FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ByteCount)
        {
            throw new ArgumentException(
                $"Landmark blobs are {ByteCount} bytes; found {bytes.Length}.", nameof(bytes));
        }

        Span<float> values = stackalloc float[FaceLandmarks.ValueCount];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes[(i * sizeof(float))..]);
        }

        return FaceLandmarks.FromFloats(values);
    }
}
