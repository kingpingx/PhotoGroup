namespace PhotoGrouper.Domain.Identity;

/// <summary>Identity of a photo file tracked by the library.</summary>
/// <remarks>
/// Ids are strongly typed rather than bare Guids so that passing a FaceId where a
/// PhotoId is expected is a compile error. Nearly every method in the storage layer
/// takes at least one id, and they are otherwise indistinguishable at a call site.
/// </remarks>
public readonly record struct PhotoId(Guid Value)
{
    public static PhotoId New() => new(Uuid7.NewGuid());
    public override string ToString() => Value.ToString();
}

/// <summary>Identity of a single detected face within a photo.</summary>
public readonly record struct FaceId(Guid Value)
{
    public static FaceId New() => new(Uuid7.NewGuid());
    public override string ToString() => Value.ToString();
}

/// <summary>Identity of a named person.</summary>
public readonly record struct PersonId(Guid Value)
{
    public static PersonId New() => new(Uuid7.NewGuid());
    public override string ToString() => Value.ToString();
}

/// <summary>Identity of an algorithmic face cluster, prior to being named.</summary>
public readonly record struct ClusterId(Guid Value)
{
    public static ClusterId New() => new(Uuid7.NewGuid());
    public override string ToString() => Value.ToString();
}

/// <summary>Identity of a folder the library scans.</summary>
public readonly record struct ScanRootId(Guid Value)
{
    public static ScanRootId New() => new(Uuid7.NewGuid());
    public override string ToString() => Value.ToString();
}

/// <summary>Identity of one export (copy or move) run.</summary>
public readonly record struct ExportRunId(Guid Value)
{
    public static ExportRunId New() => new(Uuid7.NewGuid());
    public override string ToString() => Value.ToString();
}

/// <summary>Identity of a single file operation within an export run.</summary>
public readonly record struct ExportOpId(Guid Value)
{
    public static ExportOpId New() => new(Uuid7.NewGuid());
    public override string ToString() => Value.ToString();
}
