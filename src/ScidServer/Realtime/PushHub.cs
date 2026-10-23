using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using ScidServer.Models;
using ScidServer.Proto;
using ScidServer.Security;
using ScidServer.State;

namespace ScidServer.Realtime;

public sealed class PushHub(MemoryStore store, TokenService tokens, ILogger<PushHub> log)
{
    private const string DefaultImage = "https://store.supercell.com/images/brawlstars/empty-background.png";
    private const string DefaultLogo = "https://store.supercell.com/images/brawlstars/logo.png";

    private sealed class Client(WebSocket socket)
    {
        public WebSocket Socket { get; } = socket;

        public ConcurrentDictionary<string, byte> Pushed { get; } = new();
        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }

    private readonly ConcurrentDictionary<string, Client> _clients = new();

    public async Task Accept(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var raw = ctx.Request.Headers.Authorization.ToString();
        raw = raw.StartsWith("Bearer ", StringComparison.Ordinal) ? raw[7..].Trim() : ctx.Request.Query["token"].ToString();
        var identity = ReadSocketToken(raw);
        if (identity == null && raw.Length > 0)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        var buffer = new byte[4096];
        Client? client = null;
        var scid = "";

        try
        {
            if (identity == null)
            {
                var first = await socket.ReceiveAsync(buffer, ctx.RequestAborted);
                if (first.MessageType != WebSocketMessageType.Text) return;
                identity = ReadSocketToken(TokenFromMessage(Encoding.UTF8.GetString(buffer, 0, first.Count)));
            }

            client = identity == null ? null : Register(identity, socket);
            if (client == null)
            {
                await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "invalid token", CancellationToken.None);
                return;
            }
            scid = identity!.Scid;
            log.LogInformation("WebSocket connected: {Scid}", scid);

            while (socket.State == WebSocketState.Open)
            {
                var message = await socket.ReceiveAsync(buffer, ctx.RequestAborted);
                if (message.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
        }
        finally
        {
            if (client != null)
                _clients.TryRemove(new KeyValuePair<string, Client>(scid, client));
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
                catch (WebSocketException) { }
            }
            if (scid.Length > 0) log.LogInformation("WebSocket disconnected: {Scid}", scid);
        }
    }

    public async Task<bool> PushPending(Profile profile)
    {
        if (!_clients.TryGetValue(profile.Scid, out var client) || client.Socket.State != WebSocketState.Open)
            return false;

        var pushed = 0;
        var priority = 100_000;
        foreach (var item in store.PendingItems(profile.GameAccountId))
        {
            if (!client.Pushed.TryAdd(item.Id, 0)) continue;

            var claimToken = tokens.CreateClaimToken(profile.Scid, profile.Pid, item);
            var bytes = BuildPurchasesReceived(item, claimToken, priority--);
            await client.SendLock.WaitAsync();
            try
            {
                await client.Socket.SendAsync(bytes, WebSocketMessageType.Binary, true, CancellationToken.None);
                pushed++;
            }
            catch (WebSocketException ex)
            {
                client.Pushed.TryRemove(item.Id, out _);
                log.LogWarning("WebSocket push to {Scid} failed: {Error}", profile.Scid, ex.Message);
                break;
            }
            finally
            {
                client.SendLock.Release();
            }
        }

        if (pushed > 0) log.LogInformation("Pushed {Count} item card(s) to {Scid}", pushed, profile.Scid);
        return pushed > 0;
    }

    public async Task Disconnect(string scid, string reason)
    {
        if (!_clients.TryRemove(scid, out var client)) return;
        try { await client.Socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, reason, CancellationToken.None); }
        catch (WebSocketException) { client.Socket.Abort(); }
    }

    private Client? Register(Identity identity, WebSocket socket)
    {
        var profile = store.FindByScid(identity.Scid);
        if (profile == null || profile.GameAccountId != identity.GameAccountId || profile.SessionVersion > identity.Version)
        {
            log.LogInformation("WebSocket rejected: token for {Scid} / {Pid} is not current", identity.Scid, identity.Pid);
            return null;
        }

        var client = new Client(socket);
        Client? previous = null;
        _clients.AddOrUpdate(identity.Scid, client, (_, old) =>
        {
            previous = old;
            return client;
        });
        if (previous != null && previous.Socket.State == WebSocketState.Open)
            _ = previous.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "replaced", CancellationToken.None);
        return client;
    }

    private Identity? ReadSocketToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        token = Uri.UnescapeDataString(token).Replace("Bearer ", "").Trim();
        return tokens.ReadIdentity(token)
               ?? (TokenService.FindEmbeddedJwt(token) is { } embedded ? tokens.ReadIdentity(embedded) : null);
    }

    private static string TokenFromMessage(string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("token", out var token)) return token.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("authorization", out var auth)) return auth.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
        }
        return message;
    }

    private static byte[] BuildPurchasesReceived(Item item, string claimToken, int priority)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var shopItem = new IdShopItem
        {
            Id = item.Id,
            ProductId = item.ProductId,
            Type = item.IsSeasonPass ? IdShopItemType.SeasonPass : IdShopItemType.Unknown,
            Tier = "DEFAULT",
            Status = new IdShopItemStatus { Available = new IdShopItemStatusAvailable() }
        };
        shopItem.Applications.Add(["laser", "laser-prod"]);
        foreach (var (key, value) in item.Contents)
            shopItem.Contents.Add(new IdShopItemContentsEntry { Key = key, Value = value });

        var hasImage = !string.IsNullOrEmpty(item.ImageUrl);
        var hasBackground = !string.IsNullOrEmpty(item.BackgroundImageUrl);
        var product = new IdShopProduct
        {
            Application = "laser",
            ImageUrl = hasImage && hasBackground ? item.ImageUrl : DefaultImage,
            BackgroundImageUrl = hasBackground ? item.BackgroundImageUrl : hasImage ? item.ImageUrl : DefaultImage,
            LogoImageUrl = DefaultLogo,
            Priority = priority,
            Type = item.ProductId
        };
        product.Title.Add("en", item.Title);
        product.Title.Add("ru", item.Title);
        product.Tags.Add("STORE_FREE_OFFER_CLAIM");

        var purchases = new NotificationPurchasesReceived
        {
            Product = product,
            ProductQuantity = 1,
            Token = claimToken,
            TemplateType = 1,
            GroupId = "scid-server-items",
            Origin = "STORE_FREE_OFFER_CLAIM"
        };
        purchases.ShopItems.Add(shopItem);

        return new ServerMessage
        {
            Notification = new NotificationMessage
            {
                IssuedAt = new ScidTimestamp { Seconds = now },
                NotBefore = new ScidTimestamp { Seconds = now },
                PurchasesReceived = purchases
            }
        }.ToByteArray();
    }
}
