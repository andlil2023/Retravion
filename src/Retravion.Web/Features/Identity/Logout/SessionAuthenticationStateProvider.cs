using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Retravion.Web.Features.Identity.Logout;

// Circuits retain their principal independently of HTTP authentication. Revalidate
// on revocation as well as when the host initializes or reconnects a circuit.
internal sealed class SessionAuthenticationStateProvider : AuthenticationStateProvider,
    IHostEnvironmentAuthenticationStateProvider, IDisposable
{
    private static readonly AuthenticationState Anonymous = new(new ClaimsPrincipal(new ClaimsIdentity()));
    private readonly SessionTicketStore _store;
    private readonly object _gate = new();
    private Task<AuthenticationState> _state = Task.FromResult(Anonymous);

    public SessionAuthenticationStateProvider(SessionTicketStore store)
    {
        _store = store;
        _store.Revoked += Revalidate;
    }

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        lock (_gate) return Validate(_state);
    }

    public void SetAuthenticationState(Task<AuthenticationState> authenticationStateTask)
    {
        lock (_gate)
        {
            _state = Validate(authenticationStateTask);
            NotifyAuthenticationStateChanged(_state);
        }
    }

    private void Revalidate()
    {
        lock (_gate)
        {
            _state = Validate(_state);
            NotifyAuthenticationStateChanged(_state);
        }
    }

    private async Task<AuthenticationState> Validate(Task<AuthenticationState> stateTask)
    {
        var state = await stateTask;
        return state.User.Identity?.IsAuthenticated == true &&
            !_store.IsActive(state.User.FindFirstValue(SessionTicketStore.SessionClaim)) ? Anonymous : state;
    }

    public void Dispose() => _store.Revoked -= Revalidate;
}
