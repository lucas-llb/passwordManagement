using Microsoft.UI;
using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.Windows.Storage.Pickers;
using PasswordManager.Core.Vault;
using PasswordManager.ViewModels;

namespace PasswordManager.App;

/// <summary>
/// <see cref="IExportFilePicker"/> over the Windows App SDK save dialog (<see cref="FileSavePicker"/>). It is owned by
/// the app window it is created with, so it needs no window handle initialization. It only returns the path the user
/// chose: it never creates the file, and it never offers or uses a temp or cache folder.
/// </summary>
internal sealed class WindowsExportFilePicker : IExportFilePicker
{
    private readonly WindowId _window;

    /// <param name="window">The window that owns the dialog.</param>
    public WindowsExportFilePicker(WindowId window)
    {
        _window = window;
    }

    /// <inheritdoc/>
    public async Task<string?> PickAsync()
    {
        // The file type name and the suggested name come from the resw, so no text lives in code.
        var resources = new ResourceLoader();
        var picker = new FileSavePicker(_window)
        {
            SuggestedFileName = resources.GetString("ExportSuggestedFileName"),
            DefaultFileExtension = VaultFormat.FileExtension,
        };
        picker.FileTypeChoices.Add(resources.GetString("ExportFileTypeName"), [VaultFormat.FileExtension]);

        // Null when the user cancels.
        var result = await picker.PickSaveFileAsync();
        return result?.Path;
    }
}
