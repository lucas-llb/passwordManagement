namespace PasswordManager.ViewModels;

/// <summary>
/// What the export page hands to <see cref="ExportViewModel.ExportCommand"/>: the export password typed twice and the
/// picker to ask for the destination. It deliberately has no <c>ToString</c> override and is not a record, so nothing
/// prints the arrays.
/// </summary>
/// <remarks>
/// The command takes ownership of both arrays and zeroes them before it returns, whatever the outcome; callers must not
/// read them afterwards.
/// </remarks>
public sealed class ExportRequest
{
    /// <summary>Wraps the arguments without copying the arrays.</summary>
    /// <param name="password">The export password.</param>
    /// <param name="confirmation">The password typed a second time.</param>
    /// <param name="picker">Asks the user where to save the file.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public ExportRequest(char[] password, char[] confirmation, IExportFilePicker picker)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(confirmation);
        ArgumentNullException.ThrowIfNull(picker);

        Password = password;
        Confirmation = confirmation;
        Picker = picker;
    }

    /// <summary>The export password. Zeroed by the export command.</summary>
    public char[] Password { get; }

    /// <summary>The confirmation. Zeroed by the export command.</summary>
    public char[] Confirmation { get; }

    /// <summary>The destination dialog.</summary>
    public IExportFilePicker Picker { get; }
}
