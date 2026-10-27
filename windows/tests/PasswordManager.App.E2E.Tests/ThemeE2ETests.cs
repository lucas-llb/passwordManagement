using System.Globalization;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using PasswordManager.App.E2E.Tests.Accessibility;

namespace PasswordManager.App.E2E.Tests;

// The app follows the system app mode live, title bar included, and its text keeps AA contrast (4.5:1) in both
// themes. The test switches the user's real AppsUseLightTheme for a few seconds and always restores it (SystemTheme).
// It stays on Profiles and Unlock: no vault is open, so nothing secret is on screen when the pixels are captured.
public sealed class ThemeE2ETests
{
    private const double MaxDarkBackground = 0.1;
    private const double MinLightBackground = 0.4;
    private const double MinContrast = 4.5;

    private static readonly TimeSpan RepaintTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void SystemThemeSwitch_AppFollowsLiveWithAaTextContrast()
    {
        // Launch first: in Release it skips before the registry is read or written.
        using var app = AppSession.Launch(SeededVault.Seed);
        SystemTheme.RestoreInterrupted();
        var dark = SystemTheme.AppsUseDarkTheme;

        Assert.Single(app.Items("ProfilesList"));
        AssertRenderedTheme(app, dark);

        using (SystemTheme.Override(!dark))
        {
            AssertRenderedTheme(app, !dark);

            Assert.Single(app.Items("ProfilesList")).Patterns.Invoke.Pattern.Invoke();
            app.WaitFor("UnlockPassword");
            AssertRenderedTheme(app, !dark);
        }

        AssertRenderedTheme(app, dark);
        Assert.Equal(dark, SystemTheme.AppsUseDarkTheme);
        Assert.False(File.Exists(SystemTheme.MarkerPath), "The theme restore marker was left behind.");
    }

    // Every named XAML text on screen sits on a background of the expected theme with AA contrast, and so does the
    // title bar. Retried, because the repaint after a switch is not instant; the theme change is animated, so an attempt
    // only counts once its backgrounds are the same as the previous attempt's (a settled frame, not a mid-fade one).
    private static void AssertRenderedTheme(AppSession app, bool dark)
    {
        var failures = new List<string>();
        var backgrounds = new List<string>();
        var previous = string.Empty;
        var passed = Retry.WhileFalse(
            () =>
            {
                failures.Clear();
                backgrounds.Clear();
                app.EnsureForeground();
                var texts = app.Window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text).And(cf.ByFrameworkId("XAML")))
                    .Where(t => !string.IsNullOrEmpty(t.Properties.Name.ValueOrDefault) && !t.BoundingRectangle.IsEmpty)
                    .ToArray();
                if (texts.Length == 0)
                {
                    failures.Add("no named XAML text on screen");
                }

                foreach (var text in texts)
                {
                    backgrounds.Add(Check(text, dark, requireContrast: true, failures));
                }

                var titleBar = app.Window.FindFirstChild(cf => cf.ByControlType(ControlType.TitleBar));
                if (titleBar is null)
                {
                    failures.Add("no TitleBar element");
                }
                else
                {
                    backgrounds.Add(Check(titleBar, dark, requireContrast: false, failures));
                }

                var current = string.Join(' ', backgrounds);
                var settled = current == previous;
                previous = current;
                return failures.Count == 0 && settled;
            },
            RepaintTimeout,
            TimeSpan.FromMilliseconds(250)).Success;

        var details = failures.Count == 0 ? $"the rendering never settled (last backgrounds: {previous})" : string.Join(Environment.NewLine, failures);
        Assert.True(passed, $"Expected the {(dark ? "dark" : "light")} theme with AA text:{Environment.NewLine}{details}");
    }

    // Returns the measured background, for the settled-frame check.
    private static string Check(AutomationElement element, bool dark, bool requireContrast, List<string> failures)
    {
        var (background, luminance, contrast) = ScreenPixels.Measure(element);
        var themed = dark ? luminance <= MaxDarkBackground : luminance >= MinLightBackground;
        if (!themed || (requireContrast && contrast < MinContrast))
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{element.Properties.AutomationId.ValueOrDefault} '{element.Properties.Name.ValueOrDefault}' bg={ScreenPixels.Hex(background)} L={luminance:F3} ratio={contrast:F2}"));
        }

        return ScreenPixels.Hex(background);
    }
}
