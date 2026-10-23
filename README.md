# SCID Server

Just a simple impl of supercell id for tests only

Not affiliated with or endorsed by Supercell.

## Run

Requires the .NET 9 SDK.

```bash
dotnet run --project src/ScidServer
```

The server listens on `5080` port. On first start it creates `scid-server-key.pem`, the
ES256 key that signs every token, in the working directory (`src/ScidServer` under `dotnet run`).
Keep the file to keep issued tokens valid across restarts.

To build a single executable that does not need .NET installed (pick your runtime identifier):

```bash
dotnet publish src/ScidServer -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true
```

## Layout

```text
ScidServer.sln
src/ScidServer/
  Api/                   HTTP endpoints (ScidApi.*.cs, one file per area) and the JSON builders
  Configuration/         ServerOptions, bound from the "Server" section of appsettings.json
  Infrastructure/        request logging
  Models/                Profile, Item, FriendRelation and product/relationship constants
  Protos/                protobuf schema of the /ws notification frames
  Realtime/              /ws connections and Items Delivered pushes
  Security/              ES256 tokens and email PINs
  State/                 in-memory store
```

## Configuration

Settings live in `src/ScidServer/appsettings.json` under `Server`, and can be overridden with environment variables
(`Server__FixedPin=111111`) or command-line arguments (`--Server:FixedPin=111111`).

| Setting | Default | Meaning |
|---|---|---|
| `KeyPath` | `scid-server-key.pem` | Signing key, created when missing |
| `FixedPin` | empty | When set, every PIN equals this value (handy for automated tests) |
| `UniversalLinkBase` | `https://link.example.com` | Base of profile links, QR codes and deeplinks |
| `CdnUpstream` | `https://cdn.id.supercell.com/assets/` | Target of the `/cdn/assets/*` proxy; empty disables it |
| `EnableDevEndpoints` | `true` | Exposes the `/dev/*` helpers below |
| `LogBodies` | `true` | Logs request and response bodies |

The listen address is the standard ASP.NET Core `Urls` setting (`--urls http://0.0.0.0:5080`).

## Connecting the client

The client calls Supercell ID over HTTPS on the Supercell ID hosts (`*.id.supercell.com`). To send
it to the SCID server you need both:

- find libapp.so or ios lib (i don't remeber lib name on ios sorry) and replace all url's with supercell.com or just use hosts file (you know what you do)
- TLS with a certificate the device trusts. The simplest setup is a TLS-terminating reverse proxy
  (Caddy, nginx) in front of the SCID server. Kestrel can also serve HTTPS itself through the standard
  `Kestrel:Endpoints` configuration.

The SCID server proxies `/cdn/assets/*` to `CdnUpstream`. If the machine running the SCID server also redirects
`cdn.id.supercell.com` to itself, point `CdnUpstream` at a direct address or leave it empty, or the
proxy will call itself.

## Tokens

The client authenticates every SCID call with an ES256 JWT that the game server hands it at login.
The SCID server only accepts tokens signed with its own key, so a game server under test has two options:

1. Ask the SCID server for a token. `POST /dev/token` creates a guest profile for the game account (or
   reuses the existing one) and returns its account token:

   ```bash
   curl -X POST http://localhost:5080/dev/token -H 'Content-Type: application/json' -d '{"gameAccountId": 1, "name": "Player"}'
   ```

2. Sign tokens itself with `scid-server-key.pem` (ES256, header `kid` from `/.well-known/jwks.json`)
   using the claim set below. The SCID server creates the guest profile on the first
   `accounts.batchGetByAccountTokens` call that carries the token; until then the other endpoints
   answer `token_revoked`.

| Claim | Value |
|---|---|
| `iss` | `id.supercell.com` |
| `sub`, `scid` | SCID, for example `78-<guid>` |
| `pid`, `https://id.supercell.com/appAccountId` | `0-<gameAccountId>` |
| `https://id.supercell.com/type` | `account` |
| `https://id.supercell.com/app` / `appEnv` | `laser` / `prod` |
| `game` / `env` | `laser` / `prod` |
| `email` | bound email, or empty |
| `pn` | player name |
| `ver` | session version (number, starts at 1) |
| `https://id.supercell.com/guest` | `false` |
| `iat`, `exp` | Unix seconds |

After a login or registration the SCID server returns a new account token in `scidToken`. When the email
belongs to a different game account, `switchAccount` is true and the token's `pid` is that other
account: the game server is expected to log the client into it.

Inventory claims carry a separate claim token (`type` = `claim_item`) whose
`https://id.supercell.com/contents` claim lists what to grant. The SCID server only marks the item as claimed;
a game server can verify the claim token against `/.well-known/jwks.json` and grant the contents.

## Dev endpoints

These do not exist in the real service. Disable them (`Server__EnableDevEndpoints=false`) on any
machine reachable by others: anyone who can call them can mint tokens for any account.

| Endpoint | Purpose |
|---|---|
| `POST /dev/token` `{"gameAccountId", "name"}` | Account token for a game account (creates a guest profile) |
| `POST /dev/items` `{"gameAccountId", "contents", "title", "imageUrl", "backgroundImageUrl"}` | Grants an inventory item and pushes its card to the player's open `/ws` connection |
| `POST /dev/accounts/{gameAccountId}/revoke` | Bumps the session version: old tokens get `token_revoked` and the `/ws` connection is closed |
| `GET /dev/state` | Dumps profiles, friend relations and items |

Grant 100 gems to game account 1:

```bash
curl -X POST http://localhost:5080/dev/items -H 'Content-Type: application/json' -d '{"gameAccountId": 1, "contents": {"Gems": 100}, "title": "Test gems"}'
```

`contents` keys are passed through to the client and the claim token as is. A `BrawlPass` or
`BrawlPassPlus` key turns the item into a Brawl Pass, which the receiver can activate or gift to a
friend.

## Behaviour notes

- A token is accepted only while it matches the profile currently bound to its game account and
  its `ver` is not older than the last revocation. Anything else gets HTTP 401 `token_revoked`.
- Registering an email that already belongs to the same game account works as a login. An email of
  another game account is refused on registration and switches accounts on login.
- Repeated `login.confirm` calls for the same email within 60 seconds get the first response back:
  the Android client sends this request several times in parallel.
- Friend requests that cross (both players request each other) become a friendship. A Brawl Pass can
  be gifted only to a friend, and only once.
- Each pending item is pushed at most once per `/ws` connection.

## Not implemented

- No email delivery, rate limiting or request-forgery-protection header checks.
- Recovery codes are generated but not stored; the account protection steps only acknowledge.
- Season pass status, trophies and progress in profiles are placeholders (0 / `DEFAULT`).
