using System.Security.Cryptography;
using System.Text;
using ScidServer.Models;

namespace ScidServer.State;

public sealed class MemoryStore
{
    private readonly Lock _gate = new();
    private readonly List<Profile> _profiles = [];
    private readonly List<Item> _items = [];
    private readonly Dictionary<(string, string), FriendRelation> _relations = [];

    public static string ScidFor(string seed) =>
        $"78-{new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(seed))[..16])}";

    public void Update(Action change)
    {
        lock (_gate) change();
    }

    public Profile? FindByGameAccount(long gameAccountId)
    {
        if (gameAccountId <= 0) return null;
        lock (_gate) return _profiles.FirstOrDefault(p => p.GameAccountId == gameAccountId);
    }

    public Profile? FindByScid(string scid)
    {
        if (string.IsNullOrEmpty(scid)) return null;
        lock (_gate) return _profiles.FirstOrDefault(p => p.Scid == scid);
    }

    public Profile? FindByEmail(string email)
    {
        if (string.IsNullOrEmpty(email)) return null;
        lock (_gate) return FindByEmailLocked(email);
    }

    public Profile? FindByHandle(string handle)
    {
        lock (_gate)
            return _profiles.FirstOrDefault(p => string.Equals(p.Handle, handle, StringComparison.OrdinalIgnoreCase));
    }

    public Profile? FindByAccountToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        lock (_gate) return _profiles.FirstOrDefault(p => p.AccountToken == token);
    }

    public Profile EnsureGuest(long gameAccountId, string scid, string playerName)
    {
        lock (_gate)
        {
            var existing = _profiles.FirstOrDefault(p => p.GameAccountId == gameAccountId);
            if (existing != null) return existing;

            if (string.IsNullOrEmpty(scid) || _profiles.Any(p => p.Scid == scid))
                scid = ScidFor($"guest:{gameAccountId}:{Guid.NewGuid()}");

            var profile = NewProfile(scid, gameAccountId, playerName);
            _profiles.Add(profile);
            return profile;
        }
    }

    public Profile BindEmail(string email, long gameAccountId, string playerName)
    {
        lock (_gate)
        {
            var byEmail = FindByEmailLocked(email);
            if (byEmail != null)
            {
                if (gameAccountId > 0 && byEmail.GameAccountId <= 0)
                {
                    _profiles.RemoveAll(p => p != byEmail && p.GameAccountId == gameAccountId && p.Email == null);
                    byEmail.GameAccountId = gameAccountId;
                }
                return byEmail;
            }

            var current = gameAccountId > 0 ? _profiles.FirstOrDefault(p => p.GameAccountId == gameAccountId) : null;
            if (current is { Email: null })
            {
                current.Email = email;
                return current;
            }
            if (current != null)
                current.GameAccountId = 0;

            var created = NewProfile(ScidFor($"scid:{email.ToLowerInvariant()}"), gameAccountId, playerName);
            created.Email = email;
            _profiles.Add(created);
            return created;
        }
    }

    public void Unlink(Profile profile)
    {
        lock (_gate) profile.GameAccountId = 0;
    }

    public bool TrySetHandle(Profile profile, string handle)
    {
        lock (_gate)
        {
            if (_profiles.Any(p => p != profile && string.Equals(p.Handle, handle, StringComparison.OrdinalIgnoreCase)))
                return false;
            profile.Handle = handle;
            return true;
        }
    }

    public int RevokeSessions(Profile profile)
    {
        lock (_gate) return ++profile.SessionVersion;
    }

    public string RelationshipBetween(string me, string other)
    {
        lock (_gate)
        {
            if (!_relations.TryGetValue(Key(me, other), out var relation)) return Relationship.Stranger;
            if (relation.Accepted) return Relationship.Friend;
            return relation.Requester == me ? Relationship.RequestSent : Relationship.RequestReceived;
        }
    }

    public bool AreFriends(string a, string b) => RelationshipBetween(a, b) == Relationship.Friend;

    public List<(Profile Other, string Status, long Timestamp)> RelationsOf(string scid)
    {
        lock (_gate)
        {
            var result = new List<(Profile, string, long)>();
            foreach (var relation in _relations.Values)
            {
                if (relation.Requester != scid && relation.Target != scid) continue;
                var otherScid = relation.Requester == scid ? relation.Target : relation.Requester;
                var other = _profiles.FirstOrDefault(p => p.Scid == otherScid);
                if (other == null) continue;

                var status = relation.Accepted ? Relationship.Friend
                    : relation.Requester == scid ? Relationship.RequestSent
                    : Relationship.RequestReceived;
                result.Add((other, status, relation.UpdatedAt));
            }
            return result;
        }
    }

    public string? SendFriendRequest(Profile me, Profile other)
    {
        if (me.Scid == other.Scid) return "cannot_friend_self";
        lock (_gate)
        {
            var key = Key(me.Scid, other.Scid);
            if (_relations.TryGetValue(key, out var relation))
            {
                if (relation.Accepted) return "friend_already_exists";
                if (relation.Requester == me.Scid) return null;
                relation.Accepted = true;
                relation.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                return null;
            }

            if (other.BlockIncomingFriendRequests) return "friend_request_blocked";
            _relations[key] = new FriendRelation { Requester = me.Scid, Target = other.Scid };
            return null;
        }
    }

    public bool AcceptFriendRequest(string me, string other)
    {
        lock (_gate)
        {
            if (!_relations.TryGetValue(Key(me, other), out var relation) || relation.Target != me) return false;
            relation.Accepted = true;
            relation.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return true;
        }
    }

    public bool RemoveRelation(string me, string other)
    {
        lock (_gate) return _relations.Remove(Key(me, other));
    }

    public Item AddItem(long gameAccountId, Dictionary<string, long> contents, string? title,
        string? imageUrl, string? backgroundImageUrl)
    {
        var item = new Item
        {
            Id = $"item_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}",
            OwnerGameAccountId = gameAccountId,
            Contents = new Dictionary<string, long>(contents),
            Title = string.IsNullOrWhiteSpace(title) ? "GIFT!!!" : title,
            ImageUrl = imageUrl,
            BackgroundImageUrl = backgroundImageUrl
        };
        lock (_gate) _items.Add(item);
        return item;
    }

    public Item[] PendingItems(long gameAccountId)
    {
        lock (_gate)
            return _items.Where(i => i.OwnerGameAccountId == gameAccountId && !i.Claimed).ToArray();
    }

    public bool ClaimItem(long gameAccountId, string itemId)
    {
        lock (_gate)
        {
            var item = _items.FirstOrDefault(i => i.Id == itemId && i.OwnerGameAccountId == gameAccountId);
            if (item == null) return false;
            item.Claimed = true;
            return true;
        }
    }

    public string? DonateItem(Profile sender, Profile receiver, Item item)
    {
        if (sender.GameAccountId == receiver.GameAccountId) return "cannot_donate_to_self";
        if (!AreFriends(sender.Scid, receiver.Scid)) return "receiver_not_friend";
        lock (_gate)
        {
            if (item.Claimed || item.OwnerGameAccountId != sender.GameAccountId) return "item_not_available";
            if (!item.IsSeasonPass) return "item_not_donatable";
            if (item.IsPendingDonation || item.WasDonated) return "donated_pass_cannot_be_donated_again";

            item.OwnerGameAccountId = receiver.GameAccountId;
            item.DonationSenderGameAccountId = sender.GameAccountId;
            item.DonationSenderScid = sender.Scid;
            return null;
        }
    }

    public string? ResolveDonation(Profile receiver, Item item, bool accept)
    {
        lock (_gate)
        {
            if (item.Claimed || item.OwnerGameAccountId != receiver.GameAccountId || !item.IsPendingDonation)
                return "donation_not_found";

            if (accept)
                item.WasDonated = true;
            else
                item.OwnerGameAccountId = item.DonationSenderGameAccountId;

            item.DonationSenderGameAccountId = 0;
            item.DonationSenderScid = "";
            return null;
        }
    }

    public object Snapshot()
    {
        lock (_gate)
        {
            return new
            {
                profiles = _profiles.Select(p => new
                {
                    p.Scid, p.GameAccountId, p.Email, p.Name, p.Handle, p.PlayerName, p.Avatar, p.Trophies,
                    p.ForcedOffline, p.BlockIncomingFriendRequests, p.MarketingPermission, p.SessionVersion, p.CreatedAt
                }).ToArray(),
                relations = _relations.Values.Select(r => new { r.Requester, r.Target, r.Accepted, r.UpdatedAt }).ToArray(),
                items = _items.Select(i => new
                {
                    i.Id, i.OwnerGameAccountId, i.ProductId, i.Contents, i.Title, i.Claimed,
                    i.DonationSenderGameAccountId, i.WasDonated, i.CreatedAt
                }).ToArray()
            };
        }
    }

    private Profile? FindByEmailLocked(string email) =>
        _profiles.FirstOrDefault(p => string.Equals(p.Email, email, StringComparison.OrdinalIgnoreCase));

    private static Profile NewProfile(string scid, long gameAccountId, string playerName)
    {
        var name = string.IsNullOrWhiteSpace(playerName) ? "Player" : playerName.Trim();
        return new Profile
        {
            Scid = scid,
            GameAccountId = gameAccountId,
            Name = name,
            Handle = DefaultHandle(name, gameAccountId),
            PlayerName = name
        };
    }

    private static string DefaultHandle(string name, long gameAccountId)
    {
        var handle = new string(name.Where(char.IsAsciiLetterOrDigit).ToArray());
        if (handle.Length < 5) handle = $"Player{gameAccountId}";
        return handle.Length > 18 ? handle[..18] : handle;
    }

    private static (string, string) Key(string a, string b) =>
        string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);
}
