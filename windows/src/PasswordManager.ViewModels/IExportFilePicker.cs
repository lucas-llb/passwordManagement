namespace PasswordManager.ViewModels;

/// <summary>
/// Asks the user where to save an export. The app implements it with the system save dialog; tests use a fake. The
/// ViewModels never know which dialog it is, and the picker never creates the file: <see cref="ExportViewModel"/> does.
/// </summary>
public interface IExportFilePicker
{
    /// <summary>Shows the dialog and waits for the user to choose.</summary>
    /// <returns>The full path the user chose, or null when the user canceled.</returns>
    Task<string?> PickAsync();
}
