namespace PhotoGrouper.Domain.Faces;

/// <summary>How a face came to be attached to a person.</summary>
/// <remarks>
/// Distinguishes what the app decided from what the user decided. Re-clustering may freely
/// revise an automatic assignment, but must treat a confirmation or a rejection as fixed:
/// those cost the user their attention and cannot be regenerated.
/// </remarks>
public enum Assignment
{
    /// <summary>Assigned by clustering. Revisable without asking.</summary>
    Auto = 0,

    /// <summary>The user confirmed this face belongs to the person.</summary>
    Confirmed = 1,

    /// <summary>The user said this face is not the person it was assigned to.</summary>
    Rejected = 2,

    /// <summary>Too close to call automatically; waiting in the review queue.</summary>
    Pending = 3,
}
