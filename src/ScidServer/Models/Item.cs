namespace ScidServer.Models;

public sealed class Item
{
    public required string Id { get; init; }
    public long OwnerGameAccountId { get; set; }
    public required Dictionary<string, long> Contents { get; init; }
    public string Title { get; init; } = "GIFT!!!";
    public string? ImageUrl { get; init; }
    public string? BackgroundImageUrl { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool Claimed { get; set; }
    public long DonationSenderGameAccountId { get; set; }
    public string DonationSenderScid { get; set; } = "";
    public bool WasDonated { get; set; }

    public bool IsPendingDonation => DonationSenderGameAccountId > 0;
    public bool IsSeasonPass => HasContent("BrawlPass") || HasContent("BrawlPassPlus");

    public string ProductId =>
        HasContent("BrawlPassPlus") ? Products.SeasonPassPlus :
        HasContent("BrawlPass") ? Products.SeasonPassBasic :
        $"{Products.FreeOfferBase}.{Id}";

    private bool HasContent(string name) =>
        Contents.Keys.Any(key => string.Equals(key.Split(':', 2)[0].Trim(), name, StringComparison.OrdinalIgnoreCase));
}
