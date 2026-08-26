namespace PhotoGrouper.Domain.People;

/// <summary>
/// The name a user gave to a person.
/// </summary>
/// <remarks>
/// Enforces only what is universally true of a name: present, trimmed, bounded. The rules
/// for turning a name into a folder on disk, such as Windows reserved device names and
/// illegal characters, deliberately live in the filesystem adapter instead. Those are
/// properties of NTFS, not of a person, and encoding them here would put a platform
/// detail in the innermost layer.
/// </remarks>
public readonly record struct PersonName
{
    public const int MaxLength = 100;

    private PersonName(string value) => Value = value;

    public string Value { get; }

    public static PersonName Create(string value)
    {
        if (!TryCreate(value, out var name, out var error))
        {
            throw new ArgumentException(error, nameof(value));
        }

        return name;
    }

    public static bool TryCreate(string? value, out PersonName name, out string? error)
    {
        name = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "A person's name cannot be empty.";
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaxLength)
        {
            error = $"A person's name cannot exceed {MaxLength} characters.";
            return false;
        }

        name = new PersonName(trimmed);
        error = null;
        return true;
    }

    public override string ToString() => Value;
}
