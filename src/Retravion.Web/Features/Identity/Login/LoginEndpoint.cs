using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Retravion.Identity.Application;

namespace Retravion.Web.Features.Identity.Login;

internal static class LoginEndpoint
{
    internal static void MapLogin(this WebApplication app) => app.MapPost("/identity/login", async (
        HttpContext context, IAntiforgery antiforgery, PasswordSignIn signIn) =>
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

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, result.Value),
            new Claim(ClaimTypes.Name, result.Value)
        }, CookieAuthenticationDefaults.AuthenticationScheme);
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = false });
        return Results.LocalRedirect("/home");
    });
}
