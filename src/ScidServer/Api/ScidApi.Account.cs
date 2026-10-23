using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ScidServer.Models;
using ScidServer.Security;

namespace ScidServer.Api;

public sealed partial class ScidApi
{
    private static readonly TimeSpan LoginConfirmReplayWindow = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, (object Response, DateTimeOffset Expires)> _loginConfirmReplies =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, (string Scope, string Email)> _pinAuthStates = new();

    private Task<IResult> SessionToken(HttpContext ctx)
    {
        var me = CurrentSession(ctx).Profile;
        return Task.FromResult(Results.Json(new { ok = true, token = tokens.CreateSessionToken(me) }));
    }

    private Task<IResult> GetConfig(HttpContext ctx)
    {
        var me = CurrentSession(ctx).Profile;
        return Task.FromResult(Ok(new
        {
            language = "en",
            game = "laser",
            playerRegion = "",
            gameAccountNickname = "",
            gameAccountToken = me.AccountToken,
            gameHelpLink = "",
            reCaptchaKey = "",
            environment = "prod",
            gameSeasonName = "",
            versionSuffix = ""
        }));
    }

    private Task<IResult> SettingsGet(HttpContext ctx)
    {
        var me = CurrentSession(ctx).Profile;
        return Task.FromResult(Ok(new
        {
            created = me.CreatedAt.ToString("o"),
            lang = "en",
            email_marketing_permission = me.MarketingPermission,
            email_marketing_timestamp = "0",
            marketing_scope_consents = Array.Empty<object>(),
            email_retargeting_permission = false
        }));
    }

    private async Task<IResult> SettingsSet(HttpContext ctx)
    {
        var me = CurrentSession(ctx).Profile;
        var accept = ParseBool((await ReadForm(ctx))["accept_marketing"].ToString());
        store.Update(() => me.MarketingPermission = accept);
        return Ok(new { accept_marketing = accept });
    }

    private async Task<IResult> Create(HttpContext ctx)
    {
        var email = (await ReadForm(ctx))["email"].ToString().Trim();
        if (!IsValidEmail(email)) return Fail("invalid_email_address");

        var existing = store.FindByEmail(email);
        if (existing != null && !IsSameOrUnbound(existing, OptionalIdentity(ctx)))
            return Fail("generic");

        pins.Issue(email, email);
        return Ok(new { email, state = "pending" });
    }

    private async Task<IResult> CreateConfirm(HttpContext ctx)
    {
        var form = await ReadForm(ctx);
        var email = form["email"].ToString().Trim();
        if (!pins.Consume(email, form["pin"].ToString())) return Fail("incorrect_pin");

        var bearer = OptionalIdentity(ctx);
        var existing = store.FindByEmail(email);
        if (existing != null)
            return IsSameOrUnbound(existing, bearer) ? ConfirmLogin(bearer, email) : Fail("generic");

        var gameAccountId = bearer?.GameAccountId ?? 0;
        if (store.FindByGameAccount(gameAccountId) is { Email: not null })
            return Fail("rebinding_same_game");

        var profile = store.BindEmail(email, gameAccountId, PlayerNameFor(bearer, email));
        var token = IssueAccountToken(profile);
        log.LogInformation("Registered {Email} as {Scid} on game account {GameAccountId}", email, profile.Scid, gameAccountId);
        return Ok(new { scidToken = token, bindToken = token, refreshToken = NewRefreshToken() });
    }

    private async Task<IResult> Login(HttpContext ctx)
    {
        var email = (await ReadForm(ctx))["email"].ToString().Trim();
        if (email.Length == 0) return Fail("missing_required_data");
        if (!IsValidEmail(email)) return Fail("invalid_email_address");

        pins.Issue(email, email);
        return Ok(new { email, state = "pending" });
    }

    private async Task<IResult> LoginValidate(HttpContext ctx)
    {
        var form = await ReadForm(ctx);
        var email = form["email"].ToString().Trim();
        if (!pins.Peek(email, form["pin"].ToString())) return Fail("incorrect_pin");
        var isBound = store.FindByEmail(email) is { GameAccountId: > 0 };
        return Ok(new { isBound });
    }

    private async Task<IResult> LoginConfirm(HttpContext ctx)
    {
        var form = await ReadForm(ctx);
        var email = form["email"].ToString().Trim();
        if (email.Length == 0) return Fail("missing_required_data");

        if (_loginConfirmReplies.TryGetValue(email, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
            return Results.Json(cached.Response);

        if (!pins.Consume(email, form["pin"].ToString())) return Fail("incorrect_pin");
        return ConfirmLogin(OptionalIdentity(ctx), email);
    }

    private IResult ConfirmLogin(Identity? bearer, string email)
    {
        var gameAccountId = bearer?.GameAccountId ?? 0;
        var byEmail = store.FindByEmail(email);

        if (byEmail == null)
        {
            if (gameAccountId <= 0) return Fail("binding_not_found");
            if (store.FindByGameAccount(gameAccountId) is { Email: not null })
                return Fail("rebinding_same_game");
        }

        var targetGameAccountId = byEmail is { GameAccountId: > 0 } ? byEmail.GameAccountId : gameAccountId;

        if (gameAccountId > 0 && gameAccountId != targetGameAccountId
            && store.FindByGameAccount(gameAccountId) is { Email: not null } current
            && !string.Equals(current.Email, email, StringComparison.OrdinalIgnoreCase))
            store.Unlink(current);

        var profile = store.BindEmail(email, targetGameAccountId, PlayerNameFor(bearer, email));
        var token = IssueAccountToken(profile);
        var switched = targetGameAccountId != gameAccountId;
        log.LogInformation("Login {Email} -> {Scid} on game account {GameAccountId} (switch: {Switched})",
            email, profile.Scid, targetGameAccountId, switched);

        var response = new
        {
            ok = true,
            data = new
            {
                scidToken = token,
                bindToken = token,
                refreshToken = NewRefreshToken(),
                switchAccount = switched,
                requiresReconnect = switched
            }
        };
        _loginConfirmReplies[email] = (response, DateTimeOffset.UtcNow + LoginConfirmReplayWindow);
        return Results.Json(response);
    }

    private static bool IsSameOrUnbound(Profile profile, Identity? bearer) =>
        profile.GameAccountId <= 0 || profile.GameAccountId == (bearer?.GameAccountId ?? 0);

    private static string PlayerNameFor(Identity? bearer, string email) =>
        string.IsNullOrWhiteSpace(bearer?.Name) ? email.Split('@')[0] : bearer.Name;

    private static string NewRefreshToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private async Task<IResult> BatchGetByAccountTokens(HttpContext ctx)
    {
        var raw = await ReadBody(ctx);
        var tokenList = new List<string>();
        var form = await ReadForm(ctx);
        foreach (var key in form.Keys.Where(k => k.StartsWith("accountToken", StringComparison.Ordinal)))
            foreach (var value in form[key])
                tokenList.AddRange(ParseList(value ?? ""));
        if (tokenList.Count == 0 && raw.TrimStart().StartsWith('['))
            tokenList.AddRange(ParseList(raw));

        var result = new Dictionary<string, object>();
        foreach (var token in tokenList.Select(t => t.Replace("\0", "").Trim()).Distinct())
        {
            var profile = store.FindByAccountToken(token);
            if (profile == null && tokens.ReadIdentity(token) is { GameAccountId: > 0 } identity)
            {
                profile = store.EnsureGuest(identity.GameAccountId, identity.Scid,
                    identity.Name.Length > 0 ? identity.Name : "Player");
                if (profile.AccountToken.Length == 0) store.Update(() => profile.AccountToken = token);
            }
            if (profile == null) continue;

            var alias = string.IsNullOrEmpty(profile.Handle) ? profile.Name : profile.Handle;
            var progress = new[] { profile.Trophies.ToString(), profile.Trophies.ToString() };
            result[token] = new
            {
                ok = true,
                data = new
                {
                    scid = profile.Scid,
                    identifier = new { type = "EMAIL", value = profile.Email ?? "" },
                    connectedSystem = new
                    {
                        application = "laser-prod",
                        applicationAccountId = profile.Pid,
                        applicationAccountAlias = alias,
                        username = profile.PlayerName,
                        progress,
                        score = profile.Trophies,
                        seasonPassActive = false
                    },
                    applications = new[]
                    {
                        new
                        {
                            application = "laser-prod",
                            account = new { id = profile.Pid, alias, name = profile.PlayerName, progress, score = profile.Trophies }
                        }
                    },
                    name = profile.Name,
                    avatarImage = profile.Avatar,
                    handle = profile.Handle
                }
            };
        }
        return Ok(result);
    }

    private async Task<IResult> PinAuthenticationStart(HttpContext ctx)
    {
        var form = await ReadForm(ctx);
        var email = form["identifier"].ToString().Trim();
        if (email.Length == 0) email = OptionalIdentity(ctx)?.Email ?? "";
        if (!IsValidEmail(email)) return Fail("invalid_email_address");

        var state = Guid.NewGuid().ToString("N");
        _pinAuthStates[state] = (form["scope"].ToString(), email);
        pins.Issue($"pin:{state}", email);
        return Ok(new { state });
    }

    private async Task<IResult> PinAuthenticationComplete(HttpContext ctx)
    {
        var form = await ReadForm(ctx);
        var state = form["state"].ToString();
        if (!_pinAuthStates.TryGetValue(state, out var pending)) return Fail("login_expired");
        if (!pins.Consume($"pin:{state}", form["pin"].ToString())) return Fail("incorrect_pin");

        _pinAuthStates.TryRemove(state, out _);
        return Ok(new { authentication = new { scope = pending.Scope, token = Guid.NewGuid().ToString("N") } });
    }

    private async Task<IResult> AccountProtectionScopeStep(HttpContext ctx)
    {
        _ = await ReadForm(ctx);
        return Ok(new { token = Guid.NewGuid().ToString("N") });
    }

    private async Task<IResult> RecoveryCodesGenerate(HttpContext ctx)
    {
        _ = await ReadForm(ctx);
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        string Code() => new string(RandomNumberGenerator.GetItems<char>(alphabet, 8)).Insert(4, "-");
        var codes = Enumerable.Range(0, 10).Select(_ => Code()).ToArray();
        return Ok(new { codes, token = Guid.NewGuid().ToString("N") });
    }

    private IResult LaserProfile(string accountPath)
    {
        var parts = accountPath.Split('-');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var high) || !int.TryParse(parts[1], out var low))
            return Fail("not_found", StatusCodes.Status400BadRequest);

        var gameAccountId = ((long)high << 32) | (uint)low;
        if (store.FindByGameAccount(gameAccountId) is { } profile)
        {
            return Results.Json(new
            {
                account_id = $"profile:{gameAccountId}",
                handle = profile.PlayerName,
                image_ref = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(profile.Scid))[..16]).ToString(),
                game_account_id = accountPath,
                request_friend_deeplink = $"{options.UniversalLinkBase}/?supercell_id&p={profile.Scid}",
                allow_update = true
            });
        }

        return Results.Json(new
        {
            account_id = $"anonymous:laser:{accountPath}",
            handle = $"Player{gameAccountId}",
            image_ref = "player_icon_maisie_bizarre",
            game_account_id = accountPath,
            request_friend_deeplink = "",
            allow_update = false
        });
    }

    private async Task<IResult> CdnProxy(string assetPath, HttpContext ctx)
    {
        if (string.IsNullOrEmpty(options.CdnUpstream)) return Results.NotFound();

        var url = options.CdnUpstream.TrimEnd('/') + "/" + assetPath;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var userAgent = ctx.Request.Headers.UserAgent.ToString();
        request.Headers.UserAgent.ParseAdd(userAgent.Length > 0 ? userAgent : "Mozilla/5.0");

        try
        {
            var response = await httpClients.CreateClient("cdn").SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("CDN proxy got {Status} for {Asset}", (int)response.StatusCode, assetPath);
                return Results.StatusCode((int)response.StatusCode);
            }

            ctx.Response.Headers.CacheControl = "public, max-age=86400";
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            return Results.Stream(await response.Content.ReadAsStreamAsync(), contentType);
        }
        catch (HttpRequestException ex)
        {
            log.LogWarning("CDN proxy failed for {Asset}: {Error}", assetPath, ex.Message);
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
    }
}
