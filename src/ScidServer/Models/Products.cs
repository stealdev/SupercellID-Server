namespace ScidServer.Models;

public static class Products
{
    public const string FreeOfferBase = "com.supercell.laser.scid.specialoffertier0.free";

    public const string SeasonPassBasic = "com.supercell.laser.scid.seasonpass.basic";
    public const string SeasonPassPlus = "com.supercell.laser.scid.seasonpass.plus";

    public static bool IsSeasonPass(string productId) =>
        productId is SeasonPassBasic or SeasonPassPlus;

    public static string ItemIdFrom(string productId)
    {
        const string prefix = FreeOfferBase + ".";
        return productId.StartsWith(prefix, StringComparison.Ordinal) ? productId[prefix.Length..] : "";
    }
}
