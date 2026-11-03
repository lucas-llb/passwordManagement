namespace PasswordManager.ViewModels;

/// <summary>The rule that both export pages (<c>.pmvault</c> and CSV) apply to the file the user picked.</summary>
internal static class ExportDestination
{
    /// <summary>
    /// True when the file would sit directly in the vaults folder, where it could replace <c>&lt;id&gt;.pmvault</c> or
    /// <c>profiles.json</c>. Subfolders are fine. Both sides are compared as full paths without a trailing separator;
    /// Windows paths ignore case.
    /// </summary>
    /// <param name="path">The file the user picked.</param>
    /// <param name="vaultsDirectory">The vaults folder of the app.</param>
    /// <exception cref="ArgumentException">A path is empty or not a valid path.</exception>
    internal static bool IsInVaultsFolder(string path, string vaultsDirectory)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (folder is null)
        {
            return false;
        }

        var vaults = Path.TrimEndingDirectorySeparator(Path.GetFullPath(vaultsDirectory));

        return string.Equals(Path.TrimEndingDirectorySeparator(folder), vaults, StringComparison.OrdinalIgnoreCase);
    }
}
