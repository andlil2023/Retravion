using System.Data.Common;
using Retravion.Application.Product;
using Retravion.Application.Product.Create;
using Retravion.Application.Product.Retrieve;
using Retravion.Application.Product.Status;
using Retravion.Infrastructure.Product;
using Retravion.Web.Features.Product;
using Retravion.Web.Features.Identity.Users;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Retravion.Web.Features.Identity.Logout;
using System.Security.Claims;
using Retravion.Web.Features.Identity.Login;
using Retravion.Web.Features.Identity.PasswordReplacement;
using MudBlazor.Services;
using Retravion.Identity.Application;
using Retravion.Identity.Infrastructure;
using Retravion.Web.Components;
using Retravion.Application.AuditTrail;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Web.Features.AuditTrail;

var recoveryMode = args.Length == 1 && args[0] == "--recover-initial-administrator";
var builder = WebApplication.CreateBuilder(recoveryMode ? [] : args);
// Also support source-tree `dotnet run` without a Development launch profile.
builder.WebHost.UseStaticWebAssets();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SessionTicketStore>();
builder.Services.AddScoped<AuthenticationStateProvider, SessionAuthenticationStateProvider>();
builder.Services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
    .Configure<SessionTicketStore, TimeProvider>((options, store, time) =>
    {
        options.SessionStore = store;
        options.TimeProvider = time;
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("CompletedPasswordReplacement", policy => policy
        .RequireAuthenticatedUser()
        .RequireRole("Administrator", "User")
        .RequireAssertion(context => !context.User.HasClaim(PasswordReplacementEndpoint.RestrictedClaim, "true")));
    options.AddPolicy("Administrator", policy => policy
        .RequireAuthenticatedUser().RequireRole("Administrator")
        .RequireAssertion(context => !context.User.HasClaim(PasswordReplacementEndpoint.RestrictedClaim, "true")));
    options.AddPolicy("PasswordReplacementRequired", policy => policy
        .RequireAuthenticatedUser()
        .RequireClaim(PasswordReplacementEndpoint.RestrictedClaim, "true"));
});
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/";
        options.Cookie.Name = "Retravion.Identity";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
        options.SlidingExpiration = false;
    });
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PasswordSignIn>();
builder.Services.AddSingleton<Retravion.Identity.Application.PasswordReplacement>();

builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddSingleton<IAdministratorStore>(_ => new PostgresAdministratorStore(
    builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required.")));
builder.Services.AddSingleton<IProductStore>(_ => new PostgresProductStore(
    builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required.")));
builder.Services.AddSingleton<CreateProduct>();
builder.Services.AddSingleton<RetrieveProduct>();
builder.Services.AddSingleton<ChangeProductStatus>();
builder.Services.AddScoped<ProductOperations>();
builder.Services.AddSingleton<AccountCreation>();
builder.Services.AddScoped<UserOperations>();
builder.Services.AddScoped<AccountAudit>();
builder.Services.AddSingleton<IAccountAuditTransaction>(_ => new PostgresAccountAuditTransaction(
    builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required.")));
builder.Services.AddSingleton<AdministratorInitializer>();
builder.Services.AddSingleton<AdministratorRecovery>();
builder.Services.AddScoped<AuditOperations>();
builder.Services.AddSingleton<IAuditQuery>(_ => new PostgresAuditQuery(
    builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required.")));
builder.Services.AddSingleton<IAuditSubmission>(_ => new PostgresAuditSubmission(
    builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required.")));

var app = builder.Build();
if (recoveryMode)
{
    // This command is for offline use only: all application hosts must already be stopped.
    try
    {
        var recovery = await app.Services.GetRequiredService<AdministratorRecovery>()
            .Recover(app.Configuration["InitialAdmin:Password"]);
        if (recovery.IsFailure)
        {
            Console.Error.WriteLine("Administrator recovery failed. Check the recovery configuration and account state.");
            Environment.ExitCode = 1;
        }
    }
    catch (Exception)
    {
        // Configuration and provider exceptions may include secrets.
        Console.Error.WriteLine("Administrator recovery failed. Check the recovery configuration and database availability.");
        Environment.ExitCode = 1;
    }
    finally
    {
        await app.DisposeAsync();
    }
    return;
}
try
{
    var initialization = await app.Services.GetRequiredService<AdministratorInitializer>().Initialize(
        app.Configuration["InitialAdmin:Username"], app.Configuration["InitialAdmin:Password"]);
    if (initialization.IsFailure)
    {
        throw new InvalidOperationException(initialization.Error);
    }
}
catch (Exception exception) when (exception is DbException or ArgumentException)
{
    // Provider/configuration exceptions may contain connection details. Fail closed without echoing them.
    throw new InvalidOperationException("Administrator initialization failed. Check database configuration and availability.");
}

app.UseAuthentication();
// Default-deny application requests. Only login, replacement, mapped public assets,
// and the Blazor transport (whose routed content is guarded separately) are exceptions.
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var replacementPage = string.Equals(path.Value?.TrimEnd('/'), "/password-replacement", StringComparison.OrdinalIgnoreCase);
    var replacementOperation = string.Equals(path.Value?.TrimEnd('/'), "/identity/password-replacement", StringComparison.OrdinalIgnoreCase);
    var logout = string.Equals(path.Value?.TrimEnd('/'), "/identity/logout", StringComparison.OrdinalIgnoreCase);
    var login = path == "/" || string.Equals(path.Value?.TrimEnd('/'), "/identity/login", StringComparison.OrdinalIgnoreCase);
    var publicResource = context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null
        || path.StartsWithSegments("/_blazor");
    if (context.User.Identity?.IsAuthenticated == true && !publicResource &&
        !context.RequestServices.GetRequiredService<SessionTicketStore>()
            .RecordActivity(context.User.FindFirstValue(SessionTicketStore.SessionClaim)))
        context.User = new ClaimsPrincipal(new ClaimsIdentity());
    var authenticated = context.User.Identity?.IsAuthenticated == true;
    var restricted = context.User.HasClaim(PasswordReplacementEndpoint.RestrictedClaim, "true");
    if (replacementPage && context.User.Identity?.IsAuthenticated != true)
    {
        context.Response.Redirect("/");
        return;
    }
    if (replacementPage && !restricted)
    {
        context.Response.Redirect("/home");
        return;
    }
    if (!login && !logout && !replacementPage && !replacementOperation && !publicResource)
    {
        if (!authenticated)
        {
            await context.RequestServices.GetRequiredService<AccountAudit>().RejectedRequest(context);
            context.Response.Redirect("/");
            return;
        }
        if (restricted)
        {
            await context.RequestServices.GetRequiredService<AccountAudit>().RejectedRequest(context);
            context.Response.Redirect("/password-replacement");
            return;
        }
        if ((!context.User.IsInRole("Administrator") && !context.User.IsInRole("User")) ||
            (path.StartsWithSegments("/administration") && !context.User.IsInRole("Administrator")))
        {
            await context.RequestServices.GetRequiredService<AccountAudit>().RejectedRequest(context);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
    }
    await next(context);
});
app.UseAuthorization();
app.UseAntiforgery();
app.MapLogin();
app.MapLogout();
app.MapPasswordReplacement();
app.MapUsers();
app.MapStaticAssets().AllowAnonymous();
// SessionTicketStore expires and revokes circuit principals. A transport-level
// ticket deadline would disconnect active users at their original sign-in expiry.
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.Run();

// The composition root is the only Web location that depends on persistence types.
internal sealed class PostgresAccountAuditTransaction(string connectionString,
    Func<Npgsql.NpgsqlTransaction, CancellationToken, Task>? commit = null) : IAccountAuditTransaction
{
    public async Task<AccountTransactionResult<T>> Execute<T>(
        Func<IAdministratorStore, Task<CSharpFunctionalExtensions.Result<T>>> change,
        AuditInformation success, CancellationToken cancellationToken)
    {
        var commitStarted = false;
        try
        {
            await using var connection = new Npgsql.NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var result = await change(new PostgresAdministratorStore(connection));
            if (result.IsFailure) return new(result);
            await PostgresAuditSubmission.SubmitInTransactionAsync(connection, transaction, success, cancellationToken);
            // Once commit is sent, an exception or cancellation may mean it committed.
            commitStarted = true;
            if (commit is null) await transaction.CommitAsync(cancellationToken);
            else await commit(transaction, cancellationToken);
            return new(result);
        }
        catch (Exception) when (commitStarted)
        {
            return new(CSharpFunctionalExtensions.Result.Failure<T>(
                "The account change outcome could not be confirmed. Refresh and check the account."), true);
        }
        catch (Npgsql.NpgsqlException exception) when (!cancellationToken.IsCancellationRequested &&
            (exception.IsTransient || exception is Npgsql.PostgresException
            {
                SqlState: Npgsql.PostgresErrorCodes.InsufficientPrivilege or Npgsql.PostgresErrorCodes.ReadOnlySqlTransaction
            }))
        {
            return new(CSharpFunctionalExtensions.Result.Failure<T>("The account change could not be completed."));
        }
    }
}
