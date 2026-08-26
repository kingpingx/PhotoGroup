using System.Runtime.CompilerServices;

// Path normalisation is an internal detail of adding a folder, but it is also where the same
// folder being added twice is prevented, and that is worth pinning directly rather than only
// through the use case that calls it.
[assembly: InternalsVisibleTo("PhotoGrouper.Application.Tests")]
