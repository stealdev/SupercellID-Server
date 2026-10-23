using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ScidServer.Configuration;

namespace ScidServer.Security;

public sealed class PinService(ServerOptions options, ILogger<PinService> log)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, (string Pin, DateTimeOffset Expires)> _pins =
        new(StringComparer.OrdinalIgnoreCase);

    public void Issue(string key, string email)
    {
        var pin = options.FixedPin.Length > 0
            ? options.FixedPin
            : RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        _pins[key] = (pin, DateTimeOffset.UtcNow + Lifetime);
        log.LogInformation("PIN for {Email}: {Pin}", email, pin);
    }

    public bool Peek(string key, string pin) =>
        _pins.TryGetValue(key, out var entry)
        && entry.Expires > DateTimeOffset.UtcNow
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(entry.Pin), Encoding.UTF8.GetBytes(pin.Trim()));

    public bool Consume(string key, string pin)
    {
        if (!Peek(key, pin)) return false;
        _pins.TryRemove(key, out _);
        return true;
    }
}
