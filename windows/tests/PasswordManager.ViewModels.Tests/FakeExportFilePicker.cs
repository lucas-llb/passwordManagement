namespace PasswordManager.ViewModels.Tests;

/// <summary>
/// An <see cref="IExportFilePicker"/> and <see cref="IImportFilePicker"/> that answers with a fixed path, or null for a
/// canceled dialog. A test can run a callback right before the answer (the person locking the vault while the dialog is
/// open), hold the dialog open until <see cref="Release"/>, and count how often it was asked.
/// </summary>
internal sealed class FakeExportFilePicker(string? path) : IExportFilePicker, IImportFilePicker
{
    private TaskCompletionSource? _gate;

    /// <summary>The path the dialog returns, or null for a canceled dialog.</summary>
    public string? Path { get; set; } = path;

    /// <summary>Runs after the dialog is released (or at once, when it is not blocked) and before it returns.</summary>
    public Action? BeforeReturn { get; set; }

    /// <summary>How many times <see cref="PickAsync"/> was called.</summary>
    public int Calls { get; private set; }

    /// <summary>The next <see cref="PickAsync"/> call does not complete until <see cref="Release"/>.</summary>
    public void Block() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Lets the blocked <see cref="PickAsync"/> call complete.</summary>
    public void Release()
    {
        _gate?.TrySetResult();
        _gate = null;
    }

    public async Task<string?> PickAsync()
    {
        Calls++;
        if (_gate is { } gate)
        {
            await gate.Task;
        }

        BeforeReturn?.Invoke();
        return Path;
    }
}
