using Microsoft.Extensions.DependencyInjection;
using PhotoGrouper.App.Services;
using PhotoGrouper.App.ViewModels;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Application.UseCases;
using PhotoGrouper.Infrastructure.FileSystem;
using PhotoGrouper.Infrastructure.Storage.Sqlite;
using PhotoGrouper.Infrastructure.Storage.Sqlite.Repositories;

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
        services.AddSingleton<IUnitOfWork, SqliteUnitOfWork>();

        // Platform adapters.
        services.AddSingleton<IFileSystem, WindowsFileSystem>();
        services.AddSingleton<IClock, SystemClock>();

        // Use cases.
        services.AddSingleton<ScanLibraryUseCase>();
        services.AddSingleton<ManageScanRootsUseCase>();

        // Presentation.
        services.AddSingleton<ThumbnailLoader>();
        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }
}
