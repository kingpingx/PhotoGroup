using CommunityToolkit.Mvvm.ComponentModel;

namespace PhotoGrouper.App.ViewModels;

/// <summary>
/// One stage of the journey from a folder of files to a folder per person.
/// </summary>
/// <remarks>
/// The application is a sequence, not a set of unrelated screens: photographs have to be found
/// before faces can be, faces before people, people before anything can be organised by them. A
/// list of destinations down one side says nothing about that order, and gives a new user no idea
/// which one to press first or whether the previous step actually worked.
///
/// Presenting the same screens as a numbered flow makes the order visible, and the running caption
/// on each step reports what that stage has actually produced. "21 photos" under Library and
/// "18 faces" under Detect answer, without being asked, the question that keeps coming up: did
/// that do anything?
/// </remarks>
public sealed partial class WorkflowStep(int index, string number, string title, bool isAvailable = true)
    : ObservableObject
{
    /// <summary>Position in the flow, and the index of the screen it shows.</summary>
    public int Index { get; } = index;

    public string Number { get; } = number;

    public string Title { get; } = title;

    /// <summary>False for stages that are planned but not yet built.</summary>
    public bool IsAvailable { get; } = isAvailable;

    /// <summary>What this stage has produced so far, shown beneath its title.</summary>
    [ObservableProperty]
    private string _caption = string.Empty;

    [ObservableProperty]
    private bool _isCurrent;

    /// <summary>
    /// True once this stage has produced something.
    /// </summary>
    /// <remarks>
    /// Drives the tick on the step marker. Derived from real counts rather than from whether the
    /// user has visited the screen, so it reports progress rather than navigation.
    /// </remarks>
    [ObservableProperty]
    private bool _isComplete;
}
