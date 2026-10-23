namespace ScidServer.Models;

public sealed class Profile
{
    public const string DefaultAvatar = "0,moco_support_cat,#1D1D1D,#878787";

    public required string Scid { get; init; }
    public long GameAccountId { get; set; }
    public string? Email { get; set; }
    public required string Name { get; set; }
    public required string Handle { get; set; }
    public required string PlayerName { get; set; }
    public string Avatar { get; set; } = DefaultAvatar;
    public int Trophies { get; set; }
    public bool ForcedOffline { get; set; }
    public bool BlockIncomingFriendRequests { get; set; }
    public bool MarketingPermission { get; set; }
    public int SessionVersion { get; set; } = 1;
    public string AccountToken { get; set; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string Pid => $"0-{GameAccountId}";
}
