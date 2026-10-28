using PasswordManager.App.E2E.Tests.Localization;

namespace PasswordManager.App.E2E.Tests;

// The unlock screen after wrong master passwords (a progressive delay with a countdown, kept per profile for the app's
// lifetime) and while Argon2id runs. Every password is fictitious; the countdown checks wait on the text, never sleep.
public sealed class UnlockE2ETests
{
    private const string WrongPassword = "senha-errada-ficticia";

    [Fact]
    public void WrongPasswords_DelayDoublesSurvivesBackAndThenUnlocks()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Assert.Single(app.Items("ProfilesList")).Patterns.Invoke.Pattern.Invoke();
        app.WaitFor("UnlockPassword");
        Assert.Null(app.Find("UnlockRetryCountdown"));

        // Wrong #1: one second, with the button disabled until it ends.
        app.TypePassword(WrongPassword);
        app.Invoke("UnlockButton");
        app.WaitFor("UnlockWrongPassword");
        app.WaitName("UnlockRetryCountdown", Countdown(1));
        Assert.False(app.IsEnabled("UnlockButton"));
        app.WaitEnabled("UnlockButton");
        app.WaitGone("UnlockRetryCountdown");

        // Wrong #2: two seconds, counted down second by second.
        app.TypePassword(WrongPassword);
        app.Invoke("UnlockButton");
        app.WaitName("UnlockRetryCountdown", Countdown(2));
        Assert.False(app.IsEnabled("UnlockButton"));
        app.WaitName("UnlockRetryCountdown", Countdown(1));
        app.WaitEnabled("UnlockButton");
        app.WaitGone("UnlockRetryCountdown");

        // Wrong #3: four seconds, which Back and reopening the profile do not cut short.
        app.TypePassword(WrongPassword);
        app.Invoke("UnlockButton");
        app.WaitName("UnlockRetryCountdown", Countdown(4));
        app.Invoke("UnlockBack");
        app.WaitFor("ProfilesList");
        app.WaitGone("UnlockPassword");
        Assert.Single(app.Items("ProfilesList")).Patterns.Invoke.Pattern.Invoke();
        app.WaitFor("UnlockPassword");
        var countdown = app.WaitFor("UnlockRetryCountdown").Name;
        Assert.Contains(countdown, new[] { Countdown(4), Countdown(3), Countdown(2), Countdown(1) });
        Assert.False(app.IsEnabled("UnlockButton"));

        // Once it ends, the right password opens the vault.
        app.WaitEnabled("UnlockButton");
        app.WaitGone("UnlockRetryCountdown");
        app.TypePassword(SeededVault.Password);
        app.Invoke("UnlockButton");
        app.WaitFor("EntriesList");
        app.WaitGone("UnlockPassword");
    }

    [Fact]
    public void Unlock_RealCostVault_ShowsProgressOnlyWhileArgon2idRuns()
    {
        const string password = "frase-ficticia-do-anel";
        using var app = AppSession.Launch();

        // A profile created through the UI has the default (real) Argon2id cost.
        app.Invoke("ProfilesCreate");
        app.WaitFor("CreateName");
        app.SetValue("CreateName", "Perfil Anel");
        app.SetValue("CreatePassword", password);
        app.SetValue("CreateConfirmPassword", password);
        app.Invoke("CreateButton");
        app.WaitFor("KitText");
        app.Toggle("KitSavedCheck");
        app.WaitEnabled("KitDone");
        app.Invoke("KitDone");
        Assert.Single(app.WaitItems("ProfilesList", 1)).Patterns.Invoke.Pattern.Invoke();

        app.WaitFor("UnlockPassword");
        Assert.Null(app.Find("UnlockProgress"));

        // The ring is seen while Argon2id runs off the UI thread, so the UI thread kept rendering.
        app.TypePassword(password);
        app.Invoke("UnlockButton");
        Assert.True(
            app.WaitAppears("UnlockProgress", TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(10)),
            "The progress ring was never shown while the unlock ran.");

        app.WaitFor("EntriesEmpty");
        app.WaitGone("UnlockPassword");
        Assert.Null(app.Find("UnlockProgress"));
    }

    private static string Countdown(int seconds) => AppResources.Format("UnlockRetryCountdown", seconds);
}
