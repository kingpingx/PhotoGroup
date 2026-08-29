using Microsoft.Extensions.DependencyInjection;
using PhotoGrouper.App.Services;
using PhotoGrouper.App.ViewModels;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Application.UseCases;
using PhotoGrouper.Infrastructure.FileSystem;
using PhotoGrouper.Infrastructure.Storage.Sqlite;
using PhotoGrouper.Infrastructure.Imaging;
using PhotoGrouper.Infrastructure.Storage.Sqlite.Repositories;
using PhotoGrouper.Infrastructure.Vision;

namespace PhotoGrouper.App.Composition;

/// <summary>
/// The one place where ports are bound to implementations.
/// </summary>
/// <remarks>
/// Every other project depends only on interfaces. Concentrating the bindings here is what
/// makes the storage backend and the vision providers replaceable: swapping SQLite for
/// another store is an edit to this file plus a new adapter project, with no use case,
/// entity or view model touched.
/// </remarks>
public static class CompositionRoot
{
    public static ServiceProvider Build()
    {
        var services = new ServiceCollection();

        var connections = new SqliteConnectionFactory(AppPaths.DatabaseFile);
        new SqliteStore(connections).Initialize();
        services.AddSingleton(connections);

        // Storage adapters. The photo repository satisfies both halves of the split port,
        // registered separately so a consumer can be handed read access alone.
        services.AddSingleton<SqlitePhotoRepository>();
        services.AddSingleton<IPhotoReader>(sp => sp.GetRequiredService<SqlitePhotoRepository>());
        services.AddSingleton<IPhotoWriter>(sp => sp.GetRequiredService<SqlitePhotoRepository>());
        services.AddSingleton<IScanRootRepository, SqliteScanRootRepository>();
        services.AddSingleton<IFaceRepository, SqliteFaceRepository>();
        services.AddSingleton<IPersonRepository, SqlitePersonRepository>();
        services.AddSingleton<IEmbeddingRepository, SqliteEmbeddingRepository>();
        services.AddSingleton<IClusterRepository, SqliteClusterRepository>();
        services.AddSingleton<IFaceLinkRepository, SqliteFaceLinkRepository>();
        services.AddSingleton<IIgnoredFaceRepository, SqliteIgnoredFaceRepository>();
        services.AddSingleton<IPhotoSignatureRepository, SqlitePhotoSignatureRepository>();
        services.AddSingleton<IStoreMaintenance, SqliteStoreMaintenance>();
        services.AddSingleton<IUnitOfWork, SqliteUnitOfWork>();

        // Platform adapters.
        services.AddSingleton<IFileSystem, LocalFileSystem>();
        services.AddSingleton<IClock, SystemClock>();

        // Imaging. The composite decoder is the extension point for new formats: adding RAW
        // means registering another decoder inside it, with nothing else aware the set changed.
        services.AddSingleton<IImageDecoder>(_ => CompositeImageDecoder.CreateDefault());
        services.AddSingleton<IThumbnailCache>(sp => new DiskThumbnailCache(
            AppPaths.ThumbnailCache, sp.GetRequiredService<IImageDecoder>()));

        // Vision. Detectors are registered independently of embedders and paired only at the
        // point of use, so the detector can change without re-embedding anything.
        services.AddSingleton(_ => new ModelStore(AppPaths.Models));
        services.AddSingleton(_ => new OnnxSessionFactory(preferGpu: true));
        services.AddSingleton<DetectorRegistry>();
        services.AddSingleton<IFaceAligner, OpenCvFaceAligner>();

        // Exact search rather than an approximate index. The all-pairs comparison happens once,
        // in the background, and at this scale takes a couple of minutes; an approximate index
        // would trade that for recall tuning and an index file to keep in step with the database.
        services.AddSingleton<IVectorIndex, BruteForceVectorIndex>();

        // Use cases.
        services.AddSingleton<ScanLibraryUseCase>();
        services.AddSingleton<ManageScanRootsUseCase>();
        services.AddSingleton<DetectFacesUseCase>();
        services.AddSingleton<EmbedFacesUseCase>();
        services.AddSingleton<ClusterFacesUseCase>();
        services.AddSingleton<NamePersonUseCase>();
        services.AddSingleton<AutoNameGroupsUseCase>();
        services.AddSingleton<IndexPhotoSignaturesUseCase>();
        services.AddSingleton<FindDuplicatePhotosUseCase>();
        services.AddSingleton<QuarantineDuplicatesUseCase>();
        services.AddSingleton<FindDuplicatePeopleUseCase>();
        services.AddSingleton<FindDuplicateFacesUseCase>();
        services.AddSingleton<MergePeopleUseCase>();
        services.AddSingleton<ResetLibraryUseCase>();
        services.AddSingleton<ManagePeopleUseCase>();
        services.AddSingleton<IgnoreGroupUseCase>();

        // Presentation.
        services.AddSingleton<LibraryChangedNotifier>();
        services.AddSingleton<ThumbnailLoader>();
        services.AddSingleton<FaceOverlayViewModel>();
        services.AddSingleton<DuplicatesViewModel>();
        services.AddSingleton<DuplicatePeopleViewModel>();
        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<PersonDetailViewModel>();
        services.AddSingleton<PeopleViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }
}
