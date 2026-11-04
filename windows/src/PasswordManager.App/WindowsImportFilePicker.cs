using Microsoft.UI;
using Microsoft.Windows.Storage.Pickers;
using PasswordManager.Core.Vault;
using PasswordManager.ViewModels;

namespace PasswordManager.App;

/// <summary>
/// <see cref="IImportFilePicker"/> over the Windows App SDK open dialog (<see cref="FileOpenPicker"/>). It is owned by
/// the app window it is created with, so it needs no window handle initialization. It only returns the path the user
/// chose: it never reads, copies or changes the file, and it never offers or uses a temp or cache folder. Both
/// extensions the import accepts are offered, so the dialog does not decide the format: the view model does, by the
/// extension.
/// </summary>
internal sealed class WindowsImportFilePicker : IImportFilePicker
{
    private readonly WindowId _window;

    /// <param name="window">The window that owns the dialog.</param>
    public WindowsImportFilePicker(WindowId window)
    {
        _window = window;
    }

    /// <inheritdoc/>
    public async Task<string?> PickAsync()
    {
        var picker = new FileOpenPicker(_window);
        picker.FileTypeFilter.Add(VaultFormat.FileExtension);
        picker.FileTypeFilter.Add(VaultCsv.FileExtension);

        // Null when the user cancels.
        var result = await picker.PickSingleFileAsync();
        return result?.Path;
    }
}
