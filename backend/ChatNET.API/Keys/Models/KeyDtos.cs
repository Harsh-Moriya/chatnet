namespace ChatNET.API.Keys.Models;

// Bytes travel as base64url without padding, as everywhere else in the API.
public record BundleDto(int Generation, string Bundle);

public record BackupInfoDto(int Version, DateTime UpdatedAt);

// Whether a person has set up encryption, with the public keys of every generation (oldest first) and the version of their
// backup. Setting up and reading the state answer with this.
public record KeyStatusDto(List<BundleDto> Bundles, BackupInfoDto? Backup);

public record KeyBackupDto(string Blob, int Version, DateTime UpdatedAt);
