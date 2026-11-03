using System.Diagnostics;
using System.Globalization;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using PasswordManager.App.E2E.Tests.Localization;

namespace PasswordManager.App.E2E.Tests;

// "Copiar senha" and Ctrl+C on the shown password (W-22), on the real app over the seeded vault and the user's real
// clipboard: the three privacy markers as Win32 sees them, the real clipboard history (Win+V) never getting the
// password, the clear after the timeout (short, through the Debug switch) and on lock and app close, a later copy by the
// user kept, and a busy clipboard. Every value is fictitious and compared, never printed; every step waits on a
// condition, except the bounded window in which the history must not get the password.
public sealed class ClipboardE2ETests
{
    private const int ShortClearSeconds = 3;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LockOrCloseTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MinimumAbsenceWindow = TimeSpan.FromSeconds(3);

    private static string Password => SeededVault.Entries[0].GetProperty("password").GetString()!;

    private static string Title => SeededVault.Entries[0].GetProperty("title").GetString()!;

    private static string[] ClearAfter(int seconds) =>
        ["--clipboard-clear-seconds", seconds.ToString(CultureInfo.InvariantCulture)];

    [Fact]
    public void CopyPassword_SetsPrivacyMarkers_AndClearsAfterTimeout()
    {
        using var app = AppSession.Launch(SeededVault.Seed, ClearAfter(ShortClearSeconds));
        OpenFirstEntry(app);
        StartEmpty();

        var copied = Stopwatch.StartNew();
        app.Invoke("DetailCopyPassword");
        app.WaitFor("DetailPasswordCopied");
        Assert.Null(app.Find("DetailPasswordText"));

        WaitClipboard(Holds(Password), Timeout, "The clipboard does not hold the password after Copiar senha.");
        AssertPrivacyMarkers();

        WaitClipboard(text => text is null, Timeout, "The clipboard still holds text after the timeout.");
        Assert.True(
            copied.Elapsed >= TimeSpan.FromSeconds(ShortClearSeconds - 0.5),
            $"The clipboard was cleared after {copied.Elapsed.TotalMilliseconds:F0} ms, before the timeout.");
        app.WaitGone("DetailPasswordCopied");
    }

    [Fact]
    public void CopyPassword_HistoryOn_NeverEntersHistory_AndLaterCopyIsKept()
    {
        // The markers go in before the app starts, so this flow skips in Release on its own, as AppSession.Launch does.
#if !DEBUG
        Assert.Skip("E2E tests run only in Debug: the --vaults-dir hook exists only in the Debug app.");
#endif
        Assert.True(
            SystemClipboard.IsHistoryEnabled(),
            "The clipboard history (Win+V) is off, so this test cannot prove anything. Turn it on in Settings > System > "
            + @"Clipboard (HKCU\Software\Microsoft\Clipboard\EnableClipboardHistory = 1) and run it again.");

        var markerA = Marker();
        var markerB = Marker();
        var passwordSeen = false;
        try
        {
            // A run killed before its cleanup can leave the password in the history; it must start without it.
            SystemClipboard.DeleteFromHistory(Password);
            Assert.False(SystemClipboard.HistoryContains(Password), "The password is in the clipboard history before the test.");

            // Positive control: plain text from another program does reach the history, and this is how long it takes.
            var sinceA = Stopwatch.StartNew();
            SystemClipboard.SetText(markerA);
            WaitHistory(markerA, "Marker A never reached the clipboard history: the history service is not recording.");
            var latency = sinceA.Elapsed;
            var window = TimeSpan.FromTicks(Math.Max(MinimumAbsenceWindow.Ticks, 3 * latency.Ticks));

            // The copy must still be pending when marker B replaces it (so the guard really meets "the user copied
            // something else"): the timeout covers the absence window twice, which leaves the barrier 3 × latency.
            var clearSeconds = (int)Math.Ceiling((2 * window).TotalSeconds) + 2;
            using var app = AppSession.Launch(SeededVault.Seed, ClearAfter(clearSeconds));
            OpenFirstEntry(app);

            app.Invoke("DetailCopyPassword");
            app.WaitFor("DetailPasswordCopied");
            WaitClipboard(Holds(Password), Timeout, "The clipboard does not hold the password after Copiar senha.");

            // The bounded absence window, the only fixed wait of the suite: the history never gets the password.
            var watching = Stopwatch.StartNew();
            while (watching.Elapsed < window)
            {
                passwordSeen |= SystemClipboard.HistoryContains(Password);
                Assert.False(passwordSeen, "The password entered the clipboard history.");
                Thread.Sleep(Poll);
            }

            // The user copies something else while the copy waits. Marker B reaching the history is the barrier that
            // proves the service was alive the whole time; the password is still not there.
            SystemClipboard.SetText(markerB);
            Assert.True(app.Find("DetailPasswordCopied") is not null, "The copy was resolved before the user's later copy.");
            WaitHistory(markerB, "Marker B never reached the clipboard history.");
            passwordSeen |= SystemClipboard.HistoryContains(Password);
            Assert.False(passwordSeen, "The password entered the clipboard history.");

            // At the deadline the guard sees other content: it forgets the copy and leaves the user's text alone.
            app.WaitGone("DetailPasswordCopied");
            Assert.True(Holds(markerB)(SystemClipboard.ReadText()), "The guard cleared or changed the user's later copy.");
            passwordSeen |= SystemClipboard.HistoryContains(Password);
            Assert.False(passwordSeen, "The password entered the clipboard history.");
        }
        finally
        {
            // Every marker leaves the history, and so does the password should it ever have got there.
            SystemClipboard.DeleteFromHistory(markerA);
            SystemClipboard.DeleteFromHistory(markerB);
            SystemClipboard.DeleteFromHistory(Password);
        }
    }

    [Fact]
    public void CopyPassword_ThenSessionLock_ClearsBeforeTimeout()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        OpenFirstEntry(app);
        StartEmpty();

        app.Invoke("DetailCopyPassword");
        WaitClipboard(Holds(Password), Timeout, "The clipboard does not hold the password after Copiar senha.");

        app.PostSessionChange(NativeMethods.WTS_SESSION_LOCK);
        app.WaitFor("UnlockPassword");
        WaitClipboard(
            text => !Holds(Password)(text), LockOrCloseTimeout, "The clipboard still holds the password after the lock.");
    }

    [Fact]
    public void CopyPassword_ThenAppCloses_PasswordLeavesClipboard()
    {
        var app = AppSession.Launch(SeededVault.Seed);
        try
        {
            OpenFirstEntry(app);
            StartEmpty();

            app.Invoke("DetailCopyPassword");
            WaitClipboard(Holds(Password), Timeout, "The clipboard does not hold the password after Copiar senha.");
        }
        finally
        {
            app.Dispose();
        }

        WaitClipboard(
            text => !Holds(Password)(text), LockOrCloseTimeout, "The clipboard still holds the password after the app closed.");
    }

    [Fact]
    public void ShownPassword_CtrlC_GoesThroughGuard()
    {
        using var app = AppSession.Launch(SeededVault.Seed, ClearAfter(ShortClearSeconds));
        OpenFirstEntry(app);
        app.Toggle("DetailShowPassword");
        app.WaitFor("DetailPasswordText").Focus();
        app.WaitFocus("DetailPasswordText");
        StartEmpty();

        // The message first: it is shown only until the short timeout clears the copy.
        app.PressChord(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        app.PressChord(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_C);
        app.WaitFor("DetailPasswordCopied");
        WaitClipboard(Holds(Password), Timeout, "The clipboard does not hold the password after Ctrl+C.");
        AssertPrivacyMarkers();

        // Ctrl+X is the same guarded copy and never deletes: the text and the draft stay as they were.
        StartEmpty();
        app.PressChord(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_X);
        WaitClipboard(Holds(Password), Timeout, "The clipboard does not hold the password after Ctrl+X.");
        AssertPrivacyMarkers();
        Assert.True(
            string.Equals(app.ValueOf("DetailPasswordText"), Password, StringComparison.Ordinal),
            "Ctrl+X changed the shown password.");
        Assert.False(app.IsEnabled("DetailSave"), "Ctrl+X left unsaved changes.");

        WaitClipboard(text => text is null, Timeout, "The clipboard still holds text after the timeout.");
        app.WaitGone("DetailPasswordCopied");
    }

    [Fact]
    public void NewEntry_CopyDisabledUntilPassword_ThenEnterCopies()
    {
        const string password = "Senha-nova-fictícia-789";
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app);
        app.Invoke("EntriesNew");
        app.WaitFor("DetailTitle");
        app.WaitEnabled("DetailCopyPassword", expected: false);

        app.SetValue("DetailPassword", password);
        app.WaitEnabled("DetailCopyPassword");
        StartEmpty();

        app.WaitFor("DetailCopyPassword").Focus();
        app.WaitFocus("DetailCopyPassword");
        app.PressKey(VirtualKeyShort.ENTER);
        WaitClipboard(Holds(password), Timeout, "The clipboard does not hold the draft password after Enter.");
        app.WaitFor("DetailPasswordCopied");
        app.WaitFocus("DetailCopyPassword");
    }

    [Fact]
    public void CopyPassword_ClipboardBusy_ShowsError()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        OpenFirstEntry(app);
        StartEmpty();

        using (SystemClipboard.HoldOpen())
        {
            app.Invoke("DetailCopyPassword");
            Assert.Equal(AppResources.ExpectedName("DetailCopyFailed"), app.WaitFor("DetailCopyFailed").Name);
            Assert.Null(app.Find("DetailPasswordCopied"));
        }

        app.Invoke("DetailCopyPassword");
        app.WaitGone("DetailCopyFailed");
        app.WaitFor("DetailPasswordCopied");
        WaitClipboard(Holds(Password), Timeout, "The clipboard does not hold the password after the retry.");
    }

    // A fictitious marker no real copy could ever match.
    private static string Marker() => $"pm-e2e-{Guid.NewGuid():N}";

    private static Func<string?, bool> Holds(string expected) =>
        text => string.Equals(text, expected, StringComparison.Ordinal);

    // Empties the clipboard first, so a copy left by an earlier run cannot pass for the app's.
    private static void StartEmpty()
    {
        SystemClipboard.Clear();
        Assert.True(SystemClipboard.ReadText() is null, "The clipboard still holds text after it was emptied.");
    }

    private static void AssertPrivacyMarkers()
    {
        Assert.True(
            SystemClipboard.ReadDword("CanIncludeInClipboardHistory") == 0,
            "CanIncludeInClipboardHistory is not on the clipboard as a DWORD 0.");
        Assert.True(
            SystemClipboard.ReadDword("CanUploadToCloudClipboard") == 0,
            "CanUploadToCloudClipboard is not on the clipboard as a DWORD 0.");
        Assert.True(
            SystemClipboard.HasFormat("ExcludeClipboardContentFromMonitorProcessing"),
            "ExcludeClipboardContentFromMonitorProcessing is not on the clipboard.");
    }

    private static void WaitClipboard(Func<string?, bool> condition, TimeSpan timeout, string failure)
    {
        var reached = Retry.WhileFalse(() => condition(SystemClipboard.ReadText()), timeout, Poll).Success;
        Assert.True(reached, failure);
    }

    private static void WaitHistory(string text, string failure)
    {
        var reached = Retry.WhileFalse(() => SystemClipboard.HistoryContains(text), Timeout, Poll).Success;
        Assert.True(reached, failure);
    }

    private static void Unlock(AppSession app)
    {
        Assert.Single(app.Items("ProfilesList")).Patterns.Invoke.Pattern.Invoke();
        app.TypePassword(SeededVault.Password);
        app.Invoke("UnlockButton");
        app.WaitFor("EntriesList");
        app.WaitGone("UnlockPassword");
    }

    // Seeded entry #1, opened from the list by its title.
    private static void OpenFirstEntry(AppSession app)
    {
        Unlock(app);
        var item = Retry.WhileNull(
            () => app.Items("EntriesList").FirstOrDefault(i => i.Name == Title), Timeout, Poll, ignoreException: true).Result
            ?? throw new InvalidOperationException("Entry #1 is not in the list.");
        item.Patterns.Invoke.Pattern.Invoke();
        app.WaitFor("DetailTitle");
        app.WaitGone("EntriesList");
    }
}
