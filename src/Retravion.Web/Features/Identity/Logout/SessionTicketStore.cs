using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Retravion.Web.Features.Identity.Logout;

// Process-local by design: restarting the host ends every session.
internal sealed class SessionTicketStore(TimeProvider? timeProvider = null) : ITicketStore, IDisposable
{
    internal const string SessionClaim = "Retravion.Session";
    internal static readonly TimeSpan InactivityTimeout = TimeSpan.FromMinutes(30);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, Session> _tickets = new();
    private readonly object _gate = new();
    internal event Action? Revoked;

    public Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = Guid.NewGuid().ToString("N");
        var expiry = ticket.Properties.ExpiresUtc
            ?? throw new InvalidOperationException("Session tickets must have an expiration.");
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (expiry > now)
            {
                var session = new Session(Serialize(key, ticket, expiry), expiry);
                _tickets.Add(key, session);
                session.Timer = _time.CreateTimer(_ => Expire(key), null,
                    expiry - now, Timeout.InfiniteTimeSpan);
            }
        }
        return Task.FromResult(key);
    }

    public Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        lock (_gate)
        {
            // Cookie renewal is not activity and must never resurrect an ended session.
            if (Active(key) is { } session)
                session.Ticket = Serialize(key, ticket, session.Expires);
        }
        return Task.CompletedTask;
    }

    public Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        lock (_gate)
            return Task.FromResult(Active(key) is { } session
                ? TicketSerializer.Default.Deserialize(session.Ticket) : null);
    }

    public Task RemoveAsync(string key)
    {
        lock (_gate)
        {
            if (_tickets.Remove(key, out var session)) session.Timer?.Dispose();
        }
        Revoked?.Invoke();
        return Task.CompletedTask;
    }

    internal bool IsActive(string? key)
    {
        lock (_gate) return Active(key) is not null;
    }

    // Only application requests and reported user interactions call this method.
    // Authentication checks, rendering, and transport keepalives never extend a session.
    internal bool RecordActivity(string? key)
    {
        lock (_gate)
        {
            if (Active(key) is not { } session) return false;
            session.Expires = _time.GetUtcNow() + InactivityTimeout;
            session.Ticket = Serialize(key!, TicketSerializer.Default.Deserialize(session.Ticket)!, session.Expires);
            session.Timer!.Change(InactivityTimeout, Timeout.InfiniteTimeSpan);
            return true;
        }
    }

    private Session? Active(string? key) => key is not null &&
        _tickets.TryGetValue(key, out var session) && session.Expires > _time.GetUtcNow() ? session : null;

    private void Expire(string key)
    {
        lock (_gate)
        {
            if (!_tickets.TryGetValue(key, out var session) || session.Expires > _time.GetUtcNow()) return;
            _tickets.Remove(key);
            session.Timer?.Dispose();
        }
        Revoked?.Invoke();
    }

    private static byte[] Serialize(string key, AuthenticationTicket ticket, DateTimeOffset expiry)
    {
        var principal = ticket.Principal.Clone();
        foreach (var identity in principal.Identities)
            foreach (var claim in identity.FindAll(SessionClaim).ToArray())
                identity.RemoveClaim(claim);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(SessionClaim, key));
        var properties = new AuthenticationProperties(new Dictionary<string, string?>(ticket.Properties.Items))
        {
            ExpiresUtc = expiry
        };
        return TicketSerializer.Default.Serialize(new AuthenticationTicket(principal, properties, ticket.AuthenticationScheme));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var session in _tickets.Values) session.Timer?.Dispose();
            _tickets.Clear();
        }
    }

    private sealed class Session(byte[] ticket, DateTimeOffset expires)
    {
        internal byte[] Ticket { get; set; } = ticket;
        internal DateTimeOffset Expires { get; set; } = expires;
        internal ITimer? Timer { get; set; }
    }
}
