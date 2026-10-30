using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Retravion.Application.AuditTrail;
using Retravion.Identity.Application;
using Retravion.Identity.Domain;
using Retravion.Web.Features.AuditTrail;
using Retravion.Web.Features.Identity.Logout;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed partial class AccountManagementTests
{
    [Fact]
    public async Task AuditDetailsAreNotRetrievedForAnonymousRestrictedOrdinaryOrRevokedActors()
    {
        var query = new SensitiveAuditQuery();
        await using var baseFactory = new AdministratorWebFactory(new StartupAdministratorStore(), "admin", Initial);
        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<IAuditQuery>(query)));
        using var scope = factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<AuditOperations>();
        using var anonymous = Client(factory);
        Assert.Null(await operations.Find(new ClaimsPrincipal(new ClaimsIdentity()), new()));
        using (var denied = await anonymous.GetAsync("/administration/audit"))
        {
            Assert.Equal("/", denied.Headers.Location?.OriginalString);
            Assert.DoesNotContain(SensitiveAuditQuery.Secret, await denied.Content.ReadAsStringAsync());
        }
        using var admin = Client(factory);
        var restricted = await ProductPrincipal(admin, factory.Services, "admin", Initial, "/password-replacement");
        Assert.Null(await operations.Find(restricted, new()));
        using (var denied = await admin.GetAsync("/administration/audit"))
        {
            Assert.Equal("/password-replacement", denied.Headers.Location?.OriginalString);
            Assert.DoesNotContain(SensitiveAuditQuery.Secret, await denied.Content.ReadAsStringAsync());
        }
        Assert.Equal(0, query.Calls);
        await Replace(admin, Initial, Replacement);
        var administrator = await ProductPrincipal(admin, factory.Services, "admin", Replacement, "/home");
        var page = await admin.GetStringAsync("/administration/audit");
        Assert.Matches("<summary[^>]*>View details</summary>", page);
        Assert.Contains(SensitiveAuditQuery.Secret, page);
        Assert.NotNull(await operations.Find(administrator, new()));
        using var created = await Create(admin, "ordinary", "Ordinary", AccountType.Ordinary);
        var password = (await created.Content.ReadFromJsonAsync<CreatedAccount>())!.TemporaryPassword;
        using var ordinary = Client(factory);
        await Login(ordinary, "ordinary", password);
        await Replace(ordinary, password, Replacement);
        var user = await ProductPrincipal(ordinary, factory.Services, "ordinary", Replacement, "/home");
        var calls = query.Calls;
        Assert.Null(await operations.Find(user, new()));
        using (var denied = await ordinary.GetAsync("/administration/audit"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.DoesNotContain(SensitiveAuditQuery.Secret, await denied.Content.ReadAsStringAsync());
        }
        await factory.Services.GetRequiredService<SessionTicketStore>().RemoveAsync(administrator.FindFirstValue(SessionTicketStore.SessionClaim)!);
        Assert.Null(await operations.Find(administrator, new()));
        using (var denied = await admin.GetAsync("/administration/audit"))
        {
            Assert.NotEqual(HttpStatusCode.OK, denied.StatusCode);
            Assert.DoesNotContain(SensitiveAuditQuery.Secret, await denied.Content.ReadAsStringAsync());
        }
        Assert.Equal(calls, query.Calls);
    }

    private sealed class SensitiveAuditQuery : IAuditQuery
    {
        internal const string Secret = "sensitive-historical-description";
        internal int Calls { get; private set; }
        public Task<IReadOnlyList<AuditInformation>> FindAsync(AuditFilter filter, CancellationToken cancellationToken = default)
        {
            Calls++;
            IReadOnlyList<AuditInformation> events = [new(new AuditActor.Unknown(), DateTimeOffset.UnixEpoch,
                "product.create", new AuditTarget.Identified("internal-id"), AuditActionOutcome.Success,
                new(Guid.NewGuid(), null, new("historical-id", "Name", "System", Secret, true)))];
            return Task.FromResult(events);
        }
    }
}
