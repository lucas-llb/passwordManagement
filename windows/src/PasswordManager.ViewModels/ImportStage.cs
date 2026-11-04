namespace PasswordManager.ViewModels;

/// <summary>The step of the import page the user is on.</summary>
public enum ImportStage
{
    /// <summary>No file yet, or the last one could not be read.</summary>
    ChooseFile,

    /// <summary>A <c>.pmvault</c> was read and looks like a vault; its password is needed to decrypt it.</summary>
    Password,

    /// <summary>The file was validated and is in memory; nothing was written yet.</summary>
    Preview,

    /// <summary>The vault was saved with the imported entries.</summary>
    Done,
}
