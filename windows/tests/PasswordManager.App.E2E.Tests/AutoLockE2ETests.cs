using System.Diagnostics;
using System.Globalization;
using FlaUI.Core.WindowsAPI;

namespace PasswordManager.App.E2E.Tests;

// The open vault locks by itself (W-18): after a short idle timeout passed with the Debug switch, on a Windows session
// lock and, only with --lock-on-minimize, on minimize. The session lock is the real WM_WTSSESSION_CHANGE posted to the
// real window, so the desktop is never locked. The idle tests move the real cursor (never a click) and park it off the
// window, so a cursor left on the window can neither keep the vault open nor be needed to. Every password is
// fictitious; the waits are on the UI, except the fixed settle of the checks that something did not happen.
public sealed class AutoLockE2ETests
{
    private const int IdleTimeoutSeconds = 3;

    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan InputInterval = TimeSpan.FromSeconds(1);

    private static readonly string[] ShortIdleTimeout =
        ["--idle-timeout-seconds", IdleTimeoutSeconds.ToString(CultureInfo.InvariantCulture)];

    [Fact]
    public void Idle_LocksFromTheDetailPageWithNoEntryLeft()
    {
        using var app = AppSession.Launch(SeededVault.Seed, ShortIdleTimeout);
        app.ParkPointerOutside();
        Unlock(app);
        app.Items("EntriesList")[0].Patterns.Invoke.Pattern.Invoke();
        app.WaitFor("DetailTitle");

        // No input from here on: the idle check locks the vault.
        app.WaitFor("UnlockPassword");
        Assert.Null(app.Find("EntriesList"));
        Assert.Null(app.Find("DetailTitle"));
        app.AssertNoEntryVisible();
        app.WaitDisplayAffinity(NativeMethods.WDA_NONE);

        // The stack is [Profiles, Unlock]: nothing from the vault is left behind Back.
        app.Invoke("UnlockBack");
        app.WaitFor("ProfilesList");
        app.WaitGone("UnlockPassword");

        Unlock(app);
    }

    [Fact]
    public void Activity_KeyboardAndPointer_KeepTheVaultOpenPastTheTimeout()
    {
        using var app = AppSession.Launch(SeededVault.Seed, ShortIdleTimeout);
        app.ParkPointerOutside();
        Unlock(app);
        var open = Stopwatch.StartNew();
        app.EnsureForeground();

        // Keys only, for one timeout and more: the vault stays open.
        var keys = new[] { VirtualKeyShort.DOWN, VirtualKeyShort.UP };
        for (var step = 0; open.Elapsed < TimeSpan.FromSeconds(IdleTimeoutSeconds + 1); step++)
        {
            app.PressKey(keys[step % keys.Length]);
            Thread.Sleep(InputInterval);
            Assert.NotNull(app.Find("EntriesList"));
        }

        // Then pointer moves only (no click), for one timeout and more after the last key.
        var pointer = Stopwatch.StartNew();
        for (var step = 0; pointer.Elapsed < TimeSpan.FromSeconds(IdleTimeoutSeconds + 1); step++)
        {
            app.MovePointerInside(step % 2 == 0 ? 20 : -20);
            Thread.Sleep(InputInterval);
            Assert.NotNull(app.Find("EntriesList"));
        }

        Assert.True(
            open.Elapsed >= TimeSpan.FromSeconds(2 * IdleTimeoutSeconds),
            $"The vault was kept open for only {open.Elapsed}; the check needs twice the timeout.");
        Assert.NotNull(app.Find("EntriesList"));
        Assert.Null(app.Find("UnlockPassword"));

        // Once the input stops, the vault locks.
        app.ParkPointerOutside();
        app.WaitFor("UnlockPassword");
        app.WaitGone("EntriesList");
        app.AssertNoEntryVisible();
    }

    [Fact]
    public void SessionLock_LocksOnlyAnOpenVault()
    {
        using var app = AppSession.Launch(SeededVault.Seed);

        // With no vault open the session lock does nothing.
        app.WaitFor("ProfilesList");
        app.PostSessionChange(NativeMethods.WTS_SESSION_LOCK);
        Thread.Sleep(Settle);
        Assert.NotNull(app.Find("ProfilesList"));
        Assert.Null(app.Find("UnlockPassword"));

        Unlock(app);
        app.Items("EntriesList")[0].Patterns.Invoke.Pattern.Invoke();
        app.WaitFor("DetailTitle");

        // The control: the entry is in the window before the lock, so the check after it has power.
        Assert.NotEmpty(app.VisibleEntryFields());

        app.PostSessionChange(NativeMethods.WTS_SESSION_LOCK);
        app.WaitFor("UnlockPassword");
        app.WaitGone("DetailTitle");
        Assert.Null(app.Find("EntriesList"));
        app.AssertNoEntryVisible();

        // Unlocking the session does not reopen anything.
        app.PostSessionChange(NativeMethods.WTS_SESSION_UNLOCK);
        Thread.Sleep(Settle);
        Assert.NotNull(app.Find("UnlockPassword"));
        Assert.Null(app.Find("EntriesList"));

        // No lock is left pending: the vault opened again stays open.
        Unlock(app);
        Thread.Sleep(Settle);
        Assert.NotNull(app.Find("EntriesList"));
        Assert.Null(app.Find("UnlockPassword"));
    }

    [Fact]
    public void Minimize_ByDefault_KeepsTheVaultOpen()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app);

        app.Minimize();
        Thread.Sleep(Settle);
        app.Restore();

        app.WaitFor("EntriesList");
        Assert.Null(app.Find("UnlockPassword"));
        Assert.Equal(SeededVault.Entries.Length, app.Items("EntriesList").Length);
    }

    [Fact]
    public void Minimize_WithLockOnMinimize_LocksTheVault()
    {
        using var app = AppSession.Launch(SeededVault.Seed, "--lock-on-minimize");
        Unlock(app);

        app.Minimize();
        app.Restore();

        app.WaitFor("UnlockPassword");
        app.WaitGone("EntriesList");
        app.AssertNoEntryVisible();
        app.WaitDisplayAffinity(NativeMethods.WDA_NONE);
    }

    // Opens the seeded vault from the profile list (or the Unlock page it leads to) and waits for the entry list.
    private static void Unlock(AppSession app)
    {
        if (app.Find("UnlockPassword") is null)
        {
            Assert.Single(app.Items("ProfilesList")).Patterns.Invoke.Pattern.Invoke();
        }

        app.TypePassword(SeededVault.Password);
        app.Invoke("UnlockButton");
        app.WaitFor("EntriesList");
        app.WaitGone("UnlockPassword");
    }
}
