using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using PasswordManager.App.E2E.Tests.Localization;

namespace PasswordManager.App.E2E.Tests.Packaging;

// W-29: the published Release app is the only build trimming can break at runtime (XAML reflection, PRI resources, the
// source-generated JSON). These tests start that exe and only read what it shows: the profile list title, then the
// Create page and back. A published exe cannot be pointed at a temp vaults folder (its switches are Debug-only), so it
// opens the real %LOCALAPPDATA%\PasswordManagement\vaults read-only: no password is typed and no profile is created,
// renamed or saved. Without a publish both tests skip, so dotnet test --solution stays green.
public sealed class PublishedAppSmokeTests
{
    private const string ExeName = "PasswordManager.App.exe";

    // Set it to smoke another publish (the ARM64 build on an ARM64 machine, or an untrimmed baseline) instead.
    private const string PublishedAppVariable = "PM_PUBLISHED_APP";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // ---- The tests ----

    [Fact]
    public void PublishedApp_StartsAndShowsProfiles()
    {
        WithPublishedApp(window =>
        {
            // The title text comes from the resw through the PRI file: an empty or wrong name is a trimming/resource
            // regression, not a UI change (the resw is the one the other guards read).
            var title = WaitFor(window, "ProfilesTitle");
            Assert.Equal(AppResources.ExpectedName("ProfilesTitle"), title.Name);
        });
    }

    [Fact]
    public void PublishedApp_CreatePageOpensAndReturns()
    {
        WithPublishedApp(window =>
        {
            WaitFor(window, "ProfilesTitle");

            // Create only navigates: nothing is typed and CreateButton is never invoked, so the real vaults folder keeps
            // its profiles. A disabled button would make that unreachable, and the test says so instead of clicking.
            var create = WaitFor(window, "ProfilesCreate");
            if (!create.IsEnabled)
            {
                Assert.Skip("The Create button is disabled, so the Create page cannot be reached without changing state.");
            }

            create.Patterns.Invoke.Pattern.Invoke();
            Assert.Equal(AppResources.ExpectedName("CreateTitle"), WaitFor(window, "CreateTitle").Name);

            WaitFor(window, "CreateBack").Patterns.Invoke.Pattern.Invoke();
            WaitFor(window, "ProfilesTitle");
        });
    }

    // ---- The published app ----

    // PM_PUBLISHED_APP wins; otherwise the x64 publish of the artifact contract, whose TargetFramework the test project
    // already bakes into the Debug apphost path.
    private static string PublishedAppPath()
    {
        var configured = Environment.GetEnvironmentVariable(PublishedAppVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        return Path.Combine(AppResources.AppDir, "bin", "Release", TargetFramework, "win-x64", "publish", ExeName);
    }

    private static string TargetFramework
    {
        get
        {
            var segments = AppSession.Metadata("AppExePath")
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var debug = Array.FindIndex(segments, segment => segment.Equals("Debug", StringComparison.OrdinalIgnoreCase));
            Assert.True(debug >= 0 && debug + 1 < segments.Length, "AppExePath has no bin/Debug/<tfm>/ segment.");
            return segments[debug + 1];
        }
    }

    private static string RequirePublishedApp()
    {
        var exe = PublishedAppPath();
        Assert.SkipUnless(
            File.Exists(exe),
            $"No published Release app at {exe}. Build it first: pwsh -NoProfile -File tools/Publish-Release.ps1 " +
            $"-RuntimeIdentifier win-x64, or set {PublishedAppVariable} to a published {ExeName}.");
        return exe;
    }

    private static void WithPublishedApp(Action<Window> assert)
    {
        Application? app = null;
        UIA3Automation? automation = null;
        try
        {
            var start = new ProcessStartInfo(RequirePublishedApp())
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(PublishedAppPath())!,
            };
            app = Application.Launch(start);
            automation = new UIA3Automation();
            assert(WaitForMainWindow(app, automation));
        }
        finally
        {
            Close(app, automation);
        }
    }

    private static Window WaitForMainWindow(Application app, UIA3Automation automation)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            // A published build that cannot load its XAML or resources dies before the window: the exit code is the signal.
            if (app.HasExited)
            {
                Assert.Fail(
                    $"The published app exited with code {app.ExitCode} before showing a window. Run " +
                    "tools/Publish-Release.ps1 with -NoReadyToRun to compare, and record the ReadyToRun result.");
            }

            var window = app.GetMainWindow(automation, TimeSpan.FromMilliseconds(250));
            if (window is not null)
            {
                return window;
            }
        }

        Assert.Fail($"The published app did not show a window within {Timeout.TotalSeconds:N0} s: {PublishedAppPath()}");
        return null!;
    }

    private static AutomationElement WaitFor(Window window, string automationId)
    {
        var element = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId(automationId)),
            Timeout,
            TimeSpan.FromMilliseconds(100)).Result;
        Assert.True(element is not null, $"Element '{automationId}' did not appear in the published app window.");
        return element!;
    }

    // Best effort: the app is asked to close first, then killed, so a failed test never leaves the real app on the desk.
    private static void Close(Application? app, UIA3Automation? automation)
    {
        try
        {
            if (app is not null)
            {
                app.Close();
                if (!app.HasExited)
                {
                    app.Kill();
                }
            }
        }
        finally
        {
            app?.Dispose();
            automation?.Dispose();
        }
    }
}
