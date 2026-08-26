using Avalonia.Controls;
using Avalonia.Interactivity;
using PhotoGrouper.App.ViewModels;

namespace PhotoGrouper.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (DataContext is SettingsViewModel model)
        {
            await model.RefreshAsync(CancellationToken.None);
        }
    }
}
