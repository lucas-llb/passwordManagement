using CommunityToolkit.Mvvm.ComponentModel;

namespace PasswordManager.ViewModels;

/// <summary>
/// The one way a secret reaches the clipboard (AGENTS.md §3.6). <see cref="Copy"/> puts it there marked as private
/// through <see cref="ISecureClipboard"/>; <see cref="CheckAsync"/> empties the clipboard once
/// <see cref="ClearAfter"/> has passed, but only if it still holds what the app copied, so something the person
/// copied afterwards is never erased; <see cref="ExpireNow"/> makes the next check clear at once, which is how a lock
/// takes the secret off the clipboard. It has no thread affinity, like <see cref="AutoLock"/>: callers (the UI thread)
/// serialize the calls. The view calls <see cref="CheckAsync"/> from a timer while <see cref="PendingCopy"/> is not 0.
/// </summary>
/// <remarks>
/// Time comes from a <see cref="TimeProvider"/> timestamp, which is monotonic, so changing the wall clock cannot
/// shorten or extend the delay. The guard keeps the copied <see cref="string"/> only until the copy is resolved
/// (cleared, or found to be gone); that is the same managed-string limit as the entry draft.
/// </remarks>
public sealed partial class ClipboardGuard : ObservableObject
{
    private readonly ISecureClipboard _clipboard;
    private readonly TimeProvider _time;

    // The copy waiting to be cleared, or null. It is non-null exactly while PendingCopy is not 0.
    private string? _secret;

    // Monotonic timestamp of the pending copy, and how long after it the clipboard is cleared.
    private long _copiedAt;
    private TimeSpan _delay;

    private long _lastCopyId;

    // True while a check awaits the clipboard: a tick that arrives meanwhile does nothing.
    private bool _checking;

    /// <param name="clipboard">The system clipboard.</param>
    /// <param name="time">The clock of the delay; only its monotonic timestamp is used.</param>
    /// <param name="clearAfter">How long a copy stays on the clipboard; see <see cref="DefaultClearAfter"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="clipboard"/> or <paramref name="time"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="clearAfter"/> is not greater than zero.</exception>
    public ClipboardGuard(ISecureClipboard clipboard, TimeProvider time, TimeSpan clearAfter)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(clearAfter, TimeSpan.Zero);

        _clipboard = clipboard;
        _time = time;
        ClearAfter = clearAfter;
    }

    /// <summary>How long a copy stays on the clipboard: 30 s (AGENTS.md §3.6).</summary>
    public static TimeSpan DefaultClearAfter { get; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a copy stays on the clipboard before <see cref="CheckAsync"/> clears it.</summary>
    public TimeSpan ClearAfter { get; }

    /// <summary>
    /// The id that <see cref="Copy"/> returned for the copy waiting to be cleared, or 0 for none. The view runs its
    /// timer only while this is not 0, and a page compares it with its own copy id to tell whether its copy is the
    /// one on the clipboard.
    /// </summary>
    [ObservableProperty]
    public partial long PendingCopy { get; private set; }

    /// <summary>
    /// Puts <paramref name="secret"/> on the clipboard with the privacy markers and starts the delay again from now,
    /// replacing the previous pending copy (it is no longer on the clipboard).
    /// </summary>
    /// <param name="secret">The text to copy; never empty.</param>
    /// <returns>
    /// The id of the new copy, greater than 0. If the clipboard refuses, 0: the previous pending copy, if any, is
    /// still on the clipboard and stays pending with its own deadline.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="secret"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="secret"/> is null.</exception>
    public long Copy(string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);

        if (!_clipboard.TrySetSecret(secret))
        {
            return 0;
        }

        _secret = secret;
        _copiedAt = _time.GetTimestamp();
        _delay = ClearAfter;

        var id = ++_lastCopyId;
        PendingCopy = id;
        return id;
    }

    /// <summary>
    /// The deadline of the pending copy becomes now, so the next <see cref="CheckAsync"/> clears it. The lock calls
    /// this. With nothing pending it does nothing, and a copy made afterwards gets its full delay.
    /// </summary>
    public void ExpireNow()
    {
        if (_secret is not null)
        {
            _delay = TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Clears the clipboard if the pending copy is due and the clipboard still holds it. Called from the timer tick.
    /// Nothing happens before the deadline, and a call made while another check is waiting for the clipboard returns at
    /// once. If the clipboard holds the copy it is emptied, and the copy is forgotten once that works; if it holds
    /// something else, the copy is forgotten and the clipboard is left alone; if it is busy, or the clear fails, the
    /// copy stays pending and the next tick tries again.
    /// </summary>
    public async Task CheckAsync()
    {
        var id = PendingCopy;
        if (_checking || _secret is not { } secret || _time.GetElapsedTime(_copiedAt) < _delay)
        {
            return;
        }

        _checking = true;
        try
        {
            var match = await _clipboard.MatchAsync(secret);

            // A copy made during the await replaced this one. The clipboard now holds the new secret, so whatever was
            // found about the old one says nothing about it: the new copy keeps its own deadline.
            if (PendingCopy != id)
            {
                return;
            }

            var resolved = match switch
            {
                ClipboardMatch.Holds => _clipboard.TryClear(),
                ClipboardMatch.Differs => true,
                _ => false, // Unavailable: the answer is unknown, so the copy stays pending.
            };

            if (resolved)
            {
                Forget();
            }
        }
        finally
        {
            _checking = false;
        }
    }

    private void Forget()
    {
        _secret = null;
        PendingCopy = 0;
    }
}
