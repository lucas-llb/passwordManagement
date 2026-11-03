namespace PasswordManager.ViewModels;

/// <summary>How the last attempt of <see cref="ExportViewModel.ExportCommand"/> ended. The view maps each value to its own text.</summary>
public enum ExportOutcome
{
    /// <summary>Nothing to show: no attempt yet, the attempt was refused by the password rule, canceled, or the vault locked.</summary>
    None,

    /// <summary>The export was written to the chosen file.</summary>
    Saved,

    /// <summary>The export could not be written (the folder is missing or denied, the file is locked, and so on); nothing was changed.</summary>
    Failed,

    /// <summary>The chosen file is in the app's own vaults folder, so it was refused to keep a vault or the profile list from being overwritten.</summary>
    VaultsFolder,
}
