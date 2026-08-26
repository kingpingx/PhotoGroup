using Avalonia.Threading;

namespace PhotoGrouper.App.Services;

/// <summary>
/// Tells every screen that the library underneath them has changed.
/// </summary>
/// <remarks>
/// Screens hold their own copies of what they display: the library keeps a list of folders and
/// tiles, the people screen keeps groups and names. Anything that changes the library from
/// somewhere else therefore leaves those copies describing a state that no longer exists.
/// Clearing the library made that visible — the folder list stayed on screen afterwards and only
/// disappeared when something else happened to reload it.
///
/// Deliberately a single notification with no payload. The screens already know how to rebuild
/// themselves from storage, and describing what changed would mean every future operation
/// remembering to describe itself correctly.
/// </remarks>
public sealed class LibraryChangedNotifier
{
    private readonly List<Func<Task>> _subscribers = [];

    /// <summary>Registers a screen to be rebuilt whenever the library changes.</summary>
    public void Subscribe(Func<Task> onChanged) => _subscribers.Add(onChanged);

    /// <summary>
    /// Rebuilds every registered screen.
    /// </summary>
    /// <remarks>
    /// Marshalled onto the interface thread because callers may be finishing background work, and
    /// the subscribers touch observable collections that are bound to controls.
    /// </remarks>
    public void NotifyChanged() => Dispatcher.UIThread.Post(async () =>
    {
        foreach (var subscriber in _subscribers.ToArray())
        {
            try
            {
                await subscriber();
            }
            catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
            {
                // One screen failing to rebuild must not stop the others from doing so.
            }
        }
    });
}
