using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.ViewModels;

/// <summary>
/// Locks the open vault by itself: after <see cref="AutoLockOptions.IdleTimeout"/> with no input to the app window,
/// when the Windows session locks, and, if <see cref="AutoLockOptions.LockOnMinimize"/> is on, when the window is
/// minimized. Locking runs the list's own <see cref="EntryListViewModel.LockCommand"/>, so it ends on the unlock page
/// with the stack <c>[Profiles, Unlock]</c>, exactly like the Lock button. With no vault open every trigger does
/// nothing. It has no thread affinity, like <see cref="NavigationService"/>: callers (the UI thread) serialize the
/// calls. The view feeds it the input and window events and calls <see cref="CheckIdle"/> from a timer.
/// </summary>
public sealed partial class AutoLock : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly TimeProvider _time;

    // The list of the open vault, or null. Dropped as soon as that vault is locked, so nothing keeps it alive.
    private EntryListViewModel? _vault;

    // Monotonic timestamp of the last input, or of the moment the vault opened.
    private long _lastActivity;

    // The unlock that was running when a trigger arrived: the vault it opens is locked as soon as it ends.
    private IAsyncRelayCommand? _pendingUnlock;

    /// <param name="navigation">The page stack; the open vault is the <see cref="EntryListViewModel"/> it shows.</param>
    /// <param name="time">The clock of the idle timeout; only its monotonic timestamp is used.</param>
    /// <param name="options">When to lock.</param>
    public AutoLock(INavigationService navigation, TimeProvider time, AutoLockOptions options)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);

        _navigation = navigation;
        _time = time;
        Options = options;
        _lastActivity = time.GetTimestamp();

        _navigation.Navigated += OnNavigated;
        Refresh();
    }

    /// <summary>When to lock.</summary>
    public AutoLockOptions Options { get; }

    /// <summary>
    /// True while a vault is open, on its list or on a page above it. The view runs the idle timer only while this is
    /// true.
    /// </summary>
    [ObservableProperty]
    public partial bool IsVaultOpen { get; private set; }

    /// <summary>Records input to the app window: the idle timeout starts again from now.</summary>
    public void ReportActivity() => _lastActivity = _time.GetTimestamp();

    /// <summary>Locks the open vault if the idle timeout has passed since the last input or since it opened.</summary>
    public void CheckIdle()
    {
        if (_vault is { IsOpen: true } && _time.GetElapsedTime(_lastActivity) >= Options.IdleTimeout)
        {
            Lock();
        }
    }

    /// <summary>The Windows session locked (or disconnected): the open vault locks at once.</summary>
    public void OnSessionLocked() => Lock();

    /// <summary>The window was minimized: the open vault locks if <see cref="AutoLockOptions.LockOnMinimize"/> is on.</summary>
    public void OnMinimized()
    {
        if (Options.LockOnMinimize)
        {
            Lock();
        }
    }

    private void OnNavigated(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        if (_navigation.Current is EntryListViewModel { IsOpen: true } list && !ReferenceEquals(list, _vault))
        {
            // A vault was just opened: the idle clock starts now, whatever happened before.
            _vault = list;
            _lastActivity = _time.GetTimestamp();
        }
        else if (_vault is { IsOpen: false })
        {
            _vault = null;
        }

        IsVaultOpen = _vault is not null;
    }

    private void Lock()
    {
        if (_vault is { IsOpen: true } vault)
        {
            vault.LockCommand.Execute(null);
            return;
        }

        // Argon2id is running: the trigger applies to the vault this unlock opens, and to nothing later. Locking here
        // would race the unlock's own navigation, so it waits for the command to end.
        if (_pendingUnlock is null
            && _navigation.Current is UnlockViewModel { UnlockCommand: { IsRunning: true } command })
        {
            _pendingUnlock = command;
            command.PropertyChanged += OnPendingUnlockChanged;

            // The unlock may have ended between the check and the subscription.
            if (!command.IsRunning)
            {
                EndPendingUnlock(command);
            }
        }
    }

    private void OnPendingUnlockChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IAsyncRelayCommand.IsRunning)
            && sender is IAsyncRelayCommand { IsRunning: false } command)
        {
            EndPendingUnlock(command);
        }
    }

    private void EndPendingUnlock(IAsyncRelayCommand command)
    {
        command.PropertyChanged -= OnPendingUnlockChanged;

        // One shot: whichever path ends it first applies the lock, and only once.
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _pendingUnlock, null, command), command))
        {
            return;
        }

        if (_vault is { IsOpen: true } vault)
        {
            vault.LockCommand.Execute(null);
        }
    }
}
