using CommunityToolkit.Mvvm.ComponentModel;

namespace PasswordManager.ViewModels;

/// <summary>
/// Base class of the ViewModels that the navigation service shows as a page. The service calls the two hooks around
/// every change of the current page, on the thread that called it (the UI thread in the app).
/// </summary>
public abstract class PageViewModel : ObservableObject
{
    /// <summary>Called after this page becomes the current one and before <c>Navigated</c> is raised.</summary>
    public virtual void OnNavigatedTo()
    {
    }

    /// <summary>Called when this page stops being the current one, before the next page is set.</summary>
    public virtual void OnNavigatedFrom()
    {
    }
}
