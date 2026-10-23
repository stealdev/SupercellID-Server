using Microsoft.AspNetCore.WebUtilities;
using ScidServer.Models;
using ScidServer.Security;

namespace ScidServer.Api;

public sealed partial class ScidApi
{
    private Task<IResult> ItemsList(HttpContext ctx)
    {
        var me = CurrentSession(ctx).Profile;
        var items = store.PendingItems(me.GameAccountId).Select(i => json.Item(i, me.Scid)).ToArray();
        return Task.FromResult(Ok(new { items }));
    }

    private async Task<IResult> ItemsClaim(HttpContext ctx)
    {
        var me = CurrentSession(ctx).Profile;
        var productId = (await ReadForm(ctx))["productId"].ToString();
        var itemId = Products.ItemIdFrom(productId);
        var pending = store.PendingItems(me.GameAccountId).Where(i => !i.IsPendingDonation).ToArray();

        var item = itemId.Length > 0
            ? pending.FirstOrDefault(i => i.Id == itemId)
            : pending.FirstOrDefault(i => i.ProductId == productId);
        item ??= pending.Length == 1 ? pending[0] : null;
        if (item == null) return Fail("not_found");

        store.ClaimItem(me.GameAccountId, item.Id);
        log.LogInformation("Claimed {ItemId} ({Contents}) for game account {GameAccountId}",
            item.Id, string.Join(", ", item.Contents.Select(c => $"{c.Key}={c.Value}")), me.GameAccountId);

        var claimToken = tokens.CreateClaimToken(me.Scid, me.Pid, item);
        return Ok(new { item = json.Item(item, me.Scid, "CLAIM_IN_PROGRESS", claimToken, productId) });
    }

    private async Task<IResult> ItemsClaimComplete(HttpContext ctx)
    {
        var me = CurrentSession(ctx).Profile;
        var ticket = tokens.ReadClaim((await ReadForm(ctx))["token"].ToString());
        if (ticket == null || ticket.Scid != me.Scid || ticket.Pid != me.Pid)
            return Fail("invalid_claim_token", StatusCodes.Status401Unauthorized);

        store.ClaimItem(me.GameAccountId, ticket.ItemId);
        return Results.Json(new { ok = true });
    }

    private async Task<IResult> ItemsDonate(HttpContext ctx)
    {
        var me = CurrentSession(ctx).Profile;
        var form = await ReadForm(ctx);
        var receiverId = form["receiverAccountId"].ToString();
        var receiver = receiverId.StartsWith("0-", StringComparison.Ordinal)
            ? store.FindByGameAccount(TokenService.ParseGameAccountId(receiverId))
            : store.FindByScid(receiverId);
        if (receiver == null) return Fail("receiver_not_found");

        var item = FindPendingItem(me, form["itemId"].ToString(), form["productId"].ToString(), donation: false);
        if (item == null) return Fail("item_not_available");

        var error = store.DonateItem(me, receiver, item);
        if (error != null) return Fail(error);

        _ = hub.PushPending(receiver);
        return Ok(new { item = json.Item(item, me.Scid, "DONATION_SENT") });
    }

    private async Task<IResult> ItemsResolveDonation(HttpContext ctx, bool accept)
    {
        var me = CurrentSession(ctx).Profile;
        var form = await ReadForm(ctx);
        var item = FindPendingItem(me, form["itemId"].ToString(), form["productId"].ToString(), donation: true);
        if (item == null) return Fail("donation_not_found");

        var sender = store.FindByGameAccount(item.DonationSenderGameAccountId);
        var error = store.ResolveDonation(me, item, accept);
        if (error != null) return Fail(error);

        if (!accept && sender != null)
            _ = hub.PushPending(sender);
        return Ok(new { item = json.Item(item, me.Scid, accept ? "AVAILABLE" : "DONATION_REJECTED") });
    }

    private Item? FindPendingItem(Profile owner, string itemId, string productId, bool donation)
    {
        var candidates = store.PendingItems(owner.GameAccountId).Where(i => i.IsPendingDonation == donation).ToArray();
        var match = candidates.FirstOrDefault(i =>
            (itemId.Length > 0 && i.Id == itemId) || (productId.Length > 0 && i.ProductId == productId));
        if (match != null || !donation) return match;
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private async Task<IResult> NotificationAck(HttpContext ctx)
    {
        var me = CurrentSession(ctx).Profile;
        var body = QueryHelpers.ParseQuery(await ReadBody(ctx));
        if (body.TryGetValue("event", out var evt) && evt.ToString() == "dismiss"
            && body.TryGetValue("token", out var token)
            && tokens.ReadClaim(token.ToString()) is { } ticket
            && ticket.Scid == me.Scid
            && !Products.IsSeasonPass(ticket.ProductId))
        {
            store.ClaimItem(me.GameAccountId, ticket.ItemId);
            log.LogInformation("Claimed {ItemId} on dismiss for game account {GameAccountId}", ticket.ItemId, me.GameAccountId);
        }
        return Results.Json(new { ok = true });
    }
}
