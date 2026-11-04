namespace PasswordManager.ViewModels;

/// <summary>
/// One imported entry as the preview shows it. It has no password, notes or folder, so the page cannot show them.
/// </summary>
/// <remarks>A class and not a record, so no generated member can print the fields.</remarks>
public sealed class ImportPreviewRow
{
    internal ImportPreviewRow(string title, string username, string url, bool isDuplicate)
    {
        Title = title;
        Username = username;
        Url = url;
        IsDuplicate = isDuplicate;
    }

    /// <summary>The title of the entry.</summary>
    public string Title { get; }

    /// <summary>The username of the entry.</summary>
    public string Username { get; }

    /// <summary>The URL of the entry.</summary>
    public string Url { get; }

    /// <summary>True when the entry matches a vault entry.</summary>
    public bool IsDuplicate { get; }
}
