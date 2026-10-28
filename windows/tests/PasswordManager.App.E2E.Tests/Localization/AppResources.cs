using System.Globalization;
using System.Xml.Linq;

namespace PasswordManager.App.E2E.Tests.Localization;

// Reads the App sources and Resources.resw straight from the repository, so the guards and the E2E tests share one view of them.
internal static class AppResources
{
    // The key format of an attached property in a resw: "<uid>.[using:<namespace>]<Owner>.<Property>".
    private const string AutomationNameSuffix = ".[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name";

    private static readonly string[] ValueProperties = ["Text", "Content", "Header"];

    private static readonly Lazy<IReadOnlyDictionary<string, string>> LazyStrings = new(
        () => ToStrings(ReadEntries(File.ReadAllText(ReswPath))));

    public static string SourceDir => AppSession.Metadata("SourceDir");

    public static string AppDir => Path.Combine(SourceDir, "PasswordManager.App");

    public static string ViewModelsDir => Path.Combine(SourceDir, "PasswordManager.ViewModels");

    public static string ReswPath => Path.Combine(AppDir, "Strings", "pt-BR", "Resources.resw");

    public static IReadOnlyDictionary<string, string> Strings => LazyStrings.Value;

    // A resw value with {0}-style placeholders, formatted as the app formats it (current culture).
    public static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Strings[key], args);

    // Recursive; build output folders (bin, obj) hold generated copies of the sources, so they never count.
    public static IReadOnlyList<string> SourceFiles(string directory, string pattern) =>
        [.. Directory
            .EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(Path.GetRelativePath(directory, path)))
            .Order(StringComparer.Ordinal)];

    public static IReadOnlyList<KeyValuePair<string, string>> ReadEntries(string reswXml) =>
        [.. XDocument.Parse(reswXml).Root!
            .Elements("data")
            .Select(data => KeyValuePair.Create(
                (string?)data.Attribute("name") ?? throw new InvalidDataException("A <data> element has no name."),
                data.Element("value")?.Value ?? string.Empty))];

    public static IReadOnlyDictionary<string, string> ToStrings(IReadOnlyList<KeyValuePair<string, string>> entries)
    {
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            if (!strings.TryAdd(key, value))
            {
                throw new InvalidDataException($"Duplicate resw key '{key}'.");
            }
        }

        return strings;
    }

    // The text UI Automation shows for the element with this x:Uid: an AutomationProperties.Name key when there is one
    // (so the tests stay valid once elements get their own accessible names), otherwise its Text, Content or Header.
    public static string ExpectedName(string uid) => ExpectedName(uid, Strings);

    public static string ExpectedName(string uid, IReadOnlyDictionary<string, string> strings)
    {
        if (strings.TryGetValue(uid + AutomationNameSuffix, out var automationName))
        {
            return automationName;
        }

        var keys = ValueProperties.Select(property => $"{uid}.{property}").Where(strings.ContainsKey).ToArray();
        return keys.Length == 1
            ? strings[keys[0]]
            : throw new InvalidOperationException(
                $"Expected exactly one of {uid}.Text, {uid}.Content and {uid}.Header in the resw, found {keys.Length}.");
    }

    private static bool IsBuildOutput(string relativePath)
    {
        var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments[..^1].Any(segment =>
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase) || segment.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }
}
