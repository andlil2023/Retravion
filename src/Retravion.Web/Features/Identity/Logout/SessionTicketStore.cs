using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Memory;

namespace Retravion.Web.Features.Identity.Logout;

// Process-local by design: restarting the host ends every session.
internal sealed class SessionTicketStore : ITicketStore, IDisposable
{
    internal const string SessionClaim = "Retravion.Session";
    private readonly MemoryCache _tickets = new(new MemoryCacheOptions());
    private readonly object _gate = new();
    internal event Action? Revoked;

    public Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = Guid.NewGuid().ToString("N");
        lock (_gate) Save(key, ticket);
        return Task.FromResult(key);
    }

    public Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        lock (_gate)
        {
            // An in-flight request must never resurrect a logged-out session.
            if (_tickets.TryGetValue(key, out _)) Save(key, ticket);
        }
        return Task.CompletedTask;
    }

    public Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        lock (_gate)
        {
            var ticket = _tickets.TryGetValue(key, out byte[]? bytes)
                ? TicketSerializer.Default.Deserialize(bytes!) : null;
            return Task.FromResult(ticket);
        }
    }

    public Task RemoveAsync(string key)
    {
        lock (_gate) _tickets.Remove(key);
        Revoked?.Invoke();
        return Task.CompletedTask;
    }

    internal bool IsActive(string? key)
    {
        lock (_gate) return key is not null && _tickets.TryGetValue(key, out _);
    }

    private void Save(string key, AuthenticationTicket ticket)
    {
        var principal = ticket.Principal.Clone();
        foreach (var identity in principal.Identities)
            foreach (var claim in identity.FindAll(SessionClaim).ToArray())
                identity.RemoveClaim(claim);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(SessionClaim, key));
        var stored = new AuthenticationTicket(principal, ticket.Properties, ticket.AuthenticationScheme);
        var expiry = ticket.Properties.ExpiresUtc
            ?? throw new InvalidOperationException("Session tickets must have an expiration.");
        if (expiry > DateTimeOffset.UtcNow)
            _tickets.Set(key, TicketSerializer.Default.Serialize(stored), expiry);
    }

    public void Dispose() => _tickets.Dispose();
}
