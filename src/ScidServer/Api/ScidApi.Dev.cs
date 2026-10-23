using ScidServer.State;

namespace ScidServer.Api;

public sealed partial class ScidApi
{
    public sealed record TokenRequest(long GameAccountId, string? Name);

    public sealed record ItemRequest(
        long GameAccountId,
        Dictionary<string, long>? Contents,
        string? Title,
        string? ImageUrl,
        string? BackgroundImageUrl);

    private void MapDevEndpoints(WebApplication app)
    {
        app.MapPost("/dev/token", (TokenRequest request) =>
        {
            if (request.GameAccountId <= 0)
                return Results.BadRequest(new { ok = false, error = "gameAccountId must be positive" });

            var name = string.IsNullOrWhiteSpace(request.Name) ? $"Player{request.GameAccountId}" : request.Name.Trim();
            var profile = store.EnsureGuest(request.GameAccountId,
                MemoryStore.ScidFor($"guest:{request.GameAccountId}"), name);
            var token = IssueAccountToken(profile);
            return Results.Json(new { ok = true, token, scid = profile.Scid, pid = profile.Pid });
        });

        app.MapPost("/dev/items", async (ItemRequest request) =>
        {
            var owner = store.FindByGameAccount(request.GameAccountId);
            if (owner == null)
                return Results.NotFound(new { ok = false, error = $"no profile for game account {request.GameAccountId}" });
            if (request.Contents is not { Count: > 0 })
                return Results.BadRequest(new { ok = false, error = "contents must not be empty" });

            var item = store.AddItem(owner.GameAccountId, request.Contents, request.Title,
                request.ImageUrl, request.BackgroundImageUrl);
            var pushed = await hub.PushPending(owner);
            return Results.Json(new { ok = true, item = json.Item(item, owner.Scid), pushed });
        });

        app.MapPost("/dev/accounts/{gameAccountId:long}/revoke", async (long gameAccountId) =>
        {
            var profile = store.FindByGameAccount(gameAccountId);
            if (profile == null)
                return Results.NotFound(new { ok = false, error = $"no profile for game account {gameAccountId}" });

            var version = store.RevokeSessions(profile);
            await hub.Disconnect(profile.Scid, "session revoked");
            return Results.Json(new { ok = true, sessionVersion = version });
        });

        app.MapGet("/dev/state", () => Results.Json(store.Snapshot()));
    }
}
