using System.Security.Claims;
using CSharpFunctionalExtensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Retravion.Identity.Application;
using Retravion.Identity.Domain;
using Retravion.Web.Features.Identity.Logout;

namespace Retravion.Web.Features.Identity.Users;

// Both HTTP operations and circuit callbacks recheck server-side authorization before effects.
internal sealed class UserOperations(IAuthorizationService authorization, SessionTicketStore sessions,
    IAdministratorStore store, AccountCreation creation, IPasswordHasher hasher)
{
    private async Task<bool> Allowed(ClaimsPrincipal actor) =>
        sessions.IsActive(actor.FindFirstValue(SessionTicketStore.SessionClaim)) &&
        (await authorization.AuthorizeAsync(actor, "Administrator")).Succeeded;

    public async Task<Result<IReadOnlyList<AccountSummary>>> List(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        if (!await Allowed(actor)) return Result.Failure<IReadOnlyList<AccountSummary>>("Access denied.");
        return (await store.List(cancellationToken)).Map(accounts =>
            (IReadOnlyList<AccountSummary>)accounts.Select(AccountSummary.From).ToArray());
    }

    public async Task<Result> ChangeAvailability(ClaimsPrincipal actor, string username, bool active,
        CancellationToken cancellationToken = default)
    {
        if (!await Allowed(actor)) return Result.Failure("Access denied.");
        var actorUsername = actor.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(actorUsername)) return Result.Failure("Access denied.");
        return await AccountAvailability.Change(store, actorUsername, username, active, cancellationToken);
    }

    public async Task<Result<CreatedAccount>> ResetPassword(ClaimsPrincipal actor, string username,
        CancellationToken cancellationToken = default)
    {
        await sessions.CredentialGate.WaitAsync(cancellationToken);
        try
        {
            if (!await Allowed(actor)) return Result.Failure<CreatedAccount>("Access denied.");
            var actorUsername = actor.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(actorUsername)) return Result.Failure<CreatedAccount>("Access denied.");
            var result = await AccountPasswordReset.Reset(store, hasher, actorUsername, username, cancellationToken);
            if (result.IsSuccess) sessions.RevokeAccount(result.Value.Account.Username);
            return result;
        }
        finally { sessions.CredentialGate.Release(); }
    }

    public async Task<Result<CreatedAccount>> Create(ClaimsPrincipal actor, string? username, string? displayName,
        AccountType type, CancellationToken cancellationToken = default)
    {
        if (!await Allowed(actor)) return Result.Failure<CreatedAccount>("Access denied.");
        return await creation.Create(username, displayName, type, cancellationToken);
    }
}

internal static class UserEndpoints
{
    internal static void MapUsers(this WebApplication app)
    {
        app.MapGet("/administration/accounts", async (HttpContext context, UserOperations operations) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var result = await operations.List(context.User, context.RequestAborted);
            return result.IsSuccess ? Results.Ok(result.Value) : Results.BadRequest(new { error = result.Error });
        }).RequireAuthorization("Administrator");

        app.MapPost("/administration/accounts/{username}/password-reset", async (string username, HttpContext context,
            Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery, UserOperations operations) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException) { return Results.BadRequest(); }
            var result = await operations.ResetPassword(context.User, username, context.RequestAborted);
            return result.IsSuccess ? Results.Ok(result.Value) : Results.BadRequest(new { error = result.Error });
        }).RequireAuthorization("Administrator");

        app.MapPost("/administration/accounts/{username}/availability", async (string username, HttpContext context,
            Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery, UserOperations operations) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException) { return Results.BadRequest(); }
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (!bool.TryParse(form["active"], out var active)) return Results.BadRequest();
            var result = await operations.ChangeAvailability(context.User, username, active, context.RequestAborted);
            return result.IsSuccess ? Results.Ok() : Results.BadRequest(new { error = result.Error });
        }).RequireAuthorization("Administrator");

        app.MapPost("/administration/accounts", async (HttpContext context,
            Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery, UserOperations operations) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException) { return Results.BadRequest(); }
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (!Enum.TryParse<AccountType>(form["accountType"].ToString(), out var type))
                return Results.BadRequest(new { error = "Choose a valid account type." });
            var result = await operations.Create(context.User, form["username"], form["displayName"], type, context.RequestAborted);
            return result.IsSuccess ? Results.Ok(result.Value) : Results.BadRequest(new { error = result.Error });
        }).RequireAuthorization("Administrator");
    }
}
