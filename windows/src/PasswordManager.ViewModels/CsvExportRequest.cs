namespace PasswordManager.ViewModels;

/// <summary>
/// What the CSV export page hands to <see cref="CsvExportViewModel.ExportCommand"/>: the master password typed again
/// and the picker to ask for the destination. It deliberately has no <c>ToString</c> override and is not a record, so
/// nothing prints the array.
/// </summary>
/// <remarks>
/// The command takes ownership of the array and zeroes it before it returns, whatever the outcome; callers must not
/// read it afterwards.
/// </remarks>
public sealed class CsvExportRequest
{
    /// <summary>Wraps the arguments without copying the array.</summary>
    /// <param name="masterPassword">The master password of the open vault.</param>
    /// <param name="picker">Asks the user where to save the file.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public CsvExportRequest(char[] masterPassword, IExportFilePicker picker)
    {
        ArgumentNullException.ThrowIfNull(masterPassword);
        ArgumentNullException.ThrowIfNull(picker);

        MasterPassword = masterPassword;
        Picker = picker;
    }

    /// <summary>The master password. Zeroed by the export command.</summary>
    public char[] MasterPassword { get; }

    /// <summary>The destination dialog.</summary>
    public IExportFilePicker Picker { get; }
}
