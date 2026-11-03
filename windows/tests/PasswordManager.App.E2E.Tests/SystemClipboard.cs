using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;

namespace PasswordManager.App.E2E.Tests;

// The user's real clipboard, as another program sees it (W-22). The reads go through Win32, which shows the formats the
// app put there exactly as a clipboard monitor or the history service gets them. Writing needs an owner window, which a
// test process has none of, so the only writes are WinRT ones, made on a dedicated STA thread, as is every history call.
// Every failure message names the check, never a value: the values are seeded passwords and fictitious markers.
internal static class SystemClipboard
{
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StaTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(20);

    // The text on the clipboard, or null when it holds no text.
    public static string? ReadText() => WithOpen(() =>
    {
        if (!NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_UNICODETEXT))
        {
            return null;
        }

        return ReadGlobal(NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT), (pointer, size) =>
        {
            var chars = new char[(int)(size / sizeof(char))];
            Marshal.Copy(pointer, chars, 0, chars.Length);
            var end = Array.IndexOf(chars, '\0');
            return new string(chars, 0, end >= 0 ? end : chars.Length);
        });
    });

    // Whether the clipboard offers this registered format (any value).
    public static bool HasFormat(string name) => NativeMethods.IsClipboardFormatAvailable(Register(name));

    // The first four bytes of this registered format as a DWORD, or null when the format is absent or shorter.
    public static uint? ReadDword(string name)
    {
        var format = Register(name);
        return WithOpen(() => NativeMethods.IsClipboardFormatAvailable(format)
            ? ReadGlobal(NativeMethods.GetClipboardData(format), (pointer, size) =>
                size >= sizeof(uint) ? (uint?)unchecked((uint)Marshal.ReadInt32(pointer)) : null)
            : null);
    }

    // Empties the clipboard, so a test starts from nothing it could mistake for the app's copy.
    public static void Clear() => WithOpen(() =>
    {
        Assert.True(NativeMethods.EmptyClipboard(), $"EmptyClipboard failed with error {Marshal.GetLastPInvokeError()}.");
        return true;
    });

    // Keeps the clipboard open until disposed, so every other program (the app included) finds it busy. Dispose it on
    // the thread that opened it: Win32 ties the open clipboard to that thread.
    public static IDisposable HoldOpen()
    {
        Open();
        return new Holder();
    }

    // Puts plain text on the clipboard, as a user's copy in another program does, and flushes it so it outlives the
    // helper thread. It is allowed in the history, so it reaches Win+V.
    public static void SetText(string text) => OnSta(() =>
    {
        var package = new DataPackage();
        package.SetText(text);
        var deadline = DateTime.UtcNow + OpenTimeout;
        while (true)
        {
            try
            {
                Clipboard.SetContent(package);
                Clipboard.Flush();
                return true;
            }
            catch (COMException) when (DateTime.UtcNow < deadline)
            {
                // Another program holds the clipboard for a moment; try again.
                Thread.Sleep(Poll);
            }
        }
    });

    public static bool IsHistoryEnabled() => OnSta(Clipboard.IsHistoryEnabled);

    public static bool HistoryContains(string text) => OnSta(() => HistoryItems(text).Count > 0);

    // Deletes every history item that holds exactly this text; the number deleted.
    public static int DeleteFromHistory(string text) => OnSta(() =>
    {
        var deleted = 0;
        foreach (var item in HistoryItems(text))
        {
            if (Clipboard.DeleteItemFromHistory(item))
            {
                deleted++;
            }
        }

        return deleted;
    });

    // The history items whose text is exactly this one, compared ordinally. Runs on the STA thread.
    private static List<ClipboardHistoryItem> HistoryItems(string text)
    {
        var result = Clipboard.GetHistoryItemsAsync().AsTask().GetAwaiter().GetResult();
        Assert.True(
            result.Status == ClipboardHistoryItemsResultStatus.Success,
            $"The clipboard history could not be read: {result.Status}.");

        var matches = new List<ClipboardHistoryItem>();
        foreach (var item in result.Items)
        {
            if (item.Content.Contains(StandardDataFormats.Text)
                && string.Equals(item.Content.GetTextAsync().AsTask().GetAwaiter().GetResult(), text, StringComparison.Ordinal))
            {
                matches.Add(item);
            }
        }

        return matches;
    }

    private static uint Register(string name)
    {
        var format = NativeMethods.RegisterClipboardFormatW(name);
        Assert.True(format != 0, $"RegisterClipboardFormatW failed with error {Marshal.GetLastPInvokeError()}.");
        return format;
    }

    // Another program (the app, the history service) may hold the clipboard for a moment: retry the open for a while.
    private static void Open()
    {
        var deadline = DateTime.UtcNow + OpenTimeout;
        while (!NativeMethods.OpenClipboard(nint.Zero))
        {
            var error = Marshal.GetLastPInvokeError();
            Assert.True(DateTime.UtcNow < deadline, $"The clipboard stayed busy; OpenClipboard failed with error {error}.");
            Thread.Sleep(Poll);
        }
    }

    private static T WithOpen<T>(Func<T> read)
    {
        Open();
        try
        {
            return read();
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    // A clipboard handle is a global memory block; a null one (a format whose owner could not render it) reads as null.
    private static T? ReadGlobal<T>(nint handle, Func<nint, nuint, T> read)
    {
        if (handle == nint.Zero)
        {
            return default;
        }

        var pointer = NativeMethods.GlobalLock(handle);
        if (pointer == nint.Zero)
        {
            return default;
        }

        try
        {
            return read(pointer, NativeMethods.GlobalSize(handle));
        }
        finally
        {
            NativeMethods.GlobalUnlock(handle);
        }
    }

    // The WinRT clipboard is OLE's, which wants an STA thread; a fresh one per call keeps the test thread out of it.
    private static T OnSta<T>(Func<T> action)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
#pragma warning disable CA1031 // Any failure, assertions included, is rethrown on the test thread below.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(StaTimeout), "A WinRT clipboard call did not return.");
        failure?.Throw();
        return result;
    }

    private sealed class Holder : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            NativeMethods.CloseClipboard();
        }
    }
}
