using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using PhotoGrouper.App.Composition;
using PhotoGrouper.App.ViewModels;
using PhotoGrouper.App.Views;

namespace PhotoGrouper.App;

public partial class App : Avalonia.Application
{
    private ServiceProvider? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = CompositionRoot.Build();

            desktop.MainWindow = new MainWindow
            {
                DataContext = _services.GetRequiredService<MainWindowViewModel>(),
            };

            // Disposal is guarded and happens on exit rather than on the shutdown request. The
            // container owns cached bitmaps and native inference sessions, and a failure while
            // tearing those down should not turn an ordinary window close into a crash report.
            desktop.Exit += (_, _) =>
            {
                try
                {
                    _services?.Dispose();
                }
                catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException)
                {
                    // The process is ending; there is nothing left to salvage.
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
