using System.Security.Cryptography;

namespace Retravion.Web.Features.Mcp;

internal enum McpConnectionState { Pending, Approved, Denied, Expired }

internal sealed record StartedMcpConnection(string DeviceCode, string UserCode);

// Process-local browser handoff for the terminal bridge. Holds only the digest of the bridge's secret:
// approval binds that digest to the approving session, so no credential is ever sent back or stored.
internal sealed class McpConnections(TimeProvider time)
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    internal const int Capacity = 20;
    // No 0/O/1/I/L: the user compares this code between terminal and browser.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private readonly object gate = new();
    private readonly Dictionary<string, Pending> pending = new(StringComparer.Ordinal);

    internal StartedMcpConnection? Start()
    {
        var deviceCode = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        lock (gate)
        {
            Purge();
            if (pending.Count >= Capacity) return null;
            string userCode;
            do userCode = $"{RandomNumberGenerator.GetString(Alphabet, 4)}-{RandomNumberGenerator.GetString(Alphabet, 4)}";
            while (pending.ContainsKey(userCode));
            pending[userCode] = new(McpCredentials.Digest(deviceCode), time.GetUtcNow() + Lifetime, McpConnectionState.Pending);
            return new(deviceCode, userCode);
        }
    }

    internal static string? Normalize(string? userCode)
    {
        var code = userCode?.Trim().ToUpperInvariant();
        return code is { Length: 9 } && code[4] == '-' && code.Where((c, i) => i != 4).All(Alphabet.Contains) ? code : null;
    }

    internal bool IsPending(string? userCode)
    {
        lock (gate)
        {
            Purge();
            return Normalize(userCode) is { } code && pending.TryGetValue(code, out var entry) && entry.State == McpConnectionState.Pending;
        }
    }

    // Single use: a decision is taken once. Returns the digest to bind on approval.
    internal byte[]? Decide(string? userCode, bool approve)
    {
        lock (gate)
        {
            Purge();
            if (Normalize(userCode) is not { } code || !pending.TryGetValue(code, out var entry) ||
                entry.State != McpConnectionState.Pending) return null;
            pending[code] = entry with { State = approve ? McpConnectionState.Approved : McpConnectionState.Denied };
            return entry.Digest;
        }
    }

    internal void Deny(byte[] digest)
    {
        lock (gate)
            foreach (var key in pending.Where(p => CryptographicOperations.FixedTimeEquals(p.Value.Digest, digest)).Select(p => p.Key).ToArray())
                pending[key] = pending[key] with { State = McpConnectionState.Denied };
    }

    // The bridge learns a final outcome once; the entry is then forgotten.
    internal McpConnectionState Status(string? deviceCode)
    {
        if (deviceCode is not { Length: 64 }) return McpConnectionState.Expired;
        var digest = McpCredentials.Digest(deviceCode);
        lock (gate)
        {
            Purge();
            var match = pending.FirstOrDefault(p => CryptographicOperations.FixedTimeEquals(p.Value.Digest, digest));
            if (match.Key is null) return McpConnectionState.Expired;
            if (match.Value.State != McpConnectionState.Pending) pending.Remove(match.Key);
            return match.Value.State;
        }
    }

    private void Purge()
    {
        foreach (var key in pending.Where(p => p.Value.Expires <= time.GetUtcNow()).Select(p => p.Key).ToArray())
            pending.Remove(key);
    }

    private sealed record Pending(byte[] Digest, DateTimeOffset Expires, McpConnectionState State);
}
