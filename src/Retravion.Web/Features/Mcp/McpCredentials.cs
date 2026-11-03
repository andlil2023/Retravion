using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Retravion.Web.Features.Identity.Logout;

namespace Retravion.Web.Features.Mcp;

// Process-local, like browser sessions. Store only credential digests; one credential per session.
internal sealed class McpCredentials(SessionTicketStore sessions, TimeProvider time)
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    internal const string CredentialClaim = "Retravion.McpCredential";
    private readonly object gate = new();
    private readonly Dictionary<string, Credential> credentials = [];

    internal async Task<IssuedMcpCredential?> Issue(ClaimsPrincipal principal, IAuthorizationService authorization)
    {
        var session = principal.FindFirstValue(SessionTicketStore.SessionClaim);
        if (!sessions.IsActive(session) ||
            !(await authorization.AuthorizeAsync(principal, "CompletedPasswordReplacement")).Succeeded)
            return null;
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var expires = time.GetUtcNow() + Lifetime;
        var identity = principal.Clone();
        ((ClaimsIdentity)identity.Identity!).AddClaim(new Claim(CredentialClaim, Guid.NewGuid().ToString("N")));
        lock (gate)
        {
            foreach (var key in credentials.Where(p => p.Value.Expires <= time.GetUtcNow() || !sessions.IsActive(p.Value.Session)).Select(p => p.Key).ToArray())
                credentials.Remove(key);
            credentials[session!] = new(Digest(token), identity, session!, expires);
        }
        return new(token, expires);
    }

    internal ClaimsPrincipal Resolve(string? token)
    {
        if (token is not { Length: 64 }) return new(new ClaimsIdentity());
        var digest = Digest(token);
        lock (gate)
        {
            var credential = credentials.Values.FirstOrDefault(c => CryptographicOperations.FixedTimeEquals(c.Digest, digest));
            // Retain the known actor on revoked sessions; the request and write boundaries still deny access.
            return credential is not null && credential.Expires > time.GetUtcNow()
                ? credential.Principal.Clone() : new(new ClaimsIdentity());
        }
    }

    internal bool IsActive(ClaimsPrincipal principal)
    {
        var session = principal.FindFirstValue(SessionTicketStore.SessionClaim);
        lock (gate)
            return session is not null && credentials.TryGetValue(session, out var credential) &&
                credential.Expires > time.GetUtcNow() && sessions.IsActive(session) &&
                credential.Principal.FindFirstValue(CredentialClaim) == principal.FindFirstValue(CredentialClaim);
    }

    internal void Revoke(ClaimsPrincipal principal)
    {
        var session = principal.FindFirstValue(SessionTicketStore.SessionClaim);
        if (session is not null) lock (gate) credentials.Remove(session);
    }

    private static byte[] Digest(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
    private sealed record Credential(byte[] Digest, ClaimsPrincipal Principal, string Session, DateTimeOffset Expires);
}

internal sealed record IssuedMcpCredential(string Token, DateTimeOffset ExpiresAt);
