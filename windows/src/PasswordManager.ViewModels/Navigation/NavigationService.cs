namespace PasswordManager.ViewModels.Navigation;

/// <summary>
/// The page stack. It is plain .NET with no thread affinity: callers (the UI thread) serialize the calls. Every change
/// runs in the same order: <see cref="PageViewModel.OnNavigatedFrom"/> of the old page, set <see cref="Current"/>,
/// <see cref="PageViewModel.OnNavigatedTo"/> of the new page, then <see cref="Navigated"/>.
/// </summary>
public sealed class NavigationService : INavigationService
{
    private readonly Stack<PageViewModel> _backStack = new();

    /// <inheritdoc />
    public PageViewModel? Current { get; private set; }

    /// <inheritdoc />
    public bool CanGoBack => _backStack.Count > 0;

    /// <inheritdoc />
    public event EventHandler? Navigated;

    /// <inheritdoc />
    public void NavigateTo(PageViewModel page)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (Current is { } current)
        {
            _backStack.Push(current);
        }

        Show(page);
    }

    /// <inheritdoc />
    public bool GoBack()
    {
        if (!_backStack.TryPop(out var previous))
        {
            return false;
        }

        Show(previous);
        return true;
    }

    /// <inheritdoc />
    public void ResetTo(PageViewModel page)
    {
        ArgumentNullException.ThrowIfNull(page);

        _backStack.Clear();
        Show(page);
    }

    private void Show(PageViewModel page)
    {
        Current?.OnNavigatedFrom();
        Current = page;
        page.OnNavigatedTo();
        Navigated?.Invoke(this, EventArgs.Empty);
    }
}
