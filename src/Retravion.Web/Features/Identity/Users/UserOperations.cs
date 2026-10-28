using System.Security.Claims;
using CSharpFunctionalExtensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Retravion.Application.AuditTrail;
using Retravion.Identity.Application;
using Retravion.Identity.Domain;
using Retravion.Web.Features.Identity.Logout;

namespace Retravion.Web.Features.Identity.Users;

// Both HTTP operations and circuit callbacks recheck server-side authorization before effects.
internal sealed class UserOperations(IAuthorizationService authorization, SessionTicketStore sessions,
    IAdministratorStore store, IPasswordHasher hasher, IAccountAuditTransaction transaction, AccountAudit audit)
{
    private async Task<bool> Allowed(ClaimsPrincipal actor) =>
        sessions.IsActive(actor.FindFirstValue(SessionTicketStore.SessionClaim)) &&
        (await authorization.AuthorizeAsync(actor, "Administrator")).Succeeded;

    private async Task<AccountTransactionResult<T>> Attempt<T>(ClaimsPrincipal actor, string action, string? username,
        Func<Task<AccountTransactionResult<T>>> operation)
    {
        try { return await operation(); }
        catch (Exception)
        {
            // Preserve the original fault (including cancellation), even if reporting also faults.
            try { await audit.Failure(actor, action, username); }
            catch (Exception) { }
            throw;
        }
    }

    public async Task<Result<IReadOnlyList<AccountSummary>>> List(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        if (!await Allowed(actor)) return Result.Failure<IReadOnlyList<AccountSummary>>("Access denied.");
        return (await store.List(cancellationToken)).Map(accounts =>
            (IReadOnlyList<AccountSummary>)accounts.Select(AccountSummary.From).ToArray());
    }

    public async Task<Result> ChangeAvailability(ClaimsPrincipal actor, string username, bool active,
        CancellationToken cancellationToken = default)
    {
        var action = active ? "account.reactivate" : "account.inactivate";
        var actorUsername = actor.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!await Allowed(actor) || string.IsNullOrEmpty(actorUsername))
        {
            await audit.Failure(actor, action, username);
            return Result.Failure("Access denied.");
        }
        var outcome = await Attempt(actor, action, username, () => transaction.Execute(async transactionalStore =>
        {
            var result = await AccountAvailability.Change(transactionalStore, actorUsername, username, active, cancellationToken);
            return result.IsSuccess ? Result.Success(true) : Result.Failure<bool>(result.Error);
        }, audit.Information(actor, action, username, AuditActionOutcome.Success, targetKnown: true), cancellationToken));
        if (outcome.Result.IsFailure && !outcome.Uncertain) await audit.Failure(actor, action, username);
        return outcome.Result.IsSuccess ? Result.Success() : Result.Failure(outcome.Result.Error);
    }

    public async Task<Result<CreatedAccount>> ResetPassword(ClaimsPrincipal actor, string username,
        CancellationToken cancellationToken = default)
    {
        const string action = "account.password-reset";
        var outcome = await Attempt(actor, action, username, async () =>
        {
            await sessions.CredentialGate.WaitAsync(cancellationToken);
            try
            {
                var actorUsername = actor.FindFirstValue(ClaimTypes.NameIdentifier);
                if (!await Allowed(actor) || string.IsNullOrEmpty(actorUsername))
                    return new AccountTransactionResult<CreatedAccount>(Result.Failure<CreatedAccount>("Access denied."));
                var result = await transaction.Execute(transactionalStore =>
                    AccountPasswordReset.Reset(transactionalStore, hasher, actorUsername, username, cancellationToken),
                    audit.Information(actor, action, username, AuditActionOutcome.Success, targetKnown: true), cancellationToken);
                if (result.Result.IsSuccess || result.Uncertain) sessions.RevokeAccount(username);
                return result;
            }
            finally { sessions.CredentialGate.Release(); }
        });
        if (outcome.Result.IsFailure && !outcome.Uncertain) await audit.Failure(actor, action, username);
        return outcome.Result;
    }

    public async Task<Result<CreatedAccount>> Create(ClaimsPrincipal actor, string? username, string? displayName,
        AccountType type, CancellationToken cancellationToken = default)
    {
        const string action = "account.create";
        if (!await Allowed(actor))
        {
            await audit.Failure(actor, action, username);
            return Result.Failure<CreatedAccount>("Access denied.");
        }
        var outcome = await Attempt(actor, action, username, () => transaction.Execute(transactionalStore =>
            new AccountCreation(transactionalStore, hasher).Create(username, displayName, type, cancellationToken),
            audit.Information(actor, action, username, AuditActionOutcome.Success, targetKnown: true), cancellationToken));
        if (outcome.Result.IsFailure && !outcome.Uncertain) await audit.Failure(actor, action, username);
        return outcome.Result;
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
            Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery, UserOperations operations, AccountAudit audit) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException) { await audit.RejectedRequest(context); return Results.BadRequest(); }
            var result = await operations.ResetPassword(context.User, username, context.RequestAborted);
            return result.IsSuccess ? Results.Ok(result.Value) : Results.BadRequest(new { error = result.Error });
        }).RequireAuthorization("Administrator");

        app.MapPost("/administration/accounts/{username}/availability", async (string username, HttpContext context,
            Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery, UserOperations operations, AccountAudit audit) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException) { await audit.RejectedRequest(context); return Results.BadRequest(); }
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (!bool.TryParse(form["active"], out var active)) { await audit.RejectedRequest(context); return Results.BadRequest(); }
            var result = await operations.ChangeAvailability(context.User, username, active, context.RequestAborted);
            return result.IsSuccess ? Results.Ok() : Results.BadRequest(new { error = result.Error });
        }).RequireAuthorization("Administrator");

        app.MapPost("/administration/accounts", async (HttpContext context,
            Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery, UserOperations operations, AccountAudit audit) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException) { await audit.RejectedRequest(context); return Results.BadRequest(); }
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (!Enum.TryParse<AccountType>(form["accountType"].ToString(), out var type))
            {
                await audit.RejectedRequest(context);
                return Results.BadRequest(new { error = "Choose a valid account type." });
            }
            var result = await operations.Create(context.User, form["username"], form["displayName"], type, context.RequestAborted);
            return result.IsSuccess ? Results.Ok(result.Value) : Results.BadRequest(new { error = result.Error });
        }).RequireAuthorization("Administrator");
    }
}
