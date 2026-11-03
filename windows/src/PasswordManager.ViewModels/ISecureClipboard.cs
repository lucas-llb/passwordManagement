namespace PasswordManager.ViewModels;

/// <summary>
/// The system clipboard as <see cref="ClipboardGuard"/> needs it: put a secret on it with the privacy markers, ask
/// whether it still holds that secret, and empty it. The App implements it over the Windows clipboard. Every member
/// runs on the thread that calls the guard (the UI thread), and none of them throws because the clipboard is busy: a
/// busy clipboard shows up as <see langword="false"/> or <see cref="ClipboardMatch.Unavailable"/>, and the guard tries
/// again on its next tick.
/// </summary>
public interface ISecureClipboard
{
    /// <summary>
    /// Replaces the clipboard content with <paramref name="text"/>, marked so that it stays out of the clipboard
    /// history, out of the cloud clipboard and out of clipboard monitors (AGENTS.md §3.6).
    /// </summary>
    /// <param name="text">The secret to copy; never empty.</param>
    /// <returns>True if the clipboard now holds the text; false if it refused (for example because it was busy).</returns>
    bool TrySetSecret(string text);

    /// <summary>Tells whether the clipboard still holds exactly <paramref name="text"/>.</summary>
    /// <param name="text">The secret the app copied.</param>
    /// <returns>The answer, or <see cref="ClipboardMatch.Unavailable"/> if the clipboard could not be read.</returns>
    Task<ClipboardMatch> MatchAsync(string text);

    /// <summary>Empties the clipboard.</summary>
    /// <returns>True if it is empty now; false if it could not be emptied (for example because it was busy).</returns>
    bool TryClear();
}

/// <summary>What <see cref="ISecureClipboard.MatchAsync"/> found on the clipboard.</summary>
public enum ClipboardMatch
{
    /// <summary>The clipboard holds exactly the text that was asked about.</summary>
    Holds,

    /// <summary>The clipboard holds something else, or nothing: the person copied over it, or it was cleared.</summary>
    Differs,

    /// <summary>The clipboard could not be read now (it is busy); the answer is unknown.</summary>
    Unavailable,
}
