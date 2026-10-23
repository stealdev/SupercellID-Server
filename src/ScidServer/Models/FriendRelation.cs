namespace ScidServer.Models;

public sealed class FriendRelation
{
    public required string Requester { get; init; }
    public required string Target { get; init; }
    public bool Accepted { get; set; }
    public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
