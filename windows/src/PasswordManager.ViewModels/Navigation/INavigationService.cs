namespace PasswordManager.ViewModels.Navigation;

/// <summary>
/// Owns the page stack of the app. The view layer only listens to <see cref="Navigated"/> and shows
/// <see cref="Current"/>; it keeps no stack of its own.
/// </summary>
public interface INavigationService
{
    /// <summary>The page being shown, or null before the first <see cref="ResetTo"/>.</summary>
    PageViewModel? Current { get; }

    /// <summary>Whether <see cref="GoBack"/> has a previous page to return to.</summary>
    bool CanGoBack { get; }

    /// <summary>Raised after every change of <see cref="Current"/>.</summary>
    event EventHandler? Navigated;

    /// <summary>Shows <paramref name="page"/> and keeps the current page on the back stack.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="page"/> is null.</exception>
    void NavigateTo(PageViewModel page);

    /// <summary>Returns to the previous page.</summary>
    /// <returns>False, without raising <see cref="Navigated"/>, when there is no previous page.</returns>
    bool GoBack();

    /// <summary>Clears the back stack and makes <paramref name="page"/> the root page.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="page"/> is null.</exception>
    void ResetTo(PageViewModel page);
}
