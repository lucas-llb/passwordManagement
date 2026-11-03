using System.Runtime.InteropServices;
using PasswordManager.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace PasswordManager.App;

/// <summary>
/// <see cref="ISecureClipboard"/> over the WinRT clipboard (<c>Windows.ApplicationModel.DataTransfer</c>). Every member
/// runs on the UI thread, which is the thread the <see cref="ClipboardGuard"/> is called from. A busy clipboard (another
/// program holds it open) raises a <see cref="COMException"/>, which becomes <see langword="false"/> or
/// <see cref="ClipboardMatch.Unavailable"/>; nothing else is caught.
/// </summary>
internal sealed class WindowsClipboard : ISecureClipboard
{
    // The format that tells clipboard monitors to leave the content alone. WinRT has no property for it, so it travels
    // as a custom format whose value is a DWORD 0, which is how Windows itself marks sensitive content.
    private const string ExcludeFromMonitorFormat = "ExcludeClipboardContentFromMonitorProcessing";

    // A DWORD is four bytes.
    private const ulong DwordSize = 4;

    // How long the read-back may wait for the text. It answers in milliseconds; the bound only keeps a stuck read from
    // freezing the window.
    private static readonly TimeSpan ReadBackTimeout = TimeSpan.FromSeconds(2);

    /// <inheritdoc/>
    /// <remarks>
    /// The content is set with <see cref="Clipboard.SetContentWithOptions"/>, which keeps it out of the clipboard history
    /// (Win+V) and out of the cloud clipboard. <see cref="Clipboard.Flush"/> is deliberately never called: it would copy
    /// the data into the system's own storage, where it would outlive the app. Without it Windows drops the content
    /// when the app exits, so a closed app leaves no password on the clipboard. Setting does not throw when another
    /// program holds the clipboard: the content is silently dropped. So a set only counts once the clipboard is read back
    /// and holds the text and the marker.
    /// </remarks>
    public bool TrySetSecret(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);

        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            package.SetData(ExcludeFromMonitorFormat, new InMemoryRandomAccessStream { Size = DwordSize });

            Clipboard.SetContentWithOptions(
                package,
                new ClipboardContentOptions { IsAllowedInHistory = false, IsRoamable = false });
            return HoldsSecret(text);
        }
        catch (COMException)
        {
            return false;
        }
        catch (AggregateException ex) when (ex.InnerException is COMException)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<ClipboardMatch> MatchAsync(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);

        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
            {
                return ClipboardMatch.Differs;
            }

            var current = await content.GetTextAsync();
            return string.Equals(current, text, StringComparison.Ordinal) ? ClipboardMatch.Holds : ClipboardMatch.Differs;
        }
        catch (COMException)
        {
            return ClipboardMatch.Unavailable;
        }
    }

    // Whether the clipboard now holds this text together with the marker only a guarded copy carries. A busy clipboard
    // throws on the read (the caller treats it as a refusal); stale or empty content fails the checks.
    private static bool HoldsSecret(string text)
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(ExcludeFromMonitorFormat) || !content.Contains(StandardDataFormats.Text))
        {
            return false;
        }

        var read = content.GetTextAsync().AsTask();
        return read.Wait(ReadBackTimeout) && string.Equals(read.Result, text, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public bool TryClear()
    {
        try
        {
            Clipboard.Clear();
            return true;
        }
        catch (COMException)
        {
            return false;
        }
    }
}
