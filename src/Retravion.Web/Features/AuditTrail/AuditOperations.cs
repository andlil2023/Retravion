using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Retravion.Application.AuditTrail;
using Retravion.Web.Features.Identity.Logout;

namespace Retravion.Web.Features.AuditTrail;

internal sealed class AuditOperations(IAuthorizationService authorization, SessionTicketStore sessions, IAuditQuery query)
{
    public async Task<IReadOnlyList<AuditInformation>?> Find(ClaimsPrincipal actor, AuditFilter filter, CancellationToken cancellationToken = default)
    {
        if (!sessions.IsActive(actor.FindFirstValue(SessionTicketStore.SessionClaim)) ||
            !(await authorization.AuthorizeAsync(actor, "Administrator")).Succeeded)
            return null;
        return await query.FindAsync(filter, cancellationToken);
    }
}
