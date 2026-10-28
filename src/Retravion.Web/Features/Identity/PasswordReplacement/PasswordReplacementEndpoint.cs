using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Retravion.Identity.Application;
using Retravion.Identity.Domain;
using ReplacementUseCase = Retravion.Identity.Application.PasswordReplacement;

namespace Retravion.Web.Features.Identity.PasswordReplacement;

internal static class PasswordReplacementEndpoint
{
    internal const string RestrictedClaim = "Retravion.PasswordReplacementRequired";

    internal static Task SignIn(HttpContext context, Administrator account, bool restricted)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, account.Username),
            new(ClaimTypes.Name, account.DisplayName)
        };
        claims.Add(restricted ? new Claim(RestrictedClaim, "true") : new Claim(ClaimTypes.Role,
            account.AccountType == AccountType.Administrator ? "Administrator" : "User"));
        return context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
            new AuthenticationProperties { IsPersistent = false });
    }

    internal static void MapPasswordReplacement(this WebApplication app) =>
        app.MapPost("/identity/password-replacement", async (HttpContext context,
            IAntiforgery antiforgery, ReplacementUseCase replacement, IAdministratorStore store, IPasswordHasher hasher) =>
        {
            if (context.User.Identity?.IsAuthenticated != true)
                return Results.LocalRedirect("/");
            if (!context.User.HasClaim(RestrictedClaim, "true"))
                return Results.LocalRedirect("/home");

            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest();
            }

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var newPassword = form["newPassword"].ToString();
            var confirmation = form["confirmNewPassword"].ToString();
            if (string.IsNullOrEmpty(newPassword) || string.IsNullOrEmpty(confirmation) ||
                !string.Equals(newPassword, confirmation, StringComparison.Ordinal))
                return Results.LocalRedirect("/password-replacement?confirmationFailed=true");

            var username = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            var result = await replacement.Replace(username, form["currentPassword"].ToString(),
                newPassword, context.RequestAborted);
            if (result.IsFailure)
                return Results.LocalRedirect("/password-replacement?failed=true");

            var loaded = await store.LoadForSignIn(username, context.RequestAborted);
            if (loaded.IsFailure || loaded.Value.HasNoValue ||
                !string.Equals(loaded.Value.Value.Username, username, StringComparison.OrdinalIgnoreCase) ||
                loaded.Value.Value.RequiresPasswordReplacement || !hasher.Verify(newPassword, loaded.Value.Value.PasswordHash))
            {
                await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return Results.LocalRedirect("/");
            }
            await SignIn(context, loaded.Value.Value, restricted: false);
            return Results.LocalRedirect("/home");
        });
}
