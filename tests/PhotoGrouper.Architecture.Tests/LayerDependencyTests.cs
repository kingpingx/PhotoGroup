using System.Reflection;
using FluentAssertions;
using NetArchTest.Rules;

namespace PhotoGrouper.Architecture.Tests;

/// <summary>
/// Enforces the layer dependency rule as a build failure rather than a convention.
/// </summary>
/// <remarks>
/// A clean architecture erodes one convenient using directive at a time, and the erosion is
/// invisible in review because each individual step looks harmless. These tests exist so
/// that the first such step fails loudly, at the point it is made, rather than being
/// discovered a year later when the storage backend turns out not to be swappable after all.
///
/// Written against assembly names rather than project references because that is what
/// actually constrains the compiled output.
/// </remarks>
public sealed class LayerDependencyTests
{
    private static readonly Assembly Domain = typeof(Domain.Identity.PhotoId).Assembly;

    // Referencing a type from each adapter forces the assembly to load, so the reflection below
    // finds them rather than silently passing because nothing had touched them yet.
    private static readonly Assembly[] Adapters =
    [
        typeof(Infrastructure.Storage.Sqlite.SqliteStore).Assembly,
        typeof(Infrastructure.Vision.ModelStore).Assembly,
        typeof(Infrastructure.Imaging.MatBridge).Assembly,
        typeof(Infrastructure.FileSystem.SystemClock).Assembly,
    ];

    private static readonly Assembly Application = typeof(Application.Ports.IPhotoReader).Assembly;

    private const string DomainAssembly = "PhotoGrouper.Domain";
    private const string ApplicationAssembly = "PhotoGrouper.Application";

    /// <summary>Third-party namespaces that must never appear in the inner two layers.</summary>
    private static readonly string[] ForbiddenInInnerLayers =
    [
        "Avalonia",
        "OpenCvSharp",
        "Microsoft.ML.OnnxRuntime",
        "Microsoft.Data.Sqlite",
        "ImageMagick",
        "MongoDB",
        "Microsoft.Extensions.DependencyInjection",
    ];

    [Fact]
    public void Domain_depends_on_nothing_but_the_base_class_library()
    {
        var referenced = Domain.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => name.StartsWith("PhotoGrouper", StringComparison.Ordinal))
            .ToArray();

        referenced.Should().BeEmpty(
            "the domain is the innermost layer; anything it references becomes a dependency of everything");
    }

    [Fact]
    public void Application_depends_only_on_Domain()
    {
        var referenced = Application.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => name.StartsWith("PhotoGrouper", StringComparison.Ordinal))
            .ToArray();

        referenced.Should().BeEquivalentTo([DomainAssembly],
            "use cases may use entities, but must reach the outside world only through ports");
    }

    [Theory]
    [MemberData(nameof(InnerLayerAssemblies))]
    public void Inner_layers_reference_no_framework_or_infrastructure_package(string assemblyName)
    {
        var assembly = assemblyName == DomainAssembly ? Domain : Application;

        var offenders = assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => ForbiddenInInnerLayers.Any(f => name.StartsWith(f, StringComparison.Ordinal)))
            .ToArray();

        offenders.Should().BeEmpty(
            $"{assemblyName} must stay testable without a UI, a database, or a native imaging library");
    }

    public static TheoryData<string> InnerLayerAssemblies() => new() { DomainAssembly, ApplicationAssembly };

    [Fact]
    public void Domain_types_do_not_depend_on_Application_types()
    {
        var result = Types.InAssembly(Domain)
            .ShouldNot()
            .HaveDependencyOn(ApplicationAssembly)
            .GetResult();

        result.FailingTypeNames.Should().BeNullOrEmpty("dependencies point inward, never outward");
    }

    /// <summary>
    /// Infrastructure adapters must not know about one another, with one stated exception.
    /// </summary>
    /// <remarks>
    /// The plan called for no cross-references at all between adapters. Vision breaks that: it
    /// needs the conversion between the domain's pixel buffer and OpenCV's Mat, which lives in
    /// Imaging. The alternatives were to duplicate that conversion or to add a project holding a
    /// single class, and neither is an improvement on one documented edge.
    ///
    /// The rule that genuinely matters is upheld: storage knows nothing of vision or imaging, and
    /// vision knows nothing of storage. A defect in one cannot propagate into the other, and
    /// either can be replaced without touching its neighbour.
    /// </remarks>
    [Theory]
    [InlineData("PhotoGrouper.Infrastructure.Storage.Sqlite", new[] { "Vision", "Imaging", "FileSystem" })]
    [InlineData("PhotoGrouper.Infrastructure.Vision", new[] { "Storage.Sqlite", "FileSystem" })]
    [InlineData("PhotoGrouper.Infrastructure.FileSystem", new[] { "Storage.Sqlite", "Vision", "Imaging" })]
    [InlineData("PhotoGrouper.Infrastructure.Imaging", new[] { "Storage.Sqlite", "Vision", "FileSystem" })]
    public void Infrastructure_adapters_stay_independent(string assemblyName, string[] forbidden)
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == assemblyName)
            ?? Assembly.Load(assemblyName);

        var referenced = assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

        foreach (var name in forbidden)
        {
            referenced.Should().NotContain($"PhotoGrouper.Infrastructure.{name}",
                $"{assemblyName} must be replaceable without disturbing the other adapters");
        }
    }

    /// <remarks>
    /// The one rule here about presentation rather than layering. A view model that can reach a
    /// repository will eventually do its own querying, which moves rules out of the use cases and
    /// into a place that cannot be tested without spinning up a window.
    /// </remarks>
    [Fact]
    public void ViewModels_do_not_depend_on_repositories_or_storage()
    {
        var app = typeof(PhotoGrouper.App.ViewModels.LibraryViewModel).Assembly;

        var result = Types.InAssembly(app)
            .That().HaveNameEndingWith("ViewModel")
            .ShouldNot()
            .HaveDependencyOnAny("PhotoGrouper.Infrastructure.Storage.Sqlite", "Microsoft.Data.Sqlite")
            .GetResult();

        result.FailingTypeNames.Should().BeNullOrEmpty(
            "view models talk to use cases; letting them reach storage directly hollows out the application layer");
    }
}
