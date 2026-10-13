using System.Globalization;

namespace XBsync.Models;

public enum AuthenticationStatus { Success, InteractionRequired, AccountMismatch, ServiceFailure }
public enum MainSection { Games, CloudSaves, Profiles, Settings }

public sealed record XboxProfile(string Xuid, string Gamertag, Uri? AvatarUri = null);
public sealed record AuthenticationResult(AuthenticationStatus Status, XboxProfile? Profile, string Message)
{
    public bool IsSuccess => Status == AuthenticationStatus.Success && Profile is not null;
}

public sealed record GameTitle(string TitleId, string Name, string? ServiceConfigurationId,
    string? PackageFamilyName, Uri? ArtworkUri, DateTimeOffset? LastPlayed = null, double? HoursPlayed = null)
{
    public string PlaytimeText => HoursPlayed is { } hours ? $"{hours:0.0} hours" : "Playtime unavailable";
    public string LastPlayedText => LastPlayed is { } date ? $"Last played: {date.LocalDateTime:d}" : "Last played unavailable";
    public string CloudStateText => "Cloud status unverified";
    public bool HasStorageIdentity => Guid.TryParse(ServiceConfigurationId, out var scid) && scid != Guid.Empty;
}

public sealed record CloudSaveContainer(string Id, string Name, string? ETag = null);
public sealed record CloudSaveFile(string ContainerId, string Id, string Name, long Size,
    DateTimeOffset? Modified, string? ETag, string Type)
{
    public string? AtomReference { get; init; }
    public string? ContainerETag { get; init; }
    public string SizeText => Size < 0 ? "Unavailable" : Size >= 1048576 ? $"{Size / 1048576d:0.0} MB" : Size >= 1024 ? $"{Size / 1024d:0.#} KB" : $"{Size} B";
    public string ModifiedText => Modified?.LocalDateTime.ToString("g", CultureInfo.CurrentCulture) ?? "Unavailable";
    public string LogicalPath => $"{ContainerId}/{Name}";
}

public sealed record TransferProgress(long BytesTransferred, long? TotalBytes);
//public sealed record ReplacementResult(CloudSaveFile File, bool Verified)
//{
    //public string? BackupPath { get; init; }
//}
public sealed record ActivityEntry(DateTimeOffset Time, string Description)
{
    public string TimeText => Time.LocalDateTime.ToString("g");
}

public sealed class XboxAuthenticationOptions
{
    public string? ClientId { get; init; } = "c36a9fb6-4f2a-41ff-90bd-ae7cc92031eb";
    public string Scope { get; init; } = "Xboxlive.signin Xboxlive.offline_access";
    public string? ReferenceRefreshTokenPath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "xbcsmgrrev", "reftoken");
}

public sealed record DeviceCodePrompt(string UserCode, Uri VerificationUri, DateTimeOffset ExpiresAt);
