using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace PasswordManager.App.E2E.Tests.Localization;

// Static checks behind "every UI text lives in Resources.resw". Pure functions over source text, no window and no Roslyn.
// Each Find method returns one entry per violation; an empty list means the source complies.
internal static partial class LocalizationGuard
{
    private const string GetStringCall = "GetString(";

    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    // The Debug-only command-line switches the E2E tests pass to the app. A switch name is not UI text; matched exactly.
    private static readonly HashSet<string> DebugSwitches = new(StringComparer.Ordinal)
    {
        "--vaults-dir", "--idle-timeout-seconds", "--lock-on-minimize",
    };

    // Properties that put text on screen or in front of a screen reader. In XAML their value must be a binding or come from an x:Uid, never a literal.
    private static readonly HashSet<string> TextAttributes = new(StringComparer.Ordinal)
    {
        "Text", "Content", "Header", "PlaceholderText", "Title", "Description", "Label", "Message", "Subtitle",
        "OnContent", "OffContent",
        "PrimaryButtonText", "SecondaryButtonText", "CloseButtonText",
        "ToolTipService.ToolTip",
        "AutomationProperties.Name", "AutomationProperties.HelpText", "AutomationProperties.FullDescription", "AutomationProperties.LocalizedControlType",
    };

    // Elements whose text content is a value, not text for the user.
    private static readonly HashSet<string> ValueElements = new(StringComparer.Ordinal)
    {
        "Double", "Int32", "Boolean", "Thickness", "CornerRadius", "Color", "GridLength",
    };

    public static IReadOnlyList<string> FindXamlLiterals(string xaml)
    {
        var document = XDocument.Parse(xaml, LoadOptions.SetLineInfo);
        var violations = new List<(int Line, string Message)>();

        foreach (var attribute in document.Descendants().Attributes())
        {
            if (attribute.Name.Namespace == XNamespace.None
                && TextAttributes.Contains(attribute.Name.LocalName)
                && IsLiteralText(attribute.Value))
            {
                violations.Add((LineOf(attribute), $"{attribute.Name.LocalName}=\"{attribute.Value}\""));
            }
        }

        foreach (var text in document.DescendantNodes().OfType<XText>())
        {
            var parent = text.Parent;
            if (!string.IsNullOrWhiteSpace(text.Value) && (parent is null || !ValueElements.Contains(parent.Name.LocalName)))
            {
                violations.Add((LineOf(text, parent), $"text '{text.Value.Trim()}' inside <{parent?.Name.LocalName}>"));
            }
        }

        return [.. violations.OrderBy(v => v.Line).Select(v => $"line {v.Line}: {v.Message}")];
    }

    public static IReadOnlyList<string> FindCodeLiterals(string csharp)
    {
        var violations = new List<string>();
        var index = 0;
        while (index < csharp.Length)
        {
            var current = csharp[index];
            var next = index + 1 < csharp.Length ? csharp[index + 1] : '\0';
            if (current == '/' && next == '/')
            {
                index = SkipLineComment(csharp, index);
            }
            else if (current == '/' && next == '*')
            {
                index = SkipBlockComment(csharp, index);
            }
            else if (current == '\'')
            {
                index = SkipCharLiteral(csharp, index);
            }
            else if (TryReadString(csharp, index, out var literal))
            {
                if (!IsAllowed(csharp, literal))
                {
                    violations.Add($"line {LineAt(csharp, literal.Start)}: {Excerpt(csharp[literal.Start..literal.End])}");
                }

                index = literal.End;
            }
            else
            {
                index++;
            }
        }

        return violations;
    }

    public static IReadOnlyList<string> Uids(string xaml) =>
        [.. XDocument.Parse(xaml).Descendants().Attributes(XamlNamespace + "Uid").Select(attribute => attribute.Value)];

    public static IReadOnlyList<string> CodeKeys(string csharp) =>
        [.. CodeKeyRegex().Matches(csharp).Select(match => match.Groups[1].Value)];

    // Cross-checks the resw against the x:Uids of the XAML and the GetString keys of the code, in both directions.
    // A dotted key "Uid.Property" belongs to an x:Uid; a plain key belongs to code.
    public static IReadOnlyList<string> FindKeyMismatches(
        IReadOnlyList<KeyValuePair<string, string>> entries,
        IEnumerable<string> uids,
        IEnumerable<string> codeKeys)
    {
        var violations = new List<string>();
        var uidSet = uids.ToHashSet(StringComparer.Ordinal);
        var codeKeySet = codeKeys.ToHashSet(StringComparer.Ordinal);

        foreach (var duplicate in entries.GroupBy(entry => entry.Key, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            violations.Add($"duplicate key '{duplicate.Key}'");
        }

        foreach (var (key, value) in entries)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                violations.Add($"key '{key}' has an empty value");
            }
        }

        var dottedKeys = entries.Select(entry => entry.Key).Where(key => key.Contains('.')).Distinct(StringComparer.Ordinal).ToArray();
        var uidsWithKeys = dottedKeys.Select(UidOf).ToHashSet(StringComparer.Ordinal);
        foreach (var uid in uidSet.Order(StringComparer.Ordinal).Where(uid => !uidsWithKeys.Contains(uid)))
        {
            violations.Add($"x:Uid '{uid}' has no '{uid}.*' key in the resw");
        }

        foreach (var key in dottedKeys.Where(key => !uidSet.Contains(UidOf(key))))
        {
            violations.Add($"key '{key}' matches no x:Uid in the XAML");
        }

        var plainKeys = entries.Select(entry => entry.Key).Where(key => !key.Contains('.')).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var key in plainKeys.Where(key => !codeKeySet.Contains(key)))
        {
            violations.Add($"key '{key}' is not used by any GetString call");
        }

        foreach (var key in codeKeySet.Order(StringComparer.Ordinal).Where(key => !plainKeys.Contains(key, StringComparer.Ordinal)))
        {
            violations.Add($"GetString(\"{key}\") has no plain key in the resw");
        }

        return violations;
    }

    [GeneratedRegex("""GetString\(\s*"([^"]+)"\s*\)""")]
    private static partial Regex CodeKeyRegex();

    // "new SomeException(": the message that follows is developer text and never reaches the UI.
    [GeneratedRegex(@"new\s+(?:\w+\.)*\w*Exception\s*\(")]
    private static partial Regex ExceptionConstructorRegex();

    // "[LibraryImport(" or "[DllImport(" with no ")" after it: the literal is an argument of the native import attribute.
    [GeneratedRegex(@"\[\s*(?:LibraryImport|DllImport)\s*\([^)\]]*$")]
    private static partial Regex NativeImportAttributeRegex();

    private static string UidOf(string key) => key[..key.IndexOf('.', StringComparison.Ordinal)];

    // A value is a literal unless it is a markup extension; "{}" is the XAML escape that makes a brace-leading value literal.
    private static bool IsLiteralText(string value) =>
        value.Length > 0 && (value[0] != '{' || value.StartsWith("{}", StringComparison.Ordinal));

    private static int LineOf(XObject node, XElement? fallback = null) =>
        node is IXmlLineInfo { } info && info.HasLineInfo()
            ? info.LineNumber
            : fallback is IXmlLineInfo { } parentInfo && parentInfo.HasLineInfo() ? parentInfo.LineNumber : 0;

    private static int LineAt(string source, int index) => source.AsSpan(0, index).Count('\n') + 1;

    private static string Excerpt(string literal)
    {
        var firstLine = literal.Split('\n')[0].TrimEnd('\r');
        return firstLine.Length <= 60 ? firstLine : string.Concat(firstLine.AsSpan(0, 57), "...");
    }

    private static bool IsAllowed(string source, StringLiteral literal) =>
        FollowsGetString(source, literal.Start)
        || IsInExceptionConstructor(source, literal.Start)
        || IsInNativeImportAttribute(source, literal.Start)
        || DebugSwitches.Contains(source[literal.ContentStart..literal.ContentEnd]);

    // The code right before the literal, ignoring whitespace, is "GetString(".
    private static bool FollowsGetString(string source, int literalStart)
    {
        var end = literalStart;
        while (end > 0 && char.IsWhiteSpace(source[end - 1]))
        {
            end--;
        }

        if (!source.AsSpan(0, end).EndsWith(GetStringCall, StringComparison.Ordinal))
        {
            return false;
        }

        var nameStart = end - GetStringCall.Length;
        return nameStart == 0 || !(char.IsLetterOrDigit(source[nameStart - 1]) || source[nameStart - 1] == '_');
    }

    // "new SomeException(" appears on the literal's line before the literal.
    private static bool IsInExceptionConstructor(string source, int literalStart) =>
        ExceptionConstructorRegex().IsMatch(TextBeforeOnLine(source, literalStart));

    // "[LibraryImport(" or "[DllImport(" opens on the literal's line before the literal, and no ")" has closed it yet.
    private static bool IsInNativeImportAttribute(string source, int literalStart) =>
        NativeImportAttributeRegex().IsMatch(TextBeforeOnLine(source, literalStart));

    private static ReadOnlySpan<char> TextBeforeOnLine(string source, int index)
    {
        var lineStart = index == 0 ? 0 : source.LastIndexOf('\n', index - 1) + 1;
        return source.AsSpan(lineStart, index - lineStart);
    }

    private static int SkipLineComment(string source, int index)
    {
        var end = source.IndexOf('\n', index);
        return end < 0 ? source.Length : end;
    }

    private static int SkipBlockComment(string source, int index)
    {
        var end = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
        return end < 0 ? source.Length : end + 2;
    }

    // At the opening quote of 'x', '\'' or 'A'.
    private static int SkipCharLiteral(string source, int index)
    {
        index++;
        if (index < source.Length && source[index] == '\\')
        {
            index++;
        }

        index++;
        while (index < source.Length && source[index] != '\'')
        {
            index++;
        }

        return Math.Min(index + 1, source.Length);
    }

    // At the opening quote of a plain "..." inside an interpolation hole.
    private static int SkipPlainString(string source, int index)
    {
        index++;
        while (index < source.Length && source[index] != '"')
        {
            index += source[index] == '\\' ? 2 : 1;
        }

        return Math.Min(index + 1, source.Length);
    }

    // Just after the '{' of an interpolation hole; returns the index after the matching '}'.
    private static int SkipHole(string source, int index)
    {
        var depth = 1;
        while (index < source.Length)
        {
            switch (source[index])
            {
                case '{':
                    depth++;
                    index++;
                    break;
                case '}':
                    depth--;
                    index++;
                    if (depth == 0)
                    {
                        return index;
                    }

                    break;
                case '"':
                    index = SkipPlainString(source, index);
                    break;
                case '\'':
                    index = SkipCharLiteral(source, index);
                    break;
                default:
                    index++;
                    break;
            }
        }

        return index;
    }

    // Recognizes regular ("..."), verbatim (@"..."), interpolated ($"...", $@"...", @$"...") and raw ("""...""") strings.
    private static bool TryReadString(string source, int start, out StringLiteral literal)
    {
        literal = default;
        var quote = start;
        var interpolated = false;
        var verbatim = false;
        while (quote < source.Length && (source[quote] == '$' || source[quote] == '@'))
        {
            interpolated |= source[quote] == '$';
            verbatim |= source[quote] == '@';
            quote++;
        }

        if (quote >= source.Length || source[quote] != '"')
        {
            return false;
        }

        var quotes = 0;
        while (quote + quotes < source.Length && source[quote + quotes] == '"')
        {
            quotes++;
        }

        if (quotes >= 3 && !verbatim)
        {
            var contentStart = quote + quotes;
            for (var cursor = contentStart; cursor < source.Length;)
            {
                if (source[cursor] != '"')
                {
                    cursor++;
                    continue;
                }

                var run = 0;
                while (cursor + run < source.Length && source[cursor + run] == '"')
                {
                    run++;
                }

                if (run >= quotes)
                {
                    literal = new StringLiteral(start, contentStart, cursor, cursor + run);
                    return true;
                }

                cursor += run;
            }

            literal = new StringLiteral(start, contentStart, source.Length, source.Length);
            return true;
        }

        var open = quote + 1;
        for (var cursor = open; cursor < source.Length;)
        {
            var current = source[cursor];
            if (current == '"')
            {
                if (verbatim && cursor + 1 < source.Length && source[cursor + 1] == '"')
                {
                    cursor += 2;
                    continue;
                }

                literal = new StringLiteral(start, open, cursor, cursor + 1);
                return true;
            }

            if (current == '\\' && !verbatim)
            {
                cursor += 2;
            }
            else if (interpolated && current == '{')
            {
                cursor = cursor + 1 < source.Length && source[cursor + 1] == '{' ? cursor + 2 : SkipHole(source, cursor + 1);
            }
            else
            {
                cursor++;
            }
        }

        literal = new StringLiteral(start, open, source.Length, source.Length);
        return true;
    }

    // Start..End spans the whole token with its prefix and quotes; ContentStart..ContentEnd is what sits between the quotes.
    private readonly record struct StringLiteral(int Start, int ContentStart, int ContentEnd, int End);
}
