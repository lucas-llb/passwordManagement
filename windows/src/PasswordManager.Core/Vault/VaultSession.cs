using System.Security.Cryptography;

namespace PasswordManager.Core.Vault;

/// <summary>
/// The one Core entry point the UI uses for a profile's vault: create, unlock, lock, save, change the master password,
/// export and import. It only composes <see cref="VaultFile"/>, <see cref="VaultHeader"/>, <see cref="KeySlot"/>,
/// <see cref="VaultBody"/>, <see cref="MasterPassword"/> and <see cref="RecoveryKit"/>; it adds no cryptography.
/// </summary>
/// <remarks>
/// <para>While unlocked, the session holds the Vault Key in a pinned buffer (the GC never moves or copies it), the
/// bytes of the file it unlocked or last wrote, and the decrypted <see cref="VaultContent"/>. <see cref="Lock"/>
/// zeroes the key and drops the other two. The content and its strings are managed objects that cannot be zeroed
/// (AGENTS.md §3.5): locking only drops the session's references to them.</para>
/// <para>Callers should not share one session between threads, but every member takes the same private gate, so a
/// <see cref="Lock"/> from another thread (for example, on a Windows session switch) waits for an in-flight write and
/// never lets a body be sealed with a zeroed key. The gate also serializes writes, which <see cref="VaultFile"/>
/// requires.</para>
/// <para><see cref="Create"/>, <see cref="Unlock"/>, <see cref="ChangePassword"/>, <see cref="Export"/> and
/// <see cref="Import"/> run Argon2id (about one second at the default parameters), so callers run them off the UI
/// thread.</para>
/// <para>Password and kit-text buffers are never modified, except the <c>kitText</c> output of <see cref="Create"/>;
/// the caller owns and zeroes them. Exceptions never carry secrets or file content.</para>
/// </remarks>
public sealed class VaultSession : IDisposable
{
    private const string LockedMessage = "Vault session is locked.";
    private const string AlreadyUnlockedMessage = "Vault session is already unlocked.";
    private const string ParametersOutOfRangeMessage = "KDF parameters are outside the format bounds.";

    private readonly Lock _gate = new();
    private readonly byte[] _vaultKey = GC.AllocateArray<byte>(VaultFormat.KeySize, pinned: true);
    private readonly KdfParameters _newSlotParameters;
    private byte[]? _file;
    private VaultContent? _content;
    private bool _disposed;

    /// <summary>
    /// Creates a locked session over a vault file. It validates the path only and never touches the disk.
    /// </summary>
    /// <param name="vaultPath">Path of the profile's <c>.pmvault</c> file.</param>
    /// <param name="newSlotParameters">Argon2id cost parameters for the slots this session creates (a changed master
    /// password, an export). Defaults to <see cref="KdfParameters.Default"/>; lower values are for tests only.</param>
    /// <exception cref="ArgumentNullException"><paramref name="vaultPath"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="vaultPath"/> is empty or not a valid path.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="newSlotParameters"/> is outside the format
    /// bounds.</exception>
    public VaultSession(string vaultPath, KdfParameters? newSlotParameters = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(vaultPath);

        var parameters = newSlotParameters ?? KdfParameters.Default;
        if (!parameters.IsInRange)
        {
            throw new ArgumentOutOfRangeException(nameof(newSlotParameters), ParametersOutOfRangeMessage);
        }

        VaultPath = Path.GetFullPath(vaultPath);
        _newSlotParameters = parameters;
    }

    /// <summary>Full path of the vault file.</summary>
    public string VaultPath { get; }

    /// <summary>True while the vault is unlocked. False once the session is locked or disposed.</summary>
    public bool IsUnlocked
    {
        get
        {
            lock (_gate)
            {
                return _content is not null;
            }
        }
    }

    /// <summary>The decrypted content of the vault, as last unlocked or saved.</summary>
    /// <exception cref="ObjectDisposedException">The session is disposed.</exception>
    /// <exception cref="InvalidOperationException">The session is locked.</exception>
    public VaultContent Content
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _content ?? throw new InvalidOperationException(LockedMessage);
            }
        }
    }

    /// <summary>The live Vault Key buffer, for tests that check it is zeroed. Never use it outside tests.</summary>
    internal byte[] VaultKeyBufferForTesting => _vaultKey;

    /// <summary>
    /// Builds the bytes of a new vault: a fresh random Vault Key wrapped by <paramref name="password"/> (type-1 slot)
    /// and by a new Recovery Kit (type-2 slot), and an empty body. Nothing is written: the caller passes the bytes to
    /// the profile repository.
    /// </summary>
    /// <remarks>Costs two Argon2id runs, so callers run it off the UI thread.</remarks>
    /// <param name="password">The master password. Not modified; the caller zeroes it.</param>
    /// <param name="kitText">Receives the Recovery Kit text, exactly <see cref="RecoveryKit.TextLength"/> chars.
    /// Written only on success; the caller zeroes it after showing it.</param>
    /// <param name="parameters">Argon2id cost parameters for both slots, normally <see cref="KdfParameters.Default"/>.</param>
    /// <returns>The whole file bytes.</returns>
    /// <exception cref="ArgumentException"><paramref name="kitText"/> has the wrong length, or
    /// <paramref name="password"/> contains an invalid surrogate.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="parameters"/> is outside the format bounds.</exception>
    public static byte[] Create(ReadOnlySpan<char> password, Span<char> kitText, KdfParameters parameters)
    {
        if (kitText.Length != RecoveryKit.TextLength)
        {
            throw new ArgumentException("Recovery kit text buffer has an invalid length.", nameof(kitText));
        }

        if (!parameters.IsInRange)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), ParametersOutOfRangeMessage);
        }

        Span<byte> vaultKey = stackalloc byte[VaultFormat.KeySize];
        try
        {
            RandomNumberGenerator.Fill(vaultKey);
            var slot = KeySlot.CreatePasswordSlot(password, parameters, vaultKey);
            var file = VaultBody.Seal(VaultHeader.Build([slot]), vaultKey, new VaultContent());
            return RecoveryKit.Create(file, vaultKey, parameters, kitText);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(vaultKey);
        }
    }

    /// <summary>
    /// Reads the vault file, unwraps the Vault Key with the master password and decrypts the content. On any failure
    /// the key is zeroed and the session stays locked.
    /// </summary>
    /// <remarks>Costs one Argon2id run, so callers run it off the UI thread.</remarks>
    /// <param name="password">The master password. Not modified; the caller zeroes it.</param>
    /// <exception cref="ObjectDisposedException">The session is disposed.</exception>
    /// <exception cref="InvalidOperationException">The session is already unlocked.</exception>
    /// <exception cref="IOException">The file cannot be read (for example, <see cref="FileNotFoundException"/>).</exception>
    /// <exception cref="UnauthorizedAccessException">Reading the file was denied.</exception>
    /// <exception cref="VaultFormatException">The file is not a valid v1 vault, or its body is not valid vault
    /// JSON.</exception>
    /// <exception cref="VaultAuthenticationException">Wrong password, or the file was tampered with.</exception>
    public void Unlock(ReadOnlySpan<char> password)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_content is not null)
            {
                throw new InvalidOperationException(AlreadyUnlockedMessage);
            }

            var unlocked = false;
            try
            {
                var file = VaultFile.Read(VaultPath);
                var header = VaultHeader.Parse(file);
                KeySlot.UnwrapPasswordSlot(file, header.PasswordSlotOffset, password, _vaultKey);
                var content = VaultBody.Open(file, header.BodyOffset, _vaultKey);

                _file = file;
                _content = content;
                unlocked = true;
            }
            finally
            {
                if (!unlocked)
                {
                    CryptographicOperations.ZeroMemory(_vaultKey);
                }
            }
        }
    }

    /// <summary>
    /// Zeroes the Vault Key and drops the content and the cached file bytes. Idempotent, and a no-op once the session
    /// is disposed. Waits for an operation running on another thread to finish first.
    /// </summary>
    public void Lock()
    {
        lock (_gate)
        {
            LockCore();
        }
    }

    /// <summary>Locks the session and marks it disposed; later operations throw <see cref="ObjectDisposedException"/>.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            LockCore();
            _disposed = true;
        }
    }

    /// <summary>
    /// Encrypts <paramref name="content"/> under the header of the unlocked file with a fresh body nonce and writes it
    /// atomically (the previous version becomes the <c>.bak</c> copy). Every slot, unknown ones included, stays byte
    /// for byte. The session takes the new content and file bytes only after the write succeeds; on failure it is
    /// unchanged.
    /// </summary>
    /// <param name="content">The whole new content.</param>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.</exception>
    /// <exception cref="InvalidOperationException">The session is locked.</exception>
    /// <exception cref="IOException">The write failed; the file on disk is unchanged.</exception>
    /// <exception cref="UnauthorizedAccessException">The write was denied; the file on disk is unchanged.</exception>
    public void Save(VaultContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        lock (_gate)
        {
            var file = RequireUnlockedFile();
            var bodyOffset = VaultHeader.Parse(file).BodyOffset;
            var newFile = VaultBody.Seal(file.AsSpan(0, bodyOffset), _vaultKey, content);

            VaultFile.Write(VaultPath, newFile);

            _file = newFile;
            _content = content;
        }
    }

    /// <summary>
    /// Replaces the master password: only the type-1 slot changes (the Vault Key, the kit slot and unknown slots stay)
    /// and the body is re-sealed under a fresh nonce. The new file is written atomically; the session stays unlocked.
    /// </summary>
    /// <remarks>Costs two Argon2id runs, so callers run it off the UI thread.</remarks>
    /// <param name="currentPassword">The current master password. Not modified; the caller zeroes it.</param>
    /// <param name="newPassword">The new master password. Not modified; the caller zeroes it.</param>
    /// <exception cref="ObjectDisposedException">The session is disposed.</exception>
    /// <exception cref="InvalidOperationException">The session is locked.</exception>
    /// <exception cref="ArgumentException"><paramref name="newPassword"/> contains an invalid surrogate.</exception>
    /// <exception cref="VaultAuthenticationException"><paramref name="currentPassword"/> is wrong; nothing is
    /// written and the session stays unlocked.</exception>
    /// <exception cref="IOException">The write failed; the file on disk and the session are unchanged.</exception>
    /// <exception cref="UnauthorizedAccessException">The write was denied; the file on disk and the session are
    /// unchanged.</exception>
    public void ChangePassword(ReadOnlySpan<char> currentPassword, ReadOnlySpan<char> newPassword)
    {
        lock (_gate)
        {
            var file = RequireUnlockedFile();
            var newFile = MasterPassword.Change(file, currentPassword, newPassword, _newSlotParameters);

            VaultFile.Write(VaultPath, newFile);

            _file = newFile;
        }
    }

    /// <summary>
    /// Builds an encrypted export of the current content: a new random Vault Key, wrapped only by
    /// <paramref name="exportPassword"/> (exactly one type-1 slot, no kit slot). Nothing is written: the caller saves
    /// the bytes where the user chooses.
    /// </summary>
    /// <remarks>Costs one Argon2id run, so callers run it off the UI thread.</remarks>
    /// <param name="exportPassword">The export password. Not modified; the caller zeroes it.</param>
    /// <returns>The whole export file bytes, in the vault format.</returns>
    /// <exception cref="ObjectDisposedException">The session is disposed.</exception>
    /// <exception cref="InvalidOperationException">The session is locked.</exception>
    /// <exception cref="ArgumentException"><paramref name="exportPassword"/> contains an invalid surrogate.</exception>
    public byte[] Export(ReadOnlySpan<char> exportPassword)
    {
        lock (_gate)
        {
            RequireUnlockedFile();
            var content = _content!;

            Span<byte> exportKey = stackalloc byte[VaultFormat.KeySize];
            try
            {
                RandomNumberGenerator.Fill(exportKey);
                var slot = KeySlot.CreatePasswordSlot(exportPassword, _newSlotParameters, exportKey);
                return VaultBody.Seal(VaultHeader.Build([slot]), exportKey, content);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(exportKey);
            }
        }
    }

    /// <summary>
    /// Decrypts an export (or any vault file) with its password and returns its content for preview. The session and
    /// the disk are not changed: the caller merges the entries and calls <see cref="Save"/>.
    /// </summary>
    /// <remarks>Costs one Argon2id run, so callers run it off the UI thread.</remarks>
    /// <param name="file">The whole file bytes. Not modified.</param>
    /// <param name="exportPassword">The password of the file's type-1 slot. Not modified; the caller zeroes it.</param>
    /// <returns>The decrypted content of <paramref name="file"/>.</returns>
    /// <exception cref="ObjectDisposedException">The session is disposed.</exception>
    /// <exception cref="InvalidOperationException">The session is locked.</exception>
    /// <exception cref="VaultFormatException"><paramref name="file"/> is not a valid v1 vault, or its body is not
    /// valid vault JSON.</exception>
    /// <exception cref="VaultAuthenticationException">Wrong password, or the file was tampered with.</exception>
    public VaultContent Import(ReadOnlySpan<byte> file, ReadOnlySpan<char> exportPassword)
    {
        lock (_gate)
        {
            RequireUnlockedFile();

            var header = VaultHeader.Parse(file);
            Span<byte> importKey = stackalloc byte[VaultFormat.KeySize];
            try
            {
                KeySlot.UnwrapPasswordSlot(file, header.PasswordSlotOffset, exportPassword, importKey);
                return VaultBody.Open(file, header.BodyOffset, importKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(importKey);
            }
        }
    }

    // Callers hold _gate.
    private byte[] RequireUnlockedFile()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _file ?? throw new InvalidOperationException(LockedMessage);
    }

    // Callers hold _gate.
    private void LockCore()
    {
        CryptographicOperations.ZeroMemory(_vaultKey);
        _content = null;
        _file = null;
    }
}
