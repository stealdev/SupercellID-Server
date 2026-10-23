using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using ScidServer.Configuration;
using ScidServer.Models;
using ScidServer.Realtime;
using ScidServer.Security;
using ScidServer.State;

namespace ScidServer.Api;

public sealed partial class ScidApi(
    MemoryStore store,
    TokenService tokens,
    PinService pins,
    PushHub hub,
    ScidJson json,
    ServerOptions options,
    IHttpClientFactory httpClients,
    ILogger<ScidApi> log)
{
    private const string SessionItemKey = "scid.session";

    private sealed record Session(Identity Identity, Profile Profile);

    public void Map(WebApplication app)
    {
        void Open(string path, Func<HttpContext, Task<IResult>> handler) => app.MapPost(path, handler);
        void Authed(string path, Func<HttpContext, Task<IResult>> handler) =>
            app.MapPost(path, handler).AddEndpointFilter(RequireSession);

        app.MapGet("/.well-known/jwks.json", () => Results.Json(tokens.Jwks()));

        Authed("/api/social/v3/session.init", SessionInit);
        Authed("/api/social/v3/profile.get", ProfileGet);
        Authed("/api/social/v3/profile.setAvatar", SetAvatar);
        Authed("/api/social/v3/profile.setHandle", SetHandle);
        Authed("/api/social/v3/profile.setName", SetName);
        Authed("/api/social/v3/profile.setForcedOffline", SetForcedOffline);
        Authed("/api/social/v3/profile.setBlockIncomingFriendRequests", SetBlockIncomingFriendRequests);
        Authed("/api/social/v3/profile.generateHandleSuggestions", GenerateHandleSuggestions);
        Authed("/api/social/v3/profiles.list", ProfilesList);
        Authed("/api/social/v3/friends.list", FriendsList);
        Authed("/api/social/v3/friends.createRequest", FriendsCreateRequest);
        Authed("/api/social/v3/friends.acceptRequest", FriendsAcceptRequest);
        Authed("/api/social/v3/friends.deleteRequest", FriendsDeleteRelation);
        Authed("/api/social/v3/friends.delete", FriendsDeleteRelation);
        Authed("/api/social/v3/friends.remove", FriendsDeleteRelation);

        Authed("/api/inventory/v3/items.list", ItemsList);
        Authed("/api/inventory/v3/items.claim", ItemsClaim);
        Authed("/api/inventory/v3/items.claimComplete", ItemsClaimComplete);
        Authed("/api/inventory/v3/items.donate", ItemsDonate);
        Authed("/api/inventory/v3/items.accept", ctx => ItemsResolveDonation(ctx, accept: true));
        Authed("/api/inventory/v3/items.reject", ctx => ItemsResolveDonation(ctx, accept: false));
        Authed("/api/event/v1/notification.ack", NotificationAck);

        foreach (var path in new[] { "/api/security/sessionToken", "/api/security/v1/sessionToken", "/api/security/v2/sessionToken" })
            app.MapMethods(path, ["GET", "POST"], (Func<HttpContext, Task<IResult>>)SessionToken)
                .AddEndpointFilter(RequireSession);

        Authed("/api/ingame/account/getConfig", GetConfig);
        Authed("/api/ingame/account/settings.get", SettingsGet);
        Authed("/api/ingame/account/settings.set", SettingsSet);
        Open("/api/ingame/account/create", Create);
        Open("/api/ingame/account/create.confirm", CreateConfirm);
        Open("/api/ingame/account/login", Login);
        Open("/api/ingame/account/login.validate", LoginValidate);
        Open("/api/ingame/account/login.confirm", LoginConfirm);

        Open("/api/account/v2/accounts.batchGetByAccountTokens", BatchGetByAccountTokens);
        Open("/api/account/v2/pinAuthentication.start", PinAuthenticationStart);
        Open("/api/account/v2/pinAuthentication.complete", PinAuthenticationComplete);
        Open("/api/account/v2/recoveryCodes.generate", RecoveryCodesGenerate);
        foreach (var step in new[] { "recoveryIdentifier.link", "accountProtection.modify", "accountProtection.disable", "identifier.link", "identifier.change" })
        {
            Open($"/api/account/v2/account/{step}", AccountProtectionScopeStep);
            Open($"/api/account/{step}", AccountProtectionScopeStep);
        }

        app.MapGet("/laser/{accountPath}", (string accountPath) => LaserProfile(accountPath));
        app.MapGet("/cdn/assets/{**assetPath}", CdnProxy);
        app.Map("/ws", hub.Accept);

        if (options.EnableDevEndpoints)
            MapDevEndpoints(app);
    }

    private async ValueTask<object?> RequireSession(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var ctx = invocation.HttpContext;
        var raw = Bearer(ctx);
        if (raw.Length == 0) return Fail("login_expired", StatusCodes.Status401Unauthorized);

        var identity = tokens.ReadIdentity(raw);
        if (identity == null || identity.GameAccountId <= 0 || identity.Scid.Length == 0)
            return Fail("token_revoked", StatusCodes.Status401Unauthorized);

        var profile = store.FindByGameAccount(identity.GameAccountId);
        if (profile == null || profile.Scid != identity.Scid || profile.SessionVersion > identity.Version)
        {
            log.LogInformation("Rejected {Path}: token for {Scid} / {Pid} is not current",
                ctx.Request.Path, identity.Scid, identity.Pid);
            return Fail("token_revoked", StatusCodes.Status401Unauthorized);
        }

        ctx.Items[SessionItemKey] = new Session(identity, profile);
        return await next(invocation);
    }

    private static Session CurrentSession(HttpContext ctx) => (Session)ctx.Items[SessionItemKey]!;

    private static string Bearer(HttpContext ctx)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..].Replace("\0", "").Trim() : "";
    }

    private Identity? OptionalIdentity(HttpContext ctx)
    {
        var raw = Bearer(ctx);
        return raw.Length == 0 ? null : tokens.ReadIdentity(raw);
    }

    private string IssueAccountToken(Profile profile)
    {
        var token = tokens.CreateAccountToken(profile);
        store.Update(() => profile.AccountToken = token);
        return token;
    }

    private static async Task<IFormCollection> ReadForm(HttpContext ctx)
    {
        if (ctx.Request.HasFormContentType)
            return await ctx.Request.ReadFormAsync();
        return new FormCollection(QueryHelpers.ParseQuery(await ReadBody(ctx)));
    }

    private static async Task<string> ReadBody(HttpContext ctx)
    {
        ctx.Request.EnableBuffering();
        ctx.Request.Body.Position = 0;
        using var reader = new StreamReader(ctx.Request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        ctx.Request.Body.Position = 0;
        return body;
    }

    private static List<string> ParseList(string raw)
    {
        raw = raw.Trim();
        if (raw.Length == 0) return [];
        if (raw.StartsWith('['))
        {
            try
            {
                return (JsonSerializer.Deserialize<List<string>>(raw) ?? [])
                    .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
            }
            catch (JsonException)
            {
                return [];
            }
        }
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static bool ParseBool(string raw) => bool.TryParse(raw, out var value) && value;

    private static bool IsValidEmail(string email) =>
        System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$");

    private static IResult Ok(object data) => Results.Json(new { ok = true, data });

    private static IResult Fail(string error, int statusCode = StatusCodes.Status200OK) =>
        Results.Json(new { ok = false, error }, statusCode: statusCode);
}
