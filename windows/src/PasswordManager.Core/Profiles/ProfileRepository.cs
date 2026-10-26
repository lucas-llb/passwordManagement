using System.Globalization;
using System.Text;
using System.Text.Json;
using PasswordManager.Core.Storage;
using PasswordManager.Core.Vault;

namespace PasswordManager.Core.Profiles;

/// <summary>
/// Lists, creates, renames and deletes profiles in <c>profiles.json</c> and owns their <c>&lt;id&gt;.pmvault</c> files
/// (docs/vault-format.md, "profiles.json").
/// </summary>
/// <remarks>
/// <para>The class is not thread-safe, and two instances (or two processes) doing read-modify-write on the same folder
/// can lose an update: callers must serialize every call. There is no cache: each call re-reads the file.</para>
/// <para>A missing <c>profiles.json</c> (or a missing folder) means there are no profiles. A corrupt one is an error
/// (<see cref="ProfileStoreException"/>) and is never overwritten automatically.</para>
/// <para>File names come only from the profile <see cref="Guid"/>, never from the display name. Orphan
/// <c>.pmvault</c> files (not listed in <c>profiles.json</c>) are never deleted automatically; only
/// <see cref="Delete"/> removes the files of the id it is given.</para>
/// <para>Exceptions never carry profile names or file content.</para>
/// </remarks>
public sealed class ProfileRepository
{
    /// <summary>Name of the profile list inside the vaults folder.</summary>
    public const string ProfilesFileName = "profiles.json";

    /// <summary>Application folder inside <c>%LOCALAPPDATA%</c> when the app is not packaged.</summary>
    public const string AppFolderName = "PasswordManagement";

    /// <summary>Folder that holds <c>profiles.json</c> and the vault files.</summary>
    public const string VaultsFolderName = "vaults";

    /// <summary>Maximum length of a display name, in UTF-16 code units, after normalization and trimming.</summary>
    public const int MaxDisplayNameLength = 64;

    private const string CorruptListMessage = "Profile list is corrupt.";
    private const string EmptyNameMessage = "Profile name is empty.";
    private const string TooLongNameMessage = "Profile name is too long.";
    private const string InvalidCharacterNameMessage = "Profile name has an invalid character.";
    private const string DuplicateNameMessage = "Profile name is already in use.";
    private const string NotFoundMessage = "Profile not found.";
    private const string LocalFolderUnavailableMessage = "Local application data folder is unavailable.";

    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates a repository over a vaults folder. It never touches the disk.
    /// </summary>
    /// <param name="vaultsDirectory">The vaults folder (see <see cref="GetDefaultDirectory"/>). It may not exist yet.</param>
    /// <param name="timeProvider">Clock used for <see cref="Profile.CreatedAt"/>; defaults to the system clock.</param>
    /// <exception cref="ArgumentNullException"><paramref name="vaultsDirectory"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="vaultsDirectory"/> is empty.</exception>
    public ProfileRepository(string vaultsDirectory, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(vaultsDirectory);

        VaultsDirectory = Path.GetFullPath(vaultsDirectory);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Full path of the vaults folder.</summary>
    public string VaultsDirectory { get; }

    private string ProfilesPath => Path.Combine(VaultsDirectory, ProfilesFileName);

    /// <summary>
    /// Works out the default vaults folder. Core is plain <c>net10.0</c> and cannot call <c>ApplicationData</c>, so
    /// the packaged app passes <c>ApplicationData.Current.LocalFolder.Path</c> in.
    /// </summary>
    /// <param name="packagedLocalFolder">The package's local folder, or null when the app is not packaged.</param>
    /// <returns><c>%LOCALAPPDATA%\PasswordManagement\vaults</c>, or <c>&lt;packagedLocalFolder&gt;\vaults</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="packagedLocalFolder"/> is empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">The local application data folder is unavailable.</exception>
    public static string GetDefaultDirectory(string? packagedLocalFolder = null)
    {
        if (packagedLocalFolder is null)
        {
            var localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify);
            if (string.IsNullOrEmpty(localAppData))
            {
                throw new InvalidOperationException(LocalFolderUnavailableMessage);
            }

            return Path.Combine(localAppData, AppFolderName, VaultsFolderName);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(packagedLocalFolder);

        return Path.Combine(packagedLocalFolder, VaultsFolderName);
    }

    /// <summary>
    /// Returns the path of the vault file of a profile: <c>&lt;VaultsDirectory&gt;\&lt;lowercase id&gt;.pmvault</c>.
    /// </summary>
    /// <param name="id">The profile id.</param>
    /// <exception cref="ArgumentException"><paramref name="id"/> is <see cref="Guid.Empty"/>.</exception>
    public string GetVaultPath(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Profile id is empty.", nameof(id));
        }

        return Path.Combine(VaultsDirectory, id.ToString("D", CultureInfo.InvariantCulture) + VaultFormat.FileExtension);
    }

    /// <summary>
    /// Reads <c>profiles.json</c>. Profiles come back in file order, which is creation order.
    /// </summary>
    /// <returns>The profiles; empty when the folder or the file is missing (nothing is created).</returns>
    /// <exception cref="ProfileStoreException">The file is not valid JSON of the expected shape, has a null or
    /// whitespace-named profile, an empty id or a duplicate id.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public IReadOnlyList<Profile> List() => ReadProfiles();

    /// <summary>
    /// Checks a display name without writing anything.
    /// </summary>
    /// <param name="displayName">The name typed by the user.</param>
    /// <param name="renamingId">The profile being renamed, which is skipped by the duplicate check.</param>
    /// <returns>The first rule the name breaks, or <see cref="ProfileNameError.None"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="displayName"/> is null.</exception>
    /// <exception cref="ProfileStoreException"><c>profiles.json</c> is corrupt.</exception>
    public ProfileNameError ValidateDisplayName(string displayName, Guid? renamingId = null)
    {
        ArgumentNullException.ThrowIfNull(displayName);

        return Check(displayName, renamingId, ReadProfiles(), out _);
    }

    /// <summary>
    /// Adds a profile and writes its first <c>&lt;id&gt;.pmvault</c>, so a listed profile always has a vault on disk.
    /// </summary>
    /// <param name="displayName">The name typed by the user; it is stored NFC-normalized and trimmed.</param>
    /// <param name="vaultFile">The initial vault file bytes. Validated, not modified.</param>
    /// <returns>The new profile.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="displayName"/> is null.</exception>
    /// <exception cref="ArgumentException">The name breaks a rule of <see cref="ValidateDisplayName"/>.</exception>
    /// <exception cref="VaultFormatException"><paramref name="vaultFile"/> is not a valid v1 vault file; the disk is not touched.</exception>
    /// <exception cref="ProfileStoreException"><c>profiles.json</c> is corrupt; nothing is written.</exception>
    /// <exception cref="IOException">A write failed; the new vault file is removed on a best-effort basis.</exception>
    /// <exception cref="UnauthorizedAccessException">A write was denied; the new vault file is removed on a best-effort basis.</exception>
    public Profile Create(string displayName, ReadOnlySpan<byte> vaultFile)
    {
        ArgumentNullException.ThrowIfNull(displayName);

        var profiles = ReadProfiles();
        var error = Check(displayName, renamingId: null, profiles, out var normalized);
        if (MessageFor(error) is { } message)
        {
            throw new ArgumentException(message, nameof(displayName));
        }

        VaultHeader.Parse(vaultFile);

        Directory.CreateDirectory(VaultsDirectory);

        var id = Guid.NewGuid();
        var profile = new Profile
        {
            Id = id,
            DisplayName = normalized,
            CreatedAt = TruncateToMilliseconds(_timeProvider.GetUtcNow()),
        };

        var vaultPath = GetVaultPath(id);
        VaultFile.Write(vaultPath, vaultFile);

        try
        {
            Save([.. profiles, profile]);
        }
        catch
        {
            DeleteQuietly(vaultPath);
            throw;
        }

        return profile;
    }

    /// <summary>
    /// Changes the display name of a profile. Id, creation date and list order stay; vault files are not touched.
    /// A case-only rename of the same profile is allowed.
    /// </summary>
    /// <param name="id">The profile id.</param>
    /// <param name="displayName">The new name; it is stored NFC-normalized and trimmed.</param>
    /// <returns>The renamed profile.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="displayName"/> is null.</exception>
    /// <exception cref="KeyNotFoundException">The id is not listed.</exception>
    /// <exception cref="ArgumentException">The name breaks a rule of <see cref="ValidateDisplayName"/>.</exception>
    /// <exception cref="ProfileStoreException"><c>profiles.json</c> is corrupt.</exception>
    public Profile Rename(Guid id, string displayName)
    {
        ArgumentNullException.ThrowIfNull(displayName);

        var profiles = ReadProfiles();
        var index = profiles.FindIndex(p => p.Id == id);
        if (index < 0)
        {
            throw new KeyNotFoundException(NotFoundMessage);
        }

        var error = Check(displayName, id, profiles, out var normalized);
        if (MessageFor(error) is { } message)
        {
            throw new ArgumentException(message, nameof(displayName));
        }

        var renamed = profiles[index] with { DisplayName = normalized };
        profiles[index] = renamed;
        Save(profiles);

        return renamed;
    }

    /// <summary>
    /// Removes a profile from <c>profiles.json</c> first, then deletes its <c>.pmvault</c>, <c>.bak</c> and
    /// <c>.tmp</c> files. If a file cannot be deleted the error propagates; a retry returns <c>false</c> but still
    /// removes the leftovers.
    /// </summary>
    /// <param name="id">The profile id.</param>
    /// <returns>Whether the id was listed.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is <see cref="Guid.Empty"/>.</exception>
    /// <exception cref="ProfileStoreException"><c>profiles.json</c> is corrupt; no file is deleted.</exception>
    /// <exception cref="IOException">A file could not be deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">A file could not be deleted.</exception>
    public bool Delete(Guid id)
    {
        var vaultPath = GetVaultPath(id);
        if (!Directory.Exists(VaultsDirectory))
        {
            return false;
        }

        var profiles = ReadProfiles();
        var index = profiles.FindIndex(p => p.Id == id);
        var listed = index >= 0;
        if (listed)
        {
            profiles.RemoveAt(index);
            Save(profiles);
        }

        File.Delete(vaultPath);
        File.Delete(vaultPath + VaultFile.BackupSuffix);
        File.Delete(vaultPath + VaultFile.TempSuffix);

        return listed;
    }

    private static ProfileNameError Check(
        string displayName,
        Guid? renamingId,
        List<Profile> profiles,
        out string normalized)
    {
        normalized = string.Empty;

        if (HasInvalidCharacter(displayName))
        {
            return ProfileNameError.InvalidCharacter;
        }

        normalized = displayName.Normalize(NormalizationForm.FormC).Trim();
        if (normalized.Length == 0)
        {
            return ProfileNameError.Empty;
        }

        if (normalized.Length > MaxDisplayNameLength)
        {
            return ProfileNameError.TooLong;
        }

        foreach (var profile in profiles)
        {
            if (profile.Id != renamingId
                && string.Equals(profile.DisplayName, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return ProfileNameError.Duplicate;
            }
        }

        return ProfileNameError.None;
    }

    private static bool HasInvalidCharacter(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsControl(c))
            {
                return true;
            }

            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(c))
            {
                return true;
            }
        }

        return false;
    }

    private static string? MessageFor(ProfileNameError error) => error switch
    {
        ProfileNameError.None => null,
        ProfileNameError.Empty => EmptyNameMessage,
        ProfileNameError.TooLong => TooLongNameMessage,
        ProfileNameError.InvalidCharacter => InvalidCharacterNameMessage,
        _ => DuplicateNameMessage,
    };

    private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset instant)
    {
        var ticks = instant.UtcTicks;

        return new DateTimeOffset(ticks - (ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: the original error is the one that matters.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort, as above.
        }
    }

    private List<Profile> ReadProfiles()
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(ProfilesPath);
        }
        catch (FileNotFoundException)
        {
            return [];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }

        ProfileList? list;
        try
        {
            list = JsonSerializer.Deserialize(bytes, ProfileJsonContext.Default.ProfileList);
        }
        catch (JsonException)
        {
            // No inner exception: its message can hold file content.
            throw new ProfileStoreException(CorruptListMessage);
        }

        if (list is null)
        {
            throw new ProfileStoreException(CorruptListMessage);
        }

        var ids = new HashSet<Guid>();
        foreach (var profile in list.Profiles)
        {
            if (profile is null
                || profile.Id == Guid.Empty
                || string.IsNullOrWhiteSpace(profile.DisplayName)
                || !ids.Add(profile.Id))
            {
                throw new ProfileStoreException(CorruptListMessage);
            }
        }

        return [.. list.Profiles];
    }

    private void Save(List<Profile> profiles)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new ProfileList { Profiles = profiles },
            ProfileJsonContext.Default.ProfileList);

        AtomicFile.Write(ProfilesPath, bytes, keepBackup: false);
    }
}
