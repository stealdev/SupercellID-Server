using ScidServer.Configuration;
using ScidServer.Models;

namespace ScidServer.Api;

public sealed class ScidJson(ServerOptions options)
{
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private object[] AvailableSystems =>
        [new { system = "laser", deeplink = $"{options.UniversalLinkBase}/" }];

    private string ProfileLink(Profile p) => $"{options.UniversalLinkBase}/?supercell_id&p={p.Scid}";

    public Dictionary<string, object?> Profile(Profile p, bool publicView = false, string? relationship = null)
    {
        var now = Now;
        var json = new Dictionary<string, object?>
        {
            ["universalLink"] = ProfileLink(p),
            ["name"] = new { name = p.Name, changeAllowed = now + 30L * 86_400_000 },
            ["avatarImage"] = publicView ? p.Avatar : new { image = p.Avatar },
            ["profileImage"] = p.Avatar
        };
        if (relationship != null)
            json["relationship"] = new { status = relationship, timestamp = now };

        json["qrCodeURL"] = ProfileLink(p);
        json["forcedOfflineStatus"] = p.ForcedOffline;
        json["blockIncomingFriendRequests"] = p.BlockIncomingFriendRequests;
        json["supportTier"] = 1;
        json["scid"] = p.Scid;
        json["accountId"] = p.Scid;
        json["handle"] = new { id = p.Handle, isChangeAllowed = true };
        json["availableSystems"] = AvailableSystems;
        json["connectedSystems"] = new[]
        {
            new
            {
                system = "laser",
                application = "laser-prod",
                username = p.PlayerName,
                progress = new[] { p.Trophies.ToString(), "0" },
                score = p.Trophies,
                seasonPassActive = false,
                seasonPassType = "DEFAULT",
                seasonPassProgress = 0,
                applicationAccountId = p.Pid
            }
        };
        json["characterClass"] = "connected_games_multi";
        json["identifier"] = new { type = "EMAIL", value = p.Email ?? "", scid_id = p.Scid };
        json["featurePermissions"] = new { };
        return json;
    }

    public object Friend(Profile other, string status, long timestamp) => new
    {
        accountId = other.Scid,
        handle = other.Handle,
        name = other.Name,
        profileImage = other.Avatar,
        gameAccountId = other.GameAccountId,
        playerName = other.PlayerName,
        scid = other.Scid,
        avatarImage = other.Avatar,
        applicationAccountId = other.Pid,
        relationship = new { status, timestamp },
        availableSystems = AvailableSystems,
        connectedSystems = new[] { "laser" }
    };

    public object PublicProfileSummary(Profile p, string relationship) => new
    {
        accountId = p.Scid,
        profileImage = p.Avatar,
        handle = p.Handle,
        name = p.Name,
        playerName = p.PlayerName,
        gameAccountId = p.GameAccountId,
        scid = p.Scid,
        applicationAccountId = p.Pid,
        avatarImage = p.Avatar,
        availableSystems = AvailableSystems,
        connectedSystems = new[]
        {
            new
            {
                system = "laser",
                application = "laser-prod",
                username = p.PlayerName,
                progress = new[] { p.Trophies.ToString(), "0" },
                score = p.Trophies,
                seasonPassActive = false,
                applicationAccountId = p.Pid
            }
        },
        relationship = new { status = relationship }
    };

    public object Item(Item item, string ownerScid, string? status = null, string? claimToken = null,
        string? productIdOverride = null)
    {
        var now = Now;
        status ??= item.IsPendingDonation ? "DONATION_RECEIVED" : "AVAILABLE";
        var productId = item.ProductId;
        return new
        {
            id = item.Id,
            type = item.IsSeasonPass ? "SEASON_PASS" : "UNKNOWN",
            tier = "DEFAULT",
            origin = "STORE_FREE_OFFER_CLAIM",
            accountId = ownerScid,
            createdAt = item.CreatedAt.ToUnixTimeMilliseconds(),
            status = new { status, timestamp = now, claimToken },
            productId = string.IsNullOrEmpty(productIdOverride) ? productId : productIdOverride,
            receiptId = $"SUPERCELL_STORE_FREE_OFFER_CLAIM:{ownerScid}#{productId}#1",
            applications = status == "CLAIM_IN_PROGRESS" ? new[] { "laser-prod" } : new[] { "laser", "laser-prod" },
            contents = item.Contents,
            title = status == "AVAILABLE" ? item.Title : null
        };
    }
}
