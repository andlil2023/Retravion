using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Application.AiWorkflow;

namespace Retravion.Web.Features.Mcp;

// Process-local, like browser sessions. Store only credential digests; one credential per session.
internal sealed class McpCredentials(SessionTicketStore sessions, TimeProvider time, IAiWorkflowSettingsStore settings)
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    internal const string CredentialClaim = "Retravion.McpCredential";
    private const string SettingsRevisionClaim = "Retravion.McpSettingsRevision";
    private readonly object gate = new();
    private readonly Dictionary<string, Credential> credentials = [];

    // The terminal bridge already holds the secret; only its digest is ever bound to the approving session.
    internal async Task<bool> Issue(ClaimsPrincipal principal, IAuthorizationService authorization, byte[] digest,
        CancellationToken cancellationToken = default)
    {
        var session = principal.FindFirstValue(SessionTicketStore.SessionClaim);
        if (!sessions.IsActive(session) ||
            !(await authorization.AuthorizeAsync(principal, "CompletedPasswordReplacement")).Succeeded)
            return false;
        var configuration = await settings.Load(cancellationToken);
        if (configuration.IsFailure || !configuration.Value.Enabled) return false;
        var expires = time.GetUtcNow() + Lifetime;
        var identity = principal.Clone();
        ((ClaimsIdentity)identity.Identity!).AddClaim(new Claim(CredentialClaim, Guid.NewGuid().ToString("N")));
        ((ClaimsIdentity)identity.Identity!).AddClaim(new Claim(SettingsRevisionClaim,
            configuration.Value.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        lock (gate)
        {
            foreach (var key in credentials.Where(p => p.Value.Expires <= time.GetUtcNow() || !sessions.IsActive(p.Value.Session)).Select(p => p.Key).ToArray())
                credentials.Remove(key);
            credentials[session!] = new(digest, identity, session!, expires);
        }
        return true;
    }

    // Sliding lifetime: permitted terminal activity keeps the credential alive; idle credentials still expire.
    internal void RecordActivity(ClaimsPrincipal principal)
    {
        if (!IsActive(principal)) return;
        var session = principal.FindFirstValue(SessionTicketStore.SessionClaim)!;
        lock (gate)
            if (credentials.TryGetValue(session, out var credential) && credential.Expires > time.GetUtcNow())
                credentials[session] = credential with { Expires = time.GetUtcNow() + Lifetime };
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

    internal async Task<bool> IsAllowed(ClaimsPrincipal principal, CancellationToken token = default)
    {
        if (!IsActive(principal)) return false;
        var configuration = await settings.Load(token);
        return configuration.IsSuccess && configuration.Value.Enabled &&
            long.TryParse(principal.FindFirstValue(SettingsRevisionClaim), out var revision) &&
            configuration.Value.Revision == revision;
    }

    internal void Revoke(ClaimsPrincipal principal)
    {
        var session = principal.FindFirstValue(SessionTicketStore.SessionClaim);
        if (session is not null) lock (gate) credentials.Remove(session);
    }

    internal static byte[] Digest(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
    private sealed record Credential(byte[] Digest, ClaimsPrincipal Principal, string Session, DateTimeOffset Expires);
}
