using System.Runtime.CompilerServices;

// The landmark reordering each detector performs is the single most consequential line in its
// adapter, and its only observable effect is on embedding quality much further downstream.
// Exposing it lets a test pin it against a synthetic model row.
[assembly: InternalsVisibleTo("PhotoGrouper.Infrastructure.Tests")]
