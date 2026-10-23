using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScidServer.Models;

namespace ScidServer.Security;

public sealed class TokenService
{
    private const string Ns = "https://id.supercell.com/";
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    private readonly ECDsa _key;

    public string KeyId { get; }

    private TokenService(ECDsa key)
    {
        _key = key;
        var q = key.ExportParameters(false).Q;
        KeyId = "scid-" + Convert.ToHexStringLower(SHA256.HashData([.. q.X!, .. q.Y!]))[..16];
    }

    public static TokenService LoadOrCreate(string path)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(path))
            key.ImportFromPem(File.ReadAllText(path));
        else
            File.WriteAllText(path, key.ExportECPrivateKeyPem());
        return new TokenService(key);
    }

    public static long ParseGameAccountId(string pid)
    {
        var parts = pid.Split('-');
        return parts.Length == 2 && long.TryParse(parts[1], out var id) ? id : 0;
    }

    public string CreateAccountToken(Profile profile)
    {
        var now = DateTimeOffset.UtcNow;
        var claims = BaseClaims(profile.Scid, profile.Pid, "account");
        claims["email"] = profile.Email ?? "";
        claims["ver"] = profile.SessionVersion;
        claims[Ns + "initialRefreshTokenIssuedAt"] = now.ToUnixTimeSeconds();
        claims[Ns + "guest"] = false;
        claims["pn"] = profile.PlayerName;
        return Sign(claims, now, now.AddDays(30));
    }

    public string CreateSessionToken(Profile profile)
    {
        var now = DateTimeOffset.UtcNow;
        var claims = BaseClaims(profile.Scid, profile.Pid, "session");
        claims["aud"] = "session";
        claims["ver"] = profile.SessionVersion;
        claims[Ns + "initialRefreshTokenIssuedAt"] = now.ToUnixTimeSeconds();
        return Sign(claims, now, now.AddDays(2));
    }

    public string CreateClaimToken(string scid, string pid, Item item)
    {
        var now = DateTimeOffset.UtcNow;
        var contents = new JsonObject();
        foreach (var (key, value) in item.Contents)
            contents[key] = value;

        var claims = BaseClaims(scid, pid, "claim_item");
        claims["iss"] = Ns + "api/inventory";
        claims["aud"] = "laser";
        claims[Ns + "itemId"] = item.Id;
        claims[Ns + "productId"] = item.ProductId;
        claims[Ns + "contents"] = contents;
        claims[Ns + "chronosEventId"] = 0;
        claims[Ns + "chronosVariantId"] = 0;
        claims[Ns + "chronosOfferIndex"] = 0;
        return Sign(claims, now, now.AddDays(30));
    }

    public Identity? ReadIdentity(string token)
    {
        if (Verify(token) is not { } root) return null;
        if (Claim(root, Ns + "type") is not ("account" or "session")) return null;

        var scid = Claim(root, "scid");
        if (scid.Length == 0) scid = Claim(root, "sub");
        var pid = Claim(root, "pid");
        int.TryParse(Claim(root, "ver"), out var version);
        return new Identity(scid, pid, ParseGameAccountId(pid), Claim(root, "email"), Claim(root, "pn"), version);
    }

    public ClaimTicket? ReadClaim(string token)
    {
        if (Verify(token) is not { } root || Claim(root, Ns + "type") != "claim_item") return null;
        return new ClaimTicket(Claim(root, "scid"), Claim(root, "pid"),
            Claim(root, Ns + "itemId"), Claim(root, Ns + "productId"));
    }

    public object Jwks()
    {
        var q = _key.ExportParameters(false).Q;
        return new
        {
            keys = new[]
            {
                new
                {
                    kty = "EC", use = "sig", crv = "P-256", kid = KeyId, alg = "ES256",
                    x = Base64Url.EncodeToString(q.X), y = Base64Url.EncodeToString(q.Y)
                }
            }
        };
    }

    public static string? FindEmbeddedJwt(string token)
    {
        try
        {
            var padded = token.Length % 4 == 0 ? token : token + new string('=', 4 - token.Length % 4);
            byte[] bytes;
            try { bytes = Convert.FromBase64String(padded); }
            catch (FormatException) { bytes = Convert.FromBase64String(padded.Replace('-', '+').Replace('_', '/')); }

            for (var i = 0; i < bytes.Length - 3; i++)
            {
                if (bytes[i] != 'e' || bytes[i + 1] != 'y' || bytes[i + 2] != 'J') continue;
                var sb = new StringBuilder();
                for (var j = i; j < bytes.Length; j++)
                {
                    var c = (char)bytes[j];
                    if (char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') sb.Append(c);
                    else break;
                }

                var parts = sb.ToString().Split('.');
                if (parts.Length != 3) continue;
                if (parts[2].Length > 86) parts[2] = parts[2][..86];
                return string.Join('.', parts);
            }
        }
        catch (FormatException)
        {
        }
        return null;
    }

    private static JsonObject BaseClaims(string scid, string pid, string type) => new()
    {
        ["iss"] = "id.supercell.com",
        ["sub"] = scid,
        [Ns + "type"] = type,
        [Ns + "app"] = "laser",
        [Ns + "appEnv"] = "prod",
        [Ns + "appAccountId"] = pid,
        ["scid"] = scid,
        ["game"] = "laser",
        ["env"] = "prod",
        ["pid"] = pid
    };

    private string Sign(JsonObject claims, DateTimeOffset issuedAt, DateTimeOffset expires)
    {
        claims["iat"] = issuedAt.ToUnixTimeSeconds();
        claims["exp"] = expires.ToUnixTimeSeconds();
        var header = new JsonObject { ["alg"] = "ES256", ["kid"] = KeyId, ["typ"] = "JWT" };
        var signingInput = Encode(header) + "." + Encode(claims);
        var signature = _key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);
        return signingInput + "." + Base64Url.EncodeToString(signature);
    }

    private JsonElement? Verify(string token)
    {
        var parts = token.Trim().Split('.');
        if (parts.Length != 3) return null;
        try
        {
            using (var header = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[0])))
            {
                if (!header.RootElement.TryGetProperty("alg", out var alg) || alg.GetString() != "ES256")
                    return null;
            }

            var signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            if (!_key.VerifyData(signed, Base64Url.DecodeFromChars(parts[2]), HashAlgorithmName.SHA256))
                return null;

            using var payload = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
            var root = payload.RootElement.Clone();
            if (!root.TryGetProperty("exp", out var exp) || !exp.TryGetInt64(out var expSeconds))
                return null;
            if (DateTimeOffset.UtcNow > DateTimeOffset.FromUnixTimeSeconds(expSeconds) + ClockSkew)
                return null;
            return root;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or CryptographicException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string Claim(JsonElement root, string name) =>
        !root.TryGetProperty(name, out var value) ? "" : value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            _ => value.GetRawText()
        };

    private static string Encode(JsonNode node) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(node.ToJsonString()));
}
