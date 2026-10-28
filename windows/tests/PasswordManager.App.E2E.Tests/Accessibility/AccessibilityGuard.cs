using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace PasswordManager.App.E2E.Tests.Accessibility;

// Static checks behind the W-13 invariants: every interactive control has an AutomationId and an accessible name, nothing takes a control out of
// the keyboard order or hides its focus visual, and no XAML or C# overrides the system theme (so the WinUI AA theme ramps always apply).
// Pure functions over source text, no window and no Roslyn. Each Find method returns one entry per violation ("line N: ..."); an empty list means the source complies.
// The interactive and passive lists are closed on purpose: a new element type fails FindUnclassifiedElements until someone decides which kind it is.
internal static partial class AccessibilityGuard
{
    private const string AutomationIdAttribute = "AutomationProperties.AutomationId";
    private const string AutomationNameAttribute = "AutomationProperties.Name";
    private const string LabeledByAttribute = "AutomationProperties.LabeledBy";

    // The key format of an attached property in a resw: "<uid>.[using:<namespace>]<Owner>.<Property>".
    private const string AutomationNameSuffix = ".[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name";

    private const string ThemeResourcePrefix = "{ThemeResource ";

    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly char[] ThicknessSeparators = [',', ' '];

    // Controls named by their Content: the Button family, CheckBox and RadioButton.
    private static readonly HashSet<string> ContentNamed = new(StringComparer.Ordinal)
    {
        "Button", "HyperlinkButton", "RepeatButton", "ToggleButton", "AppBarButton", "AppBarToggleButton",
        "DropDownButton", "SplitButton", "ToggleSplitButton", "CheckBox", "RadioButton",
    };

    // Controls named by their Header: the input controls and the pickers that have a Header (ColorPicker has none).
    private static readonly HashSet<string> HeaderNamed = new(StringComparer.Ordinal)
    {
        "TextBox", "PasswordBox", "RichEditBox", "AutoSuggestBox", "NumberBox", "ComboBox", "ToggleSwitch", "Slider",
        "CalendarDatePicker", "DatePicker", "TimePicker",
    };

    // Everything the user can focus or activate. Besides the two sets above, these are named only through an AutomationProperties.Name
    // resw key, a bound AutomationProperties.Name or AutomationProperties.LabeledBy.
    private static readonly HashSet<string> Interactive = new(ContentNamed.Concat(HeaderNamed), StringComparer.Ordinal)
    {
        "RatingControl", "ListView", "GridView", "ListBox", "FlipView", "TreeView", "NavigationView", "TabView", "Pivot",
        "ColorPicker", "MenuFlyoutItem", "ToggleMenuFlyoutItem", "Hyperlink",
    };

    // Layout, text, resources and plumbing: nothing the user can focus or activate.
    // ContentDialog is a container: its buttons are template parts named by PrimaryButtonText / CloseButtonText from the resw (checked by the
    // W-12 localization guard), its content is checked like any other markup, and the E2E keyboard test checks the open dialog.
    private static readonly HashSet<string> Passive = new(StringComparer.Ordinal)
    {
        "Application", "ResourceDictionary", "XamlControlsResources", "Window", "MicaBackdrop", "Frame", "Page", "Grid",
        "RowDefinition", "ColumnDefinition", "StackPanel", "ScrollViewer", "Border", "TextBlock", "Run", "FontIcon",
        "SymbolIcon", "DataTemplate", "ProgressRing", "KeyboardAccelerator", "Style", "Setter", "ContentDialog",
    };

    // Elements that put a color of their own in the markup.
    private static readonly HashSet<string> ColorElements = new(StringComparer.Ordinal)
    {
        "SolidColorBrush", "LinearGradientBrush", "RadialGradientBrush", "AcrylicBrush", "Color",
    };

    // Reports every element that is in neither list, so a new control type forces a decision.
    public static IReadOnlyList<string> FindUnclassifiedElements(string xaml)
    {
        var violations = new List<(int Line, string Message)>();
        foreach (var element in ClassifiableElements(Parse(xaml)))
        {
            var type = element.Name.LocalName;
            if (!Interactive.Contains(type) && !Passive.Contains(type))
            {
                violations.Add((LineOf(element), $"<{type}> is neither interactive nor passive; classify it in AccessibilityGuard (an interactive control needs an AutomationId and an accessible name)"));
            }
        }

        return Format(violations);
    }

    // Every interactive element needs a non-empty AutomationId and one source of an accessible name:
    // a bound AutomationProperties.Name, AutomationProperties.LabeledBy, an x:Uid whose resw has the AutomationProperties.Name key and,
    // for the Content-named and Header-named types, an x:Uid whose resw has the .Content / .Header key, or a bound Content / Header.
    public static IReadOnlyList<string> FindUnnamedControls(string xaml, IReadOnlyDictionary<string, string> strings)
    {
        var violations = new List<(int Line, string Message)>();
        foreach (var element in Parse(xaml).Descendants().Where(element => Interactive.Contains(element.Name.LocalName)))
        {
            var type = element.Name.LocalName;
            var line = LineOf(element);

            if (string.IsNullOrWhiteSpace((string?)element.Attribute(AutomationIdAttribute)))
            {
                violations.Add((line, $"<{type}> has no {AutomationIdAttribute}"));
            }

            if (!HasNameSource(element, strings, out var textProperty))
            {
                var alternative = textProperty is null ? string.Empty : $" (or a '.{textProperty}' key, or a bound {textProperty})";
                violations.Add((line, $"<{type}> has no accessible name; give its x:Uid a '<uid>{AutomationNameSuffix}' resw key{alternative}, or bind {AutomationNameAttribute} or {LabeledByAttribute}"));
            }
        }

        return Format(violations);
    }

    // Keyboard focus and its visual must stay as WinUI draws them.
    public static IReadOnlyList<string> FindFocusBlockers(string xaml)
    {
        var document = Parse(xaml);
        var violations = new List<(int Line, string Message)>();

        foreach (var element in document.Descendants())
        {
            var type = element.Name.LocalName;
            if (type == "ControlTemplate")
            {
                violations.Add((LineOf(element), "<ControlTemplate> replaces the WinUI template and with it the focus visuals"));
            }
            else if (Interactive.Contains(type) && element.Attribute("IsTabStop") is { } tabStop && IsFalse(tabStop.Value))
            {
                violations.Add((LineOf(tabStop), $"IsTabStop=\"False\" takes <{type}> out of the Tab order"));
            }
        }

        foreach (var (property, value, line) in Assignments(document))
        {
            var name = Normalize(property);
            if (name == "UseSystemFocusVisuals" && IsFalse(value))
            {
                violations.Add((line, "UseSystemFocusVisuals=\"False\" hides the system focus visuals"));
            }
            else if ((name is "FocusVisualPrimaryThickness" or "FocusVisualSecondaryThickness") && IsZeroThickness(value))
            {
                violations.Add((line, $"{name}=\"{value}\" makes the focus visual invisible"));
            }
        }

        return Format(violations);
    }

    // The XAML must leave every color to the theme: no forced theme, no color of its own (a {ThemeResource} is the only way), no opacity.
    public static IReadOnlyList<string> FindThemeOverrides(string xaml)
    {
        var document = Parse(xaml);
        var violations = new List<(int Line, string Message)>();

        foreach (var element in document.Descendants().Where(element => ColorElements.Contains(element.Name.LocalName)))
        {
            violations.Add((LineOf(element), $"<{element.Name.LocalName}> sets a color of its own; use a {{ThemeResource}} instead"));
        }

        foreach (var (property, value, line) in Assignments(document))
        {
            var name = Normalize(property);
            if (name == "RequestedTheme")
            {
                violations.Add((line, $"RequestedTheme=\"{value}\" overrides the system theme"));
            }
            else if (name == "HighContrastAdjustment" && string.Equals(value, "None", StringComparison.OrdinalIgnoreCase))
            {
                violations.Add((line, "HighContrastAdjustment=\"None\" opts out of High Contrast"));
            }
            else if (IsColorProperty(name) && !value.StartsWith(ThemeResourcePrefix, StringComparison.Ordinal))
            {
                violations.Add((line, $"{name}=\"{value}\" is not a {{ThemeResource}}"));
            }
            else if (name == "Opacity" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var opacity) && opacity < 1)
            {
                violations.Add((line, $"Opacity=\"{value}\" lowers the contrast the theme guarantees"));
            }
        }

        return Format(violations);
    }

    // Comments are stripped first, so a comment that mentions a theme type does not count.
    public static IReadOnlyList<string> FindCodeThemeOverrides(string csharp)
    {
        var code = StripComments(csharp);
        return [.. CodeThemeRegex().Matches(code).Select(match => $"line {LineAt(code, match.Index)}: {match.Value} overrides or depends on a theme color; the app must follow the system theme")];
    }

    // Comments, strings and char literals in one pass, so a "//" inside a string is not a comment and a quote inside a char literal does not open a string.
    [GeneratedRegex(@"//[^\n]*|/\*.*?\*/|@""(?:[^""]|"""")*""|""(?:[^""\\\n]|\\.)*""|'(?:[^'\\\n]|\\.)'", RegexOptions.Singleline)]
    private static partial Regex CodeTokenRegex();

    [GeneratedRegex(@"\b(?:RequestedTheme|ElementTheme|ApplicationTheme|SolidColorBrush|ColorHelper|Colors)\b|Color\.FromArgb|TitleBarTheme\.(?:Light|Dark)\b")]
    private static partial Regex CodeThemeRegex();

    private static string StripComments(string csharp) =>
        CodeTokenRegex().Replace(
            csharp,
            match => match.Value[0] == '/' ? new string('\n', match.Value.AsSpan().Count('\n')) : match.Value);

    private static XDocument Parse(string xaml) => XDocument.Parse(xaml, LoadOptions.SetLineInfo);

    // Property elements ("Grid.RowDefinitions") and elements of the x: namespace ("x:Double") are not controls.
    private static IEnumerable<XElement> ClassifiableElements(XDocument document) =>
        document.Descendants().Where(element => !element.Name.LocalName.Contains('.') && element.Name.Namespace != XamlNamespace);

    private static bool HasNameSource(XElement element, IReadOnlyDictionary<string, string> strings, out string? textProperty)
    {
        var type = element.Name.LocalName;
        var uid = (string?)element.Attribute(XamlNamespace + "Uid");

        textProperty = ContentNamed.Contains(type) ? "Content" : HeaderNamed.Contains(type) ? "Header" : null;

        return IsBinding((string?)element.Attribute(AutomationNameAttribute))
            || !string.IsNullOrWhiteSpace((string?)element.Attribute(LabeledByAttribute))
            || (uid is not null && HasText(strings, uid + AutomationNameSuffix))
            || (textProperty is not null
                && (IsBinding((string?)element.Attribute(textProperty)) || (uid is not null && HasText(strings, $"{uid}.{textProperty}"))));
    }

    private static bool HasText(IReadOnlyDictionary<string, string> strings, string key) =>
        strings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value);

    private static bool IsBinding(string? value) =>
        value is not null && (value.StartsWith("{x:Bind", StringComparison.Ordinal) || value.StartsWith("{Binding", StringComparison.Ordinal));

    private static bool IsFalse(string value) => string.Equals(value.Trim(), "False", StringComparison.OrdinalIgnoreCase);

    // "0", "0,0" and "0,0,0,0" are all zero; a binding or a resource is not a literal and never counts.
    private static bool IsZeroThickness(string value)
    {
        var parts = value.Split(ThicknessSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 0
            && parts.All(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number == 0);
    }

    private static bool IsColorProperty(string name) =>
        name is "Fill" or "Stroke"
        || name.EndsWith("Brush", StringComparison.Ordinal)
        || name.EndsWith("Color", StringComparison.Ordinal)
        || name.EndsWith("Foreground", StringComparison.Ordinal)
        || name.EndsWith("Background", StringComparison.Ordinal);

    // "TextElement.Foreground" and "Foreground" are the same property.
    private static string Normalize(string property) => property[(property.LastIndexOf('.') + 1)..];

    // Every value set in the markup: the plain attributes and the Property/Value pairs of Setters.
    private static IEnumerable<(string Property, string Value, int Line)> Assignments(XDocument document)
    {
        foreach (var element in document.Descendants())
        {
            foreach (var attribute in element.Attributes().Where(attribute => attribute.Name.Namespace == XNamespace.None && !attribute.IsNamespaceDeclaration))
            {
                yield return (attribute.Name.LocalName, attribute.Value, LineOf(attribute));
            }

            if (element.Name.LocalName == "Setter" && element.Attribute("Property") is { } property)
            {
                yield return (property.Value, (string?)element.Attribute("Value") ?? string.Empty, LineOf(element));
            }
        }
    }

    private static IReadOnlyList<string> Format(List<(int Line, string Message)> violations) =>
        [.. violations.OrderBy(violation => violation.Line).Select(violation => $"line {violation.Line}: {violation.Message}")];

    private static int LineOf(XObject node) => node is IXmlLineInfo { } info && info.HasLineInfo() ? info.LineNumber : 0;

    private static int LineAt(string source, int index) => source.AsSpan(0, index).Count('\n') + 1;
}
