using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Retravion.Identity.Application;
using Retravion.Web.Features.Identity.PasswordReplacement;

namespace Retravion.Web.Features.Identity.Login;

internal static class LoginEndpoint
{
    internal static void MapLogin(this WebApplication app) => app.MapPost("/identity/login", async (
        HttpContext context, IAntiforgery antiforgery, PasswordSignIn signIn, IAdministratorStore store,
        IPasswordHasher hasher) =>
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
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.LocalRedirect("/?failed=true");
        }

        var loaded = await store.Load(context.RequestAborted);
        if (loaded.IsFailure || loaded.Value.HasNoValue ||
            !string.Equals(loaded.Value.Value.Username, result.Value, StringComparison.Ordinal) ||
            !hasher.Verify(form["password"].ToString(), loaded.Value.Value.PasswordHash))
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.LocalRedirect("/?failed=true");
        }

        var restricted = loaded.Value.Value.RequiresPasswordReplacement;
        await PasswordReplacementEndpoint.SignIn(context, result.Value, restricted);
        return Results.LocalRedirect(restricted ? "/password-replacement" : "/home");
    });
}
