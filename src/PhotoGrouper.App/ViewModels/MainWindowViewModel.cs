using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoGrouper.App.Services;

namespace PhotoGrouper.App.ViewModels;

/// <summary>Shell view model: the workflow steps and which one is showing.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    /// <summary>Index of the settings screen, which sits outside the numbered flow.</summary>
    public const int SettingsIndex = 4;

    public MainWindowViewModel(
        LibraryViewModel library,
        PeopleViewModel people,
        SearchViewModel search,
        OrganiseViewModel organise,
        SettingsViewModel settings,
        LibraryChangedNotifier libraryChanged)
    {
        Library = library;
        People = people;
        Search = search;
        Organise = organise;
        Settings = settings;

        Steps =
        [
            new WorkflowStep(0, "1", "Library"),
            new WorkflowStep(1, "2", "People"),
            new WorkflowStep(2, "3", "Search"),
            new WorkflowStep(3, "4", "Organise"),
        ];

        // The captions read from the screens themselves, so a scan or a grouping updates the flow
        // as it happens rather than only when the user navigates back to it.
        Library.PropertyChanged += OnSectionChanged;
        People.PropertyChanged += OnSectionChanged;
        libraryChanged.Subscribe(() =>
        {
            UpdateCaptions();
            return Task.CompletedTask;
        });

        UpdateSelection();
        UpdateCaptions();
    }

    public LibraryViewModel Library { get; }

    public PeopleViewModel People { get; }

    public SearchViewModel Search { get; }

    public OrganiseViewModel Organise { get; }

    public SettingsViewModel Settings { get; }

    public ObservableCollection<WorkflowStep> Steps { get; }

    [ObservableProperty]
    private int _selectedIndex;

    public bool IsLibraryVisible => SelectedIndex == 0;

    public bool IsPeopleVisible => SelectedIndex == 1;

    public bool IsSearchVisible => SelectedIndex == 2;

    public bool IsOrganiseVisible => SelectedIndex == 3;

    public bool IsSettingsVisible => SelectedIndex == SettingsIndex;

    [RelayCommand]
    private void Select(WorkflowStep? step)
    {
        if (step is { IsAvailable: true })
        {
            SelectedIndex = step.Index;
        }
    }

    [RelayCommand]
    private void ShowSettings() => SelectedIndex = SettingsIndex;

    partial void OnSelectedIndexChanged(int value)
    {
        UpdateSelection();

        // Every screen now stays in the visual tree so that thumbnails and scroll positions
        // survive navigation, which means a screen's Loaded event fires once, at startup, and
        // never again. Anything that reads storage therefore has to refresh on becoming visible
        // instead. Settings showed this plainly: it reported the library as it had been when the
        // window opened, and offered to clear a library it believed was already empty.
        if (value == SettingsIndex)
        {
            _ = Settings.RefreshAsync(CancellationToken.None);
        }
        else if (value == 1)
        {
            _ = People.RefreshAsync(CancellationToken.None);
        }
        else if (value == 3)
        {
            // Both the people to choose from and the list of past runs come from storage, so this
            // screen has to rebuild on becoming visible like the others.
            _ = Organise.RefreshAsync(CancellationToken.None);
        }
        else if (value == 2)
        {
            // The list of people to search among is built from storage, so it has to be rebuilt
            // whenever this screen is shown; somebody who named three people and came straight
            // here would otherwise be offered a list from before they did.
            _ = Search.RefreshAsync(CancellationToken.None);
        }

        OnPropertyChanged(nameof(IsLibraryVisible));
        OnPropertyChanged(nameof(IsPeopleVisible));
        OnPropertyChanged(nameof(IsSearchVisible));
        OnPropertyChanged(nameof(IsOrganiseVisible));
        OnPropertyChanged(nameof(IsSettingsVisible));
    }

    private void UpdateSelection()
    {
        foreach (var step in Steps)
        {
            step.IsCurrent = step.Index == SelectedIndex;
        }
    }

    private void OnSectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Only the counts feed the flow; ignoring everything else keeps a busy progress update
        // from rebuilding the header on every tick.
        if (e.PropertyName is nameof(LibraryViewModel.PhotoCount)
            or nameof(LibraryViewModel.FaceCount)
            or nameof(PeopleViewModel.UnnamedGroupCount)
            or nameof(PeopleViewModel.NamedPeopleCount))
        {
            UpdateCaptions();
        }
    }

    private void UpdateCaptions()
    {
        Steps[0].Caption = Library.PhotoCount == 0
            ? "Add a folder"
            : Library.FaceCount == 0
                ? $"{Library.PhotoCount:N0} photos · detect faces"
                : $"{Library.PhotoCount:N0} photos · {Library.FaceCount:N0} faces";
        Steps[0].IsComplete = Library.FaceCount > 0;

        Steps[1].Caption = People.NamedPeopleCount > 0
            ? $"{People.NamedPeopleCount:N0} named · {People.UnnamedGroupCount:N0} to name"
            : People.UnnamedGroupCount > 0
                ? $"{People.UnnamedGroupCount:N0} groups to name"
                : "Group faces";
        Steps[1].IsComplete = People.NamedPeopleCount > 0;

        // Search is usable the moment somebody has been named, and useless before that, so the
        // caption says which of those is true rather than repeating the step's own title.
        Steps[2].Caption = People.NamedPeopleCount > 0
            ? $"find among {People.NamedPeopleCount:N0} named"
            : "name somebody first";
        Steps[3].Caption = People.NamedPeopleCount > 0
            ? "write folders per person"
            : "name somebody first";
    }
}
