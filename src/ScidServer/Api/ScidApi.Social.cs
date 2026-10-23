using System.Text.RegularExpressions;
using ScidServer.Models;
using ScidServer.Security;

namespace ScidServer.Api;

public sealed partial class ScidApi
{
    private static readonly string[] HandleAdjectives =
    [
        "Swift", "Brave", "Golden", "Silent", "Mighty", "Cosmic", "Frost", "Shadow",
        "Crimson", "Lucky", "Royal", "Turbo", "Mega", "Hyper", "Solar", "Lunar",
        "Iron", "Wild", "Noble", "Rapid", "Epic", "Stealth", "Blazing", "Frozen"
    ];

    private static readonly string[] HandleNouns =
    [
        "Tiger", "Wolf", "Falcon", "Dragon", "Shark", "Eagle", "Panther", "Viper",
        "Phoenix", "Raven", "Lion", "Bear", "Hawk", "Cobra", "Fox", "Knight",
        "Ninja", "Ranger", "Hunter", "Wizard", "Titan", "Comet", "Blade", "Ghost"
    ];

    private Task<IResult> SessionInit(HttpContext ctx)
    {
        var me = CurrentSession(ctx).Profile;
        var relations = store.RelationsOf(me.Scid);
        var friends = relations.Where(r => r.Status == Relationship.Friend).ToList();
        var incoming = relations.Where(r => r.Status == Relationship.RequestReceived).ToList();
        var outgoing = relations.Where(r => r.Status == Relationship.RequestSent).ToList();

        var data = json.Profile(me);
        data["friends"] = friends.Select(r => json.Friend(r.Other, r.Status, r.Timestamp)).ToArray();
        data["friendRequests"] = incoming.Concat(outgoing).Select(r => json.Friend(r.Other, r.Status, r.Timestamp)).ToArray();
        data["outgoingRequests"] = outgoing.Select(r => json.Friend(r.Other, r.Status, r.Timestamp)).ToArray();
        data["incomingFriendRequestsCount"] = incoming.Count;
        data["ctaCampaigns"] = Array.Empty<object>();
        data["isQuarantined"] = false;
        data["sp"] = new { allowed = false };
        data["items"] = store.PendingItems(me.GameAccountId).Select(i => json.Item(i, me.Scid)).ToArray();
        return Task.FromResult(Ok(data));
    }

    private async Task<IResult> ProfileGet(HttpContext ctx)
    {
        var me = CurrentSession(ctx).Profile;
        var targetScid = (await ReadForm(ctx))["scid"].ToString();
        if (targetScid.Length == 0 || targetScid == me.Scid)
            return Ok(json.Profile(me));

        var target = store.FindByScid(targetScid);
        if (target == null) return Fail("binding_not_found");
        return Ok(json.Profile(target, publicView: true, store.RelationshipBetween(me.Scid, target.Scid)));
    }

    private async Task<IResult> SetAvatar(HttpContext ctx)
    {
        var me = CurrentSession(ctx).Profile;
        var avatar = (await ReadForm(ctx))["avatarImage"].ToString();
        if (avatar.Length > 0) store.Update(() => me.Avatar = avatar);
        return Ok(json.Profile(me));
    }

    private async Task<IResult> SetHandle(HttpContext ctx)
    {
        var me = CurrentSession(ctx).Profile;
        var handle = (await ReadForm(ctx))["handle"].ToString().Trim();
        if (handle.Length < 5) return Fail("profile_handle_too_short");
        if (handle.Length > 18) return Fail("profile_handle_too_long");
        if (!Regex.IsMatch(handle, "^[a-zA-Z0-9]+$")) return Fail("invalid_handle");
        if (!store.TrySetHandle(me, handle)) return Fail("old_handle");
        return Ok(json.Profile(me));
    }

    private async Task<IResult> SetName(HttpContext ctx)
    {
        var me = CurrentSession(ctx).Profile;
        var name = (await ReadForm(ctx))["name"].ToString().Trim();
        if (name.Length < 2) return Fail("profile_name_too_short");
        if (name.Length > 20) return Fail("profile_name_too_long");
        store.Update(() => me.Name = name);
        return Ok(json.Profile(me));
    }

    private async Task<IResult> SetForcedOffline(HttpContext ctx)
    {
        var me = CurrentSession(ctx).Profile;
        var enabled = ParseBool((await ReadForm(ctx))["forcedOfflineStatus"].ToString());
        store.Update(() => me.ForcedOffline = enabled);
        return Ok(new { forcedOfflineStatus = enabled });
    }

    private async Task<IResult> SetBlockIncomingFriendRequests(HttpContext ctx)
    {
        var me = CurrentSession(ctx).Profile;
        var enabled = ParseBool((await ReadForm(ctx))["blockIncomingFriendRequests"].ToString());
        store.Update(() => me.BlockIncomingFriendRequests = enabled);
        return Ok(new { blockIncomingFriendRequests = enabled });
    }

    private async Task<IResult> GenerateHandleSuggestions(HttpContext ctx)
    {
        var me = CurrentSession(ctx).Profile;
        var form = await ReadForm(ctx);
        var seed = form["handle"].ToString();
        if (string.IsNullOrWhiteSpace(seed)) seed = form["name"].ToString();
        if (string.IsNullOrWhiteSpace(seed)) seed = me.Name;

        var suggestions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var baseHandle = Regex.Replace(seed, "[^a-zA-Z0-9]", "");
        if (baseHandle.Length >= 3)
            suggestions.Add(FitHandle(baseHandle[..Math.Min(baseHandle.Length, 12)] + Random.Shared.Next(1, 1000)));

        for (var guard = 0; suggestions.Count < 6 && guard < 200; guard++)
        {
            var word = HandleAdjectives[Random.Shared.Next(HandleAdjectives.Length)]
                       + HandleNouns[Random.Shared.Next(HandleNouns.Length)];
            var candidate = FitHandle(word + Random.Shared.Next(1, 1000));
            if (store.FindByHandle(candidate) == null) suggestions.Add(candidate);
        }

        return Ok(new { suggestions = suggestions.ToArray() });
    }

    private static string FitHandle(string handle)
    {
        if (handle.Length > 18) handle = handle[..18];
        return handle.Length < 5 ? handle.PadRight(5, '0') : handle;
    }

    private async Task<IResult> ProfilesList(HttpContext ctx)
    {
        var me = CurrentSession(ctx).Profile;
        var form = await ReadForm(ctx);
        var profiles = new List<Profile>();

        foreach (var handle in ParseList(form["handles"].ToString()))
            if (store.FindByHandle(handle) is { } byHandle) profiles.Add(byHandle);

        foreach (var appAccountId in ParseList(form["appAccountIds"].ToString()))
            if (store.FindByGameAccount(TokenService.ParseGameAccountId(appAccountId)) is { } byAccount) profiles.Add(byAccount);

        var result = profiles.DistinctBy(p => p.Scid)
            .Select(p => json.PublicProfileSummary(p, store.RelationshipBetween(me.Scid, p.Scid)))
            .ToArray();
        return Ok(new { profiles = result });
    }

    private async Task<IResult> FriendsList(HttpContext ctx)
    {
        var me = CurrentSession(ctx).Profile;
        var targetScid = (await ReadForm(ctx))["scid"].ToString();

        if (targetScid.Length > 0 && targetScid != me.Scid)
        {
            var roster = store.RelationsOf(targetScid)
                .Where(r => r.Status == Relationship.Friend && r.Other.Scid != me.Scid)
                .Select(r => json.Friend(r.Other, store.RelationshipBetween(me.Scid, r.Other.Scid), r.Timestamp))
                .ToArray();
            return Ok(new
            {
                friends = roster,
                outgoingRequests = Array.Empty<object>(),
                incomingRequests = Array.Empty<object>(),
                friendRequests = Array.Empty<object>(),
                incomingFriendRequestsCount = 0
            });
        }

        var relations = store.RelationsOf(me.Scid);
        object[] Select(string status) => relations.Where(r => r.Status == status)
            .Select(r => json.Friend(r.Other, r.Status, r.Timestamp)).ToArray();

        var friends = Select(Relationship.Friend);
        var incoming = Select(Relationship.RequestReceived);
        var outgoing = Select(Relationship.RequestSent);
        return Ok(new
        {
            friends = friends.Concat(incoming).Concat(outgoing).ToArray(),
            outgoingRequests = outgoing,
            incomingRequests = incoming,
            friendRequests = incoming.Concat(outgoing).ToArray(),
            incomingFriendRequestsCount = incoming.Length
        });
    }

    private async Task<IResult> FriendsCreateRequest(HttpContext ctx)
    {
        var me = CurrentSession(ctx).Profile;
        var results = new Dictionary<string, object>();
        foreach (var scid in ParseList((await ReadForm(ctx))["scids"].ToString()))
        {
            var other = store.FindByScid(scid);
            var error = other == null ? "binding_not_found" : store.SendFriendRequest(me, other);
            if (error != null)
                log.LogInformation("Friend request {From} -> {To} not created: {Error}", me.Scid, scid, error);
            results[scid] = new { ok = true };
        }
        return Ok(new { scids = results, appAccounts = new { } });
    }

    private async Task<IResult> FriendsAcceptRequest(HttpContext ctx)
    {
        var me = CurrentSession(ctx).Profile;
        foreach (var scid in ParseList((await ReadForm(ctx))["scids"].ToString()))
            store.AcceptFriendRequest(me.Scid, scid);
        return Ok(new { });
    }

    private async Task<IResult> FriendsDeleteRelation(HttpContext ctx)
    {
        var me = CurrentSession(ctx).Profile;
        var form = await ReadForm(ctx);
        var scids = ParseList(form["scids"].ToString());
        var single = form["scid"].ToString().Trim();
        if (single.Length > 0) scids.Add(single);
        if (scids.Count == 0) return Fail("missing_required_data", StatusCodes.Status400BadRequest);

        foreach (var scid in scids.Where(s => s != me.Scid))
            store.RemoveRelation(me.Scid, scid);
        return Ok(new { });
    }
}
