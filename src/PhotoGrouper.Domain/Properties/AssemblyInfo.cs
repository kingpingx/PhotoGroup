using System.Runtime.CompilerServices;

// The monotonic id generator's behaviour under a backwards clock step cannot be reached
// through the public surface without changing the system time.
[assembly: InternalsVisibleTo("PhotoGrouper.Domain.Tests")]
