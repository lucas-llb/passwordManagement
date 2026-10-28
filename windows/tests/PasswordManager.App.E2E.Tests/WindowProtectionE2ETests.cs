using System.Drawing;
using System.Globalization;
using FlaUI.Core.Capturing;
using FlaUI.Core.Tools;
using PasswordManager.App.E2E.Tests.Accessibility;

namespace PasswordManager.App.E2E.Tests;

// While the vault is open (list and detail) the window is excluded from screen capture (W-14), so a capture of its area
// shows what is behind it. The tests read the real display affinity from outside the app and compare a capture of the
// window area with one taken after moving the window off-screen. They send no key and no click.
public sealed class WindowProtectionE2ETests
{
    // A capturable window changes far more of its own area than this when it leaves; the 1% ceiling of an excluded window
    // absorbs small changes behind it, such as a terminal progress line.
    private const double MinVisibleFraction = 0.05;
    private const double MaxExcludedFraction = 0.01;

    // Borders, shadow and rounded corners are left out of the compared area.
    private const int Inset = 32;

    // Two captures in a row that differ by no more than this are a settled frame.
    private const double SettledFraction = 0.001;

    // Attempts spoiled by a change behind the window are measured again, up to this many in all.
    private const int MeasureAttempts = 3;

    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SettleInterval = TimeSpan.FromMilliseconds(250);

    [Fact]
    public void VaultOpen_WindowIsExcludedFromCaptureOnlyWhileUnlocked()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Assert.Single(app.Items("ProfilesList"));
        app.WaitDisplayAffinity(NativeMethods.WDA_NONE);

        Assert.Single(app.Items("ProfilesList")).Patterns.Invoke.Pattern.Invoke();
        app.WaitFor("UnlockPassword");
        app.WaitDisplayAffinity(NativeMethods.WDA_NONE);

        app.TypePassword("senha-errada-ficticia");
        app.Invoke("UnlockButton");
        app.WaitFor("UnlockWrongPassword");
        Assert.Equal(NativeMethods.WDA_NONE, app.DisplayAffinity());

        // The wrong password starts a one-second delay before the next attempt.
        app.WaitEnabled("UnlockButton");
        app.TypePassword(SeededVault.Password);
        app.Invoke("UnlockButton");
        app.WaitFor("EntriesList");

        // A single read, no retry: the window was excluded before the list was rendered.
        Assert.Equal(NativeMethods.WDA_EXCLUDEFROMCAPTURE, app.DisplayAffinity());

        app.Items("EntriesList")[0].Patterns.Invoke.Pattern.Invoke();
        app.WaitFor("DetailTitle");
        Assert.Equal(NativeMethods.WDA_EXCLUDEFROMCAPTURE, app.DisplayAffinity());

        app.Invoke("DetailBack");
        app.WaitFor("EntriesList");
        Assert.Equal(NativeMethods.WDA_EXCLUDEFROMCAPTURE, app.DisplayAffinity());

        app.Invoke("EntriesLock");
        app.WaitFor("UnlockPassword");
        app.WaitGone("EntriesList");
        app.WaitDisplayAffinity(NativeMethods.WDA_NONE);

        app.Invoke("UnlockBack");
        app.WaitFor("ProfilesList");
        app.WaitDisplayAffinity(NativeMethods.WDA_NONE);
    }

    [Fact]
    public void VaultOpen_ScreenCaptureSeesOnlyWhatIsBehindTheWindow()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Assert.Single(app.Items("ProfilesList")).Patterns.Invoke.Pattern.Invoke();
        app.WaitFor("UnlockPassword");
        app.WaitDisplayAffinity(NativeMethods.WDA_NONE);

        // The control: a capturable window is seen by the capture, so the check below has power.
        var unlocked = FractionOfWindowThatCaptureSees(app);
        Assert.True(unlocked >= MinVisibleFraction, Message("Unlock (capturable)", unlocked, ">=", MinVisibleFraction));

        app.TypePassword(SeededVault.Password);
        app.Invoke("UnlockButton");
        app.WaitFor("EntriesList");
        app.WaitDisplayAffinity(NativeMethods.WDA_EXCLUDEFROMCAPTURE);

        var list = FractionOfWindowThatCaptureSees(app);
        Assert.True(list <= MaxExcludedFraction, Message("entry list (excluded)", list, "<=", MaxExcludedFraction));

        app.Invoke("EntriesLock");
        app.WaitFor("UnlockPassword");
        app.WaitDisplayAffinity(NativeMethods.WDA_NONE);

        var locked = FractionOfWindowThatCaptureSees(app);
        Assert.True(locked >= MinVisibleFraction, Message("Unlock after lock (capturable)", locked, ">=", MinVisibleFraction));
    }

    // How much of the window area changes in a screen capture when the window leaves the screen: a capturable window
    // changes most of it, an excluded one nothing, because the capture already showed what is behind it. An attempt
    // only counts when the background held still: the in-place capture is taken again after the window comes back, and
    // the two must agree. A visible window agrees with itself, so a retry can never hide it.
    private static double FractionOfWindowThatCaptureSees(AppSession app)
    {
        var attempts = new List<string>();
        for (var attempt = 0; attempt < MeasureAttempts; attempt++)
        {
            var measured = MeasureOnce(app);
            if (measured.Fraction is { } fraction)
            {
                return fraction;
            }

            attempts.Add(measured.Reason);
        }

        Assert.Fail($"The screen behind the app window kept changing; the capture check needs a static background:{Environment.NewLine}{string.Join(Environment.NewLine, attempts)}");
        return double.NaN;
    }

    private static (double? Fraction, string Reason) MeasureOnce(AppSession app)
    {
        app.EnsureForeground();
        Assert.True(NativeMethods.GetWindowRect(app.Handle, out var rect), "GetWindowRect failed.");
        var area = Rectangle.FromLTRB(rect.Left + Inset, rect.Top + Inset, rect.Right - Inset, rect.Bottom - Inset);
        Assert.True(area.Width > 0 && area.Height > 0, $"The window is too small to compare: {area}.");

        using var inPlace = SettledCapture(area);
        if (inPlace is null)
        {
            return (null, "the window area never settled before the move");
        }

        Bitmap? away;
        try
        {
            var offScreen = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN)
                + NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
            Assert.True(
                NativeMethods.SetWindowPos(
                    app.Handle, nint.Zero, offScreen, rect.Top, 0, 0,
                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE),
                "SetWindowPos (off-screen) failed.");
            var moved = Retry.WhileFalse(
                () => NativeMethods.GetWindowRect(app.Handle, out var now) && now.Left >= offScreen,
                SettleTimeout,
                TimeSpan.FromMilliseconds(50)).Success;
            Assert.True(moved, "The window did not move off-screen.");

            away = SettledCapture(area);
        }
        finally
        {
            // The explicit size restores the window even if the move changed its DPI.
            NativeMethods.SetWindowPos(
                app.Handle, nint.Zero, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            app.EnsureForeground();
        }

        using (away)
        {
            if (away is null)
            {
                return (null, "the area never settled with the window off-screen");
            }

            using var back = SettledCapture(area);
            if (back is null)
            {
                return (null, "the window area never settled after the window came back");
            }

            var drift = ScreenPixels.ChangedFraction(inPlace, back);
            if (drift > SettledFraction)
            {
                return (null, string.Create(CultureInfo.InvariantCulture, $"the window area changed by {drift:P2} between the captures before and after the move"));
            }

            return (ScreenPixels.ChangedFraction(inPlace, away), string.Empty);
        }
    }

    // A capture of the area once two in a row agree (the screen has finished repainting after the last change), or null
    // if it never does. The first capture waits one interval too, so it never comes before the compositor caught up.
    private static Bitmap? SettledCapture(Rectangle area)
    {
        Bitmap? previous = null;
        var deadline = DateTime.UtcNow + SettleTimeout;
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(SettleInterval);
                Bitmap current;
                using (var image = Capture.Rectangle(area))
                {
                    current = new Bitmap(image.Bitmap);
                }

                if (previous is not null && ScreenPixels.ChangedFraction(previous, current) <= SettledFraction)
                {
                    return current;
                }

                previous?.Dispose();
                previous = current;
            }

            return null;
        }
        finally
        {
            previous?.Dispose();
        }
    }

    private static string Message(string step, double fraction, string comparison, double limit) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"On {step}, the capture saw {fraction:P2} of the window area change when the window left; expected {comparison} {limit:P0}.");
}
