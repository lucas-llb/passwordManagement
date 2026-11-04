namespace PasswordManager.ViewModels;

/// <summary>
/// Asks the user which file to import. The app implements it with the system open dialog; tests use a fake. The
/// ViewModels never know which dialog it is, and the picker never reads the file: <see cref="ImportViewModel"/> does.
/// </summary>
public interface IImportFilePicker
{
    /// <summary>Shows the dialog and waits for the user to choose.</summary>
    /// <returns>The full path the user chose, or null when the user canceled.</returns>
    Task<string?> PickAsync();
}
