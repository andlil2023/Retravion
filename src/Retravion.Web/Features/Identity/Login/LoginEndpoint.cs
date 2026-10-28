using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Retravion.Application.AuditTrail;
using Retravion.Identity.Application;
using Retravion.Web.Features.Identity.PasswordReplacement;

namespace Retravion.Web.Features.Identity.Login;

internal static class LoginEndpoint
{
    internal static void MapLogin(this WebApplication app) => app.MapPost("/identity/login", async (
        HttpContext context, IAntiforgery antiforgery, PasswordSignIn signIn, IAdministratorStore store,
        IPasswordHasher hasher, IAuditSubmission audit, TimeProvider time, ILoggerFactory loggerFactory) =>
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest();
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var result = await signIn.Authenticate(form["username"].ToString(), form["password"].ToString(),
            context.RequestAborted);
        if (result.IsFailure)
        {
            await SubmitDecision(audit, time, loggerFactory, context.RequestAborted, null);
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.LocalRedirect("/?failed=true");
        }

        var loaded = await store.LoadForSignIn(result.Value, context.RequestAborted);
        if (loaded.IsFailure || loaded.Value.HasNoValue ||
            !string.Equals(loaded.Value.Value.Username, result.Value, StringComparison.Ordinal) ||
            !hasher.Verify(form["password"].ToString(), loaded.Value.Value.PasswordHash))
        {
            await SubmitDecision(audit, time, loggerFactory, context.RequestAborted, null);
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.LocalRedirect("/?failed=true");
        }

        if (!await SubmitDecision(audit, time, loggerFactory, context.RequestAborted, result.Value) ||
            context.RequestAborted.IsCancellationRequested)
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.LocalRedirect("/?failed=true");
        }

        // Confirmation may take several seconds; do not issue a session from a stale credential snapshot.
        loaded = await store.LoadForSignIn(result.Value, context.RequestAborted);
        if (loaded.IsFailure || loaded.Value.HasNoValue ||
            !string.Equals(loaded.Value.Value.Username, result.Value, StringComparison.Ordinal) ||
            !hasher.Verify(form["password"].ToString(), loaded.Value.Value.PasswordHash) ||
            context.RequestAborted.IsCancellationRequested)
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.LocalRedirect("/?failed=true");
        }

        var restricted = loaded.Value.Value.RequiresPasswordReplacement;
        await PasswordReplacementEndpoint.SignIn(context, loaded.Value.Value, restricted);
        return Results.LocalRedirect(restricted ? "/password-replacement" : "/home");
    });

    private static async Task<bool> SubmitDecision(IAuditSubmission audit, TimeProvider time,
        ILoggerFactory loggerFactory, CancellationToken requestAborted, string? verifiedUsername)
    {
        var information = new AuditInformation(
            verifiedUsername is null ? new AuditActor.Unknown() : new AuditActor.Identified(verifiedUsername),
            time.GetUtcNow(), "identity.login", new AuditTarget.Unknown(),
            verifiedUsername is null ? AuditActionOutcome.Failure : AuditActionOutcome.Success);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var result = await audit.SubmitAsync(information, deadline.Token).WaitAsync(deadline.Token);
            if (result == AuditSubmissionResult.Success && !deadline.IsCancellationRequested) return true;
        }
        catch (Exception)
        {
            // Audit failures must not disclose provider details or grant access.
        }

        loggerFactory.CreateLogger("Retravion.Web.Features.Identity.Login.LoginEndpoint")
            .LogWarning("Login audit submission could not be confirmed.");
        return false;
    }
}
