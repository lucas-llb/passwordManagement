namespace PasswordManager.Core.Vault;

public sealed record VaultEntry
{
    public required Guid Id { get; init; }
    public required string Title { get; init; }
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public string Url { get; init; } = "";
    public string Notes { get; init; } = "";
    public string Folder { get; init; } = "";
    public bool Favorite { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }

    // Never print secrets (logs, debugger, exceptions).
    public override string ToString() => $"VaultEntry {{ Id = {Id}, Title = {Title} }}";
}
