using System.Text.Json.Serialization;

namespace Sessions.Core.Models;

/// <summary>
/// One private-key file registered with Sessions. The file stays where the user put it;
/// this record contains only its reference and non-secret public metadata.
/// </summary>
public sealed record SshKeyReference
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string? Algorithm { get; init; }
    public int? KeyLength { get; init; }
    public string? Fingerprint { get; init; }
    public bool? IsEncrypted { get; init; }
    public string? PublicKey { get; init; }

    [JsonIgnore]
    public bool IsAvailable => File.Exists(Path);
}
