using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
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

    public nint Handle => Window.Properties.NativeWindowHandle.Value;

    // The extra arguments go after --vaults-dir, for the other Debug switches (such as --idle-timeout-seconds).
    public static AppSession Launch(Action<string>? seed = null, params string[] arguments)
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
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

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

    // Polls fast for an element that may be shown only for a moment (for example a ring while an unlock runs); true once
    // it has been seen.
    public bool WaitAppears(string automationId, TimeSpan timeout, TimeSpan poll) =>
        Retry.WhileNull(() => Find(automationId), timeout, poll).Result is not null;

    public void Invoke(string automationId) => WaitFor(automationId).Patterns.Invoke.Pattern.Invoke();

    // Flips a CheckBox (or any toggle) through its TogglePattern, as a click or Space would.
    public void Toggle(string automationId) => WaitFor(automationId).Patterns.Toggle.Pattern.Toggle();

    public bool IsEnabled(string automationId) => WaitFor(automationId).IsEnabled;

    // Waits until the element is enabled (or disabled); a command's CanExecute reaches the button a moment later.
    public void WaitEnabled(string automationId, bool expected = true)
    {
        var reached = Retry.WhileFalse(() => IsEnabled(automationId) == expected, Timeout, TimeSpan.FromMilliseconds(100)).Success;
        Assert.True(reached, $"Element '{automationId}' is {(expected ? "still disabled" : "still enabled")}.");
    }

    // Waits until the element shows this name; a text bound to the view model may lag a moment behind the input.
    public void WaitName(string automationId, string expected)
    {
        var actual = Retry.WhileFalse(() => WaitFor(automationId).Name == expected, Timeout, TimeSpan.FromMilliseconds(100)).Success
            ? expected
            : WaitFor(automationId).Name;
        Assert.Equal(expected, actual);
    }

    public void TypePassword(string password) => SetValue("UnlockPassword", password);

    // Sets the text of a TextBox or a PasswordBox through its ValuePattern (only fictitious passwords in tests).
    public void SetValue(string automationId, string value) => WaitFor(automationId).Patterns.Value.Pattern.SetValue(value);

    // A ContentDialog declared in a page always leaves an empty Group with its AutomationId in the tree; only while it is
    // shown is there also a Window element with that id, which holds the content and the buttons.
    public AutomationElement? FindDialog(string dialogId) =>
        Window.FindFirstDescendant(cf => cf.ByAutomationId(dialogId).And(cf.ByControlType(ControlType.Window)));

    public AutomationElement WaitDialog(string dialogId) =>
        Retry.WhileNull(() => FindDialog(dialogId), Timeout, TimeSpan.FromMilliseconds(100)).Result
        ?? throw new InvalidOperationException($"Dialog '{dialogId}' did not open.");

    public void WaitDialogGone(string dialogId)
    {
        var gone = Retry.WhileTrue(() => FindDialog(dialogId) is not null, Timeout, TimeSpan.FromMilliseconds(100)).Result;
        Assert.True(gone, $"Dialog '{dialogId}' is still open.");
    }

    // A ContentDialog's buttons are its template parts, whose AutomationIds are PrimaryButton and CloseButton; they are
    // looked up under the open dialog, never by their text.
    public void InvokeInDialog(string dialogId, string buttonId)
    {
        var dialog = WaitDialog(dialogId);
        var button = Retry.WhileNull(
            () => dialog.FindFirstDescendant(cf => cf.ByAutomationId(buttonId)), Timeout, TimeSpan.FromMilliseconds(100)).Result
            ?? throw new InvalidOperationException($"Button '{buttonId}' of dialog '{dialogId}' did not appear.");
        button.Patterns.Invoke.Pattern.Invoke();
    }

    // Every keystroke goes through here: it brings the app window to the front and refuses to go on unless the
    // foreground window really is the app, so a key can never land in another program.
    public void EnsureForeground()
    {
        var handle = Handle;
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

    // The app window's display affinity (W-14), read from outside the app process.
    public uint DisplayAffinity()
    {
        Assert.True(
            NativeMethods.GetWindowDisplayAffinity(Handle, out var affinity),
            $"GetWindowDisplayAffinity failed with error {Marshal.GetLastPInvokeError()}.");
        return affinity;
    }

    public void WaitDisplayAffinity(uint expected)
    {
        var actual = Retry.WhileFalse(() => DisplayAffinity() == expected, Timeout, TimeSpan.FromMilliseconds(100)).Success
            ? expected
            : DisplayAffinity();
        Assert.True(expected == actual, $"Expected display affinity 0x{expected:X}, got 0x{actual:X}.");
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

    // The items once the list holds exactly this many; a list refreshed after a change may still be building them.
    public AutomationElement[] WaitItems(string listId, int count)
    {
        Retry.WhileFalse(() => Items(listId).Length == count, Timeout, TimeSpan.FromMilliseconds(100));
        var items = Items(listId);
        Assert.True(items.Length == count, $"Expected {count} items in '{listId}', found {items.Length}.");
        return items;
    }

    public static string TextOf(AutomationElement item, string automationId) =>
        (item.FindFirstDescendant(cf => cf.ByAutomationId(automationId))
            ?? throw new InvalidOperationException(automationId)).Name;

    public string ValueOf(string automationId) => WaitFor(automationId).Patterns.Value.Pattern.Value.Value;

    // Posts the real WM_WTSSESSION_CHANGE to the app's top-level window, as the OS does for a session it registered
    // for; the desktop itself is never locked (the test could not unlock it again).
    public void PostSessionChange(nuint wtsEvent)
    {
        var posted = NativeMethods.PostMessageW(
            Handle, NativeMethods.WM_WTSSESSION_CHANGE, wtsEvent, Process.GetCurrentProcess().SessionId);
        Assert.True(posted, $"PostMessageW failed with error {Marshal.GetLastPInvokeError()}.");
    }

    public void Minimize()
    {
        NativeMethods.ShowWindow(Handle, NativeMethods.SW_MINIMIZE);
        var minimized = Retry.WhileFalse(() => NativeMethods.IsIconic(Handle), Timeout, TimeSpan.FromMilliseconds(50)).Success;
        Assert.True(minimized, "The app window did not minimize.");
    }

    public void Restore()
    {
        NativeMethods.ShowWindow(Handle, NativeMethods.SW_RESTORE);
        var restored = Retry.WhileTrue(() => NativeMethods.IsIconic(Handle), Timeout, TimeSpan.FromMilliseconds(50)).Success;
        Assert.True(restored, "The app window did not restore.");
    }

    // Moves the real cursor (no click) to a point inside the window's client area, offset from its centre, so the app
    // gets pointer input.
    public void MovePointerInside(int offset)
    {
        var rect = WindowRect();
        MovePointerTo(((rect.Left + rect.Right) / 2) + offset, ((rect.Top + rect.Bottom) / 2) + offset);
    }

    // Moves the real cursor (no click) off the app window, so a cursor resting on it can neither count as activity nor
    // be needed for it: an idle check then sees only what the test does.
    public void ParkPointerOutside()
    {
        const int margin = 40;
        var rect = WindowRect();
        var screen = VirtualScreen();
        (int X, int Y)[] candidates =
        [
            (rect.Left - margin, rect.Top + margin),
            (rect.Right + margin, rect.Top + margin),
            (rect.Left + margin, rect.Bottom + margin),
            (rect.Left + margin, rect.Top - margin),
            (screen.Left + 1, screen.Top + 1),
            (screen.Right - 2, screen.Bottom - 2),
        ];
        foreach (var (x, y) in candidates)
        {
            var onScreen = x >= screen.Left && x < screen.Right && y >= screen.Top && y < screen.Bottom;
            var offWindow = x < rect.Left || x >= rect.Right || y < rect.Top || y >= rect.Bottom;
            if (onScreen && offWindow)
            {
                MovePointerTo(x, y);
                return;
            }
        }

        Assert.Fail("There is no screen point outside the app window to park the cursor on.");
    }

    // Which seeded entry values (title, username, password, URL, folder or notes; the empty ones are skipped) some element
    // of the window holds, as its Name (a text) or its Value (a text box; a password box is never read), labelled by
    // field and entry number so a failure never prints a value. Empty once nothing from the vault is on screen or in the
    // automation tree. Line endings are compared as \n, since a text box keeps them as \r.
    public string[] VisibleEntryFields()
    {
        string[] fields = ["title", "username", "password", "url", "folder", "notes"];
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var entries = SeededVault.Entries;
        for (var index = 0; index < entries.Length; index++)
        {
            foreach (var field in fields)
            {
                var value = entries[index].GetProperty(field).GetString();
                if (!string.IsNullOrEmpty(value))
                {
                    labels.TryAdd(value.ReplaceLineEndings("\n"), $"{field} #{index + 1}");
                }
            }
        }

        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var element in Window.FindAllDescendants())
        {
            var name = element.Properties.Name.ValueOrDefault;
            var value = element.Properties.IsPassword.ValueOrDefault
                ? null
                : element.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault;
            foreach (var text in new[] { name, value })
            {
                if (!string.IsNullOrEmpty(text) && labels.TryGetValue(text.ReplaceLineEndings("\n"), out var label))
                {
                    found.Add(label);
                }
            }
        }

        return [.. found];
    }

    public void AssertNoEntryVisible()
    {
        var visible = VisibleEntryFields();
        Assert.True(visible.Length == 0, $"Entry values are still in the window: {string.Join(", ", visible)}.");
    }

    // An injected absolute move, as a real mouse sends: WinUI turns it into pointer events, which a SetCursorPos (what
    // FlaUI's Mouse uses) does not reliably produce. The coordinates are physical pixels on the virtual desktop.
    private static void MovePointerTo(int x, int y)
    {
        var screen = VirtualScreen();
        INPUT[] input =
        [
            new()
            {
                Type = NativeMethods.INPUT_MOUSE,
                Mouse = new MOUSEINPUT
                {
                    Dx = (int)((x - screen.Left) * 65535L / Math.Max(1, screen.Right - screen.Left - 1)),
                    Dy = (int)((y - screen.Top) * 65535L / Math.Max(1, screen.Bottom - screen.Top - 1)),
                    Flags = NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_VIRTUALDESK,
                },
            },
        ];
        var sent = NativeMethods.SendInput(1, input, Marshal.SizeOf<INPUT>());
        Assert.True(sent == 1, $"SendInput failed with error {Marshal.GetLastPInvokeError()}.");
        Wait.UntilInputIsProcessed();
    }

    private static RECT VirtualScreen()
    {
        var left = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var top = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        return new RECT
        {
            Left = left,
            Top = top,
            Right = left + NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
            Bottom = top + NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN),
        };
    }

    private RECT WindowRect()
    {
        Assert.True(NativeMethods.GetWindowRect(Handle, out var rect), $"GetWindowRect failed with error {Marshal.GetLastPInvokeError()}.");
        return rect;
    }

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
