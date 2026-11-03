using Microsoft.UI;
using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.Windows.Storage.Pickers;
using PasswordManager.Core.Vault;
using PasswordManager.ViewModels;

namespace PasswordManager.App;

/// <summary>
/// <see cref="IExportFilePicker"/> over the Windows App SDK save dialog (<see cref="FileSavePicker"/>). It is owned by
/// the app window it is created with, so it needs no window handle initialization. It only returns the path the user
/// chose: it never creates the file, and it never offers or uses a temp or cache folder. Create it with
/// <see cref="ForVault"/> (encrypted export) or <see cref="ForCsv"/> (plaintext CSV).
/// </summary>
internal sealed class WindowsExportFilePicker : IExportFilePicker
{
    private readonly WindowId _window;
    private readonly string _suggestedFileName;
    private readonly string _fileTypeName;
    private readonly string _extension;

    private WindowsExportFilePicker(WindowId window, string suggestedFileName, string fileTypeName, string extension)
    {
        _window = window;
        _suggestedFileName = suggestedFileName;
        _fileTypeName = fileTypeName;
        _extension = extension;
    }

    /// <summary>The picker of the encrypted <c>.pmvault</c> export.</summary>
    /// <param name="window">The window that owns the dialog.</param>
    public static WindowsExportFilePicker ForVault(WindowId window)
    {
        // The file type name and the suggested name come from the resw, so no text lives in code. Each factory holds
        // its own literal keys: the localization guard only resolves keys from literal GetString calls.
        var resources = new ResourceLoader();
        return new WindowsExportFilePicker(
            window,
            resources.GetString("ExportSuggestedFileName"),
            resources.GetString("ExportFileTypeName"),
            VaultFormat.FileExtension);
    }

    /// <summary>The picker of the plaintext <c>.csv</c> export.</summary>
    /// <param name="window">The window that owns the dialog.</param>
    public static WindowsExportFilePicker ForCsv(WindowId window)
    {
        var resources = new ResourceLoader();
        return new WindowsExportFilePicker(
            window,
            resources.GetString("CsvExportSuggestedFileName"),
            resources.GetString("CsvExportFileTypeName"),
            VaultCsv.FileExtension);
    }

    /// <inheritdoc/>
    public async Task<string?> PickAsync()
    {
        var picker = new FileSavePicker(_window)
        {
            SuggestedFileName = _suggestedFileName,
            DefaultFileExtension = _extension,
        };
        picker.FileTypeChoices.Add(_fileTypeName, [_extension]);

        // Null when the user cancels.
        var result = await picker.PickSaveFileAsync();
        return result?.Path;
    }
}
