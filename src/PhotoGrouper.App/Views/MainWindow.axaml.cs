using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PhotoGrouper.App.ViewModels;

namespace PhotoGrouper.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Handled at the window during the tunnelling pass rather than on the overlays themselves.
        // An overlay is a Border with no focus of its own, so a key press never reaches it; and a
        // text box inside a panel would swallow Escape before anything else saw it. Tunnelling from
        // the top means the shortcuts work wherever the caret happens to be.
        AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);
    }

    private MainWindowViewModel? Shell => DataContext as MainWindowViewModel;

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (Shell is not { } shell)
        {
            return;
        }

        // Only ever act on an overlay that is actually open, so these keys stay available to the
        // rest of the application the rest of the time.
        if (shell.Library.Overlay.IsVisible)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    e.Handled = true;
                    shell.Library.CloseOverlayCommand.Execute(null);
                    return;

                case Key.Left:
                    e.Handled = true;
                    shell.Library.ShowPreviousCommand.Execute(null);
                    return;

                case Key.Right:
                    e.Handled = true;
                    shell.Library.ShowNextCommand.Execute(null);
                    return;
            }
        }

        // Escape closes the person panel too, but not while a name is being typed: leaving the
        // rename box should not also throw away the panel.
        if (shell.People.Detail.IsOpen
            && e.Key == Key.Escape
            && FocusManager?.GetFocusedElement() is not TextBox)
        {
            e.Handled = true;
            shell.People.Detail.CloseCommand.Execute(null);
        }
    }
}
