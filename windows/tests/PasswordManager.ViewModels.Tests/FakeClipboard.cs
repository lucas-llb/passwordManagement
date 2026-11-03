namespace PasswordManager.ViewModels.Tests;

/// <summary>
/// An <see cref="ISecureClipboard"/> that lives in memory. It holds the current text and whether the app set it with
/// the privacy markers, and a test can make it refuse a set, report <see cref="ClipboardMatch.Unavailable"/>, fail a
/// clear, or hold <see cref="MatchAsync"/> open until <see cref="ReleaseMatch"/>. The answer of a blocked match is
/// decided when it is asked, like a clipboard read that takes a while. <see cref="SetByUser"/> plays the person
/// copying something else.
/// </summary>
internal sealed class FakeClipboard : ISecureClipboard
{
    private TaskCompletionSource? _gate;

    /// <summary>The text on the clipboard, or null when it is empty.</summary>
    public string? Text { get; private set; }

    /// <summary>True when the current text was set through <see cref="TrySetSecret"/>, so it carries the privacy markers.</summary>
    public bool IsMarkedPrivate { get; private set; }

    /// <summary>When true, <see cref="TrySetSecret"/> refuses and leaves the clipboard as it is.</summary>
    public bool RefuseSet { get; set; }

    /// <summary>When true, <see cref="MatchAsync"/> answers <see cref="ClipboardMatch.Unavailable"/>.</summary>
    public bool Unavailable { get; set; }

    /// <summary>When true, <see cref="TryClear"/> fails and leaves the clipboard as it is.</summary>
    public bool FailClear { get; set; }

    /// <summary>When set, <see cref="MatchAsync"/> throws it, to show the guard recovers from a failed read.</summary>
    public Exception? MatchFailure { get; set; }

    public int SetCalls { get; private set; }

    public int MatchCalls { get; private set; }

    public int ClearCalls { get; private set; }

    /// <summary>The person copies <paramref name="text"/> in another app: no markers, nothing from this app.</summary>
    public void SetByUser(string text)
    {
        Text = text;
        IsMarkedPrivate = false;
    }

    /// <summary>The next <see cref="MatchAsync"/> calls do not complete until <see cref="ReleaseMatch"/>.</summary>
    public void BlockMatch() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Lets the blocked <see cref="MatchAsync"/> calls complete with the answer they decided when they started.</summary>
    public void ReleaseMatch()
    {
        _gate?.TrySetResult();
        _gate = null;
    }

    public bool TrySetSecret(string text)
    {
        SetCalls++;
        if (RefuseSet)
        {
            return false;
        }

        Text = text;
        IsMarkedPrivate = true;
        return true;
    }

    public async Task<ClipboardMatch> MatchAsync(string text)
    {
        MatchCalls++;
        if (MatchFailure is { } failure)
        {
            throw failure;
        }

        var answer = Unavailable
            ? ClipboardMatch.Unavailable
            : string.Equals(Text, text, StringComparison.Ordinal) ? ClipboardMatch.Holds : ClipboardMatch.Differs;

        if (_gate is { } gate)
        {
            await gate.Task;
        }

        return answer;
    }

    public bool TryClear()
    {
        ClearCalls++;
        if (FailClear)
        {
            return false;
        }

        Text = null;
        IsMarkedPrivate = false;
        return true;
    }
}
