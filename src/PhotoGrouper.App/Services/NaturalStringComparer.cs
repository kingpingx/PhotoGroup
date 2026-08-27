namespace PhotoGrouper.App.Services;

/// <summary>
/// Compares strings the way a person reads them, treating runs of digits as numbers.
/// </summary>
/// <remarks>
/// Ordinary string comparison puts "10" before "2", because it compares character by character and
/// '1' precedes '2'. That is correct for text and wrong for anything a person will scan as a list:
/// naming people 1 through 16 produced the order 1, 10, 11, 12, 13, 14, 15, 16, 2, 3, which reads
/// as broken even though it is exactly what was asked for.
///
/// Numbers are compared by value rather than converted, so a number too large for any integer type
/// still orders correctly. Leading zeroes are ignored for magnitude and used only to break a tie,
/// which keeps "007" and "7" adjacent rather than far apart.
///
/// Written rather than taken from the platform because the obvious candidate, StrCmpLogicalW, is a
/// Windows API, and this application runs elsewhere too.
/// </remarks>
public sealed class NaturalStringComparer : IComparer<string?>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        int i = 0, j = 0;

        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var comparison = CompareNumbers(x, ref i, y, ref j);
                if (comparison != 0)
                {
                    return comparison;
                }

                continue;
            }

            // Compared one character at a time in a culture-aware way, so that accented letters and
            // case behave as they do everywhere else in the interface.
            var letters = string.Compare(
                x[i].ToString(), y[j].ToString(), StringComparison.CurrentCultureIgnoreCase);

            if (letters != 0)
            {
                return letters;
            }

            i++;
            j++;
        }

        return (x.Length - i).CompareTo(y.Length - j);
    }

    /// <summary>
    /// Compares the runs of digits starting at each position, advancing both past them.
    /// </summary>
    /// <remarks>
    /// Once leading zeroes are skipped, a longer run of digits is the larger number, and runs of
    /// equal length compare character by character. That avoids parsing entirely, so no length of
    /// number can overflow.
    /// </remarks>
    private static int CompareNumbers(string x, ref int i, string y, ref int j)
    {
        var startX = i;
        var startY = j;

        while (i < x.Length && x[i] == '0')
        {
            i++;
        }

        while (j < y.Length && y[j] == '0')
        {
            j++;
        }

        var digitsX = i;
        var digitsY = j;

        while (digitsX < x.Length && char.IsDigit(x[digitsX]))
        {
            digitsX++;
        }

        while (digitsY < y.Length && char.IsDigit(y[digitsY]))
        {
            digitsY++;
        }

        var lengthX = digitsX - i;
        var lengthY = digitsY - j;

        if (lengthX != lengthY)
        {
            i = digitsX;
            j = digitsY;
            return lengthX.CompareTo(lengthY);
        }

        var span = string.CompareOrdinal(x, i, y, j, lengthX);
        i = digitsX;
        j = digitsY;

        if (span != 0)
        {
            return span;
        }

        // Identical in value: the one written with fewer leading zeroes comes first, so that the
        // order is at least stable rather than arbitrary.
        return (i - startX).CompareTo(j - startY);
    }
}
