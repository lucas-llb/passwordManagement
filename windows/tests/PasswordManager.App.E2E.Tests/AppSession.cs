using System.Diagnostics;
using System.Reflection;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace PasswordManager.App.E2E.Tests;

internal sealed class AppSession : IDisposable
{
#if DEBUG
    private const bool IsDebugBuild = true;
#else
    private const bool IsDebugBuild = false;
#endif

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly Application? _app;
    private readonly UIA3Automation? _automation;

    private AppSession(string vaultsDirectory, Application app, UIA3Automation automation, Window window)
    {
        VaultsDirectory = vaultsDirectory;
        _app = app;
        _automation = automation;
        Window = window;
    }

    public string VaultsDirectory { get; }

    public Window Window { get; }

    public static AppSession Launch(Action<string>? seed = null)
    {
        Assert.SkipUnless(IsDebugBuild, "E2E tests run only in Debug: the --vaults-dir hook exists only in the Debug app.");
        var exe = Metadata("AppExePath");
        Assert.True(File.Exists(exe), $"Build the Debug app first: {exe}");

        var directory = Directory.CreateTempSubdirectory("pm-e2e-").FullName;
        Application? app = null;
        UIA3Automation? automation = null;
        try
        {
            seed?.Invoke(directory);
            var start = new ProcessStartInfo(exe);
            start.ArgumentList.Add("--vaults-dir");
            start.ArgumentList.Add(directory);
            app = Application.Launch(start);
            automation = new UIA3Automation();
            var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(30))
                ?? throw new InvalidOperationException("The app window did not open.");
            return new AppSession(directory, app, automation, window);
        }
        catch
        {
            Cleanup(app, automation, directory);
            throw;
        }
    }

    public static string Metadata(string key) =>
        typeof(AppSession).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value
        ?? throw new InvalidOperationException(key);

    public AutomationElement? Find(string automationId) => Window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));

    public AutomationElement WaitFor(string automationId) =>
        Retry.WhileNull(() => Find(automationId), Timeout, TimeSpan.FromMilliseconds(100)).Result
        ?? throw new InvalidOperationException($"Element '{automationId}' did not appear.");

    public void WaitGone(string automationId)
    {
        var gone = Retry.WhileTrue(() => Find(automationId) is not null, Timeout, TimeSpan.FromMilliseconds(100)).Result;
        Assert.True(gone, $"Element '{automationId}' is still shown.");
    }

    public void Invoke(string automationId) => WaitFor(automationId).Patterns.Invoke.Pattern.Invoke();

    public void TypePassword(string password) => WaitFor("UnlockPassword").Patterns.Value.Pattern.SetValue(password);

    // Every keystroke goes through here: it brings the app window to the front and refuses to go on unless the
    // foreground window really is the app, so a key can never land in another program.
    public void EnsureForeground()
    {
        var handle = Window.Properties.NativeWindowHandle.Value;
        Assert.NotEqual(nint.Zero, handle);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (NativeMethods.GetForegroundWindow() != handle)
        {
            Assert.True(DateTime.UtcNow < deadline, "The app window is not in the foreground; no key was sent.");
            Window.SetForeground();
            NativeMethods.BringToForeground(handle);
            Thread.Sleep(100);
        }
    }

    public void PressKey(VirtualKeyShort key)
    {
        EnsureForeground();
        Keyboard.Type(key);
        Wait.UntilInputIsProcessed();
    }

    public void PressChord(params VirtualKeyShort[] keys)
    {
        EnsureForeground();
        Keyboard.TypeSimultaneously(keys);
        Wait.UntilInputIsProcessed();
    }

    public AutomationElement? Focused() => _automation!.FocusedElement();

    // A stable label for the focused element: its AutomationId; for a list item, which has none, the list's AutomationId
    // and the item's name, as in "EntriesList[title]"; for anything else, its control type and name.
    public static string FocusKey(AutomationElement? element)
    {
        if (element is null)
        {
            return "(none)";
        }

        var name = element.Properties.Name.ValueOrDefault ?? string.Empty;
        if (element.Properties.ControlType.ValueOrDefault == ControlType.ListItem)
        {
            return $"{element.Parent?.Properties.AutomationId.ValueOrDefault}[{name}]";
        }

        var id = element.Properties.AutomationId.ValueOrDefault;
        return string.IsNullOrEmpty(id) ? $"{element.Properties.ControlType.ValueOrDefault}:{name}" : id;
    }

    // UI Automation reports the focus of the whole desktop, so the app is brought to the front first (no key is sent).
    public void WaitFocus(string expected)
    {
        EnsureForeground();
        var actual = Retry.WhileFalse(
            () => FocusKey(Focused()) == expected, Timeout, TimeSpan.FromMilliseconds(100)).Success
            ? expected
            : FocusKey(Focused());
        Assert.Equal(expected, actual);
    }

    public AutomationElement[] Items(string listId) =>
        WaitFor(listId).FindAllChildren(cf => cf.ByControlType(ControlType.ListItem));

    public static string TextOf(AutomationElement item, string automationId) =>
        (item.FindFirstDescendant(cf => cf.ByAutomationId(automationId))
            ?? throw new InvalidOperationException(automationId)).Name;

    public string ValueOf(string automationId) => WaitFor(automationId).Patterns.Value.Pattern.Value.Value;

    public void Dispose() => Cleanup(_app, _automation, VaultsDirectory);

    private static void Cleanup(Application? app, UIA3Automation? automation, string directory)
    {
        if (app is not null)
        {
            app.Close();
            if (!app.HasExited)
            {
                app.Kill();
            }

            app.Dispose();
        }

        automation?.Dispose();
        for (var attempt = 0; Directory.Exists(directory); attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(250);
            }
        }
    }
}
