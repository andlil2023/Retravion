using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Retravion.Application.AuditTrail;
using Retravion.Domain.Product;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.Product;
using Xunit;
using ProductState = Retravion.Domain.Product.Product;

namespace Retravion.IntegrationTests;

public sealed partial class AccountManagementTests
{
    [Fact]
    public async Task DeniedProductCallbacksAuditWithoutSubmittedValuesAndLogSubmissionFailuresSafely()
    {
        var products = new ProductStoreDouble();
        var audit = new ProductAuditRecorder();
        var logger = new ProductAuditLogger();
        await using var baseFactory = new AdministratorWebFactory(new StartupAdministratorStore(), "admin", Initial);
        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<Retravion.Application.Product.IProductStore>(products);
            services.AddSingleton<IAuditSubmission>(audit);
            services.AddSingleton<ILogger<ProductOperations>>(logger);
        }));
        using var client = Client(factory);
        var restricted = await ProductPrincipal(client, factory.Services, "admin", Initial, "/password-replacement");
        await Replace(client, Initial, Replacement);
        var revoked = await ProductPrincipal(client, factory.Services, "admin", Replacement, "/home");
        await factory.Services.GetRequiredService<SessionTicketStore>().RemoveAsync(revoked.FindFirstValue(SessionTicketStore.SessionClaim)!);
        using var scope = factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<ProductOperations>();
        var expected = ProductState.Create("private-id", "private-name", ProductKind.System, "private-description").Value;
        foreach (var actor in new[] { new ClaimsPrincipal(new ClaimsIdentity()), restricted, revoked })
        {
            audit.Events.Clear();
            Assert.True((await operations.Create(actor, "private-id", "private-name", ProductKind.System, "private-description")).IsFailure);
            Assert.True((await operations.Edit(actor, expected, "private-new-id", "private-new-name", ProductKind.Firmware, "private-new-description", false)).IsFailure);
            Assert.True((await operations.Edit(actor, expected, "private-new-id", "private-new-name", ProductKind.Firmware, "private-new-description", true)).IsFailure);
            Assert.True((await operations.Retire(actor, expected)).IsFailure);
            Assert.True((await operations.ChangeStatus(actor, "private-id", expected.InternalId, false)).IsFailure);
            Assert.True((await operations.ChangeStatus(actor, "private-id", expected.InternalId, true)).IsFailure);
            Assert.True((await operations.UpdateDescription(actor, "private-id", expected.InternalId, "private-description", "private-new-description")).IsFailure);
            Assert.True((await operations.List(actor)).IsFailure);
            Assert.True((await operations.Retrieve(actor, "private-id")).IsFailure);
            Assert.Equal(new[] { "product.create", "product.edit", "product.reactivate", "product.retire", "product.retire", "product.reactivate", "product.edit" }, audit.Events.Select(e => e.Action));
            Assert.All(audit.Events, e =>
            {
                Assert.Equal(AuditActionOutcome.Failure, e.Outcome);
                Assert.IsType<AuditTarget.Unknown>(e.Target);
                Assert.Null(e.ProductChange);
                if (actor.Identity?.IsAuthenticated == true) Assert.Equal(new AuditActor.Identified("admin"), e.Actor);
                else Assert.IsType<AuditActor.Unknown>(e.Actor);
                Assert.InRange(e.OccurredAt, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow);
            });
        }
        Assert.Empty(logger.Messages);
        foreach (var throwing in new[] { false, true })
        {
            audit.Fail = true;
            audit.Throw = throwing;
            Assert.True((await operations.Create(revoked, "private-id", "private-name", ProductKind.System, "private-description")).IsFailure);
        }
        Assert.Equal(2, logger.Messages.Count);
        Assert.All(logger.Messages, message => Assert.Equal("Unauthorized Product-change audit submission failed.", message));
        Assert.Equal(0, products.Calls);
    }

    [Theory]
    [InlineData("product.create")]
    [InlineData("product.edit")]
    [InlineData("product.retire")]
    [InlineData("product.reactivate")]
    public async Task AuditViewShowsEventTimeProductIdWithoutExposingInternalUuid(string action)
    {
        var id = Guid.NewGuid();
        var values = new ProductAuditValues("<script>event-time & ID</script>", "Name", "System", "", action != "product.retire");
        var previous = action == "product.create" ? null : values with { Id = "prior-id-not-for-display" };
        AuditInformation[] events =
        [
            new(new AuditActor.Identified("author"), DateTimeOffset.UnixEpoch, action,
                new AuditTarget.Identified(id.ToString()), AuditActionOutcome.Success,
                new ProductAuditDetails(id, previous, values)),
            new(new AuditActor.Unknown(), DateTimeOffset.UnixEpoch, "account.create",
                new AuditTarget.Identified("legacy-account-target"), AuditActionOutcome.Success),
            new(new AuditActor.Unknown(), DateTimeOffset.UnixEpoch, "product.edit",
                new AuditTarget.Unknown(), AuditActionOutcome.Failure),
            new(new AuditActor.Unknown(), DateTimeOffset.UnixEpoch, "legacy-activity",
                new AuditTarget.NotApplicable(), AuditActionOutcome.Success)
        ];
        await using var baseFactory = new AdministratorWebFactory(new StartupAdministratorStore(), "admin", Initial);
        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<IAuditQuery>(new ProductAuditViewQuery(events))));
        using var admin = Client(factory);
        await Login(admin, "admin", Initial);
        await Replace(admin, Initial, Replacement);
        var page = await admin.GetStringAsync("/administration/audit");
        Assert.Contains("Identified: &lt;script&gt;event-time &amp; ID&lt;/script&gt;", page);
        Assert.DoesNotContain("<script>event-time", page);
        Assert.DoesNotContain(id.ToString(), page);
        // Historical IDs belong only to the disclosure, never the list target cell.
        Assert.DoesNotContain("prior-id-not-for-display", System.Text.RegularExpressions.Regex.Replace(page, "<details\\b[\\s\\S]*?</details>", ""));
        if (previous is not null) Assert.Contains("prior-id-not-for-display", page);
        Assert.Contains("Identified: legacy-account-target", page);
        Assert.Contains("Unknown", page);
        Assert.Contains("Not applicable", page);
        // Rendering does not replace the immutable target in the audit facts.
        Assert.Equal(new AuditTarget.Identified(id.ToString()), events[0].Target);
    }

    private sealed class ProductAuditViewQuery(IReadOnlyList<AuditInformation> events) : IAuditQuery
    {
        public Task<IReadOnlyList<AuditInformation>> FindAsync(AuditFilter filter, CancellationToken cancellationToken = default) =>
            Task.FromResult(events);
    }

    private sealed class ProductAuditRecorder : IAuditSubmission
    {
        public List<AuditInformation> Events { get; } = [];
        public bool Fail;
        public bool Throw;
        public Task<AuditSubmissionResult> SubmitAsync(AuditInformation information, CancellationToken cancellationToken = default)
        {
            if (!information.Action.StartsWith("product.", StringComparison.Ordinal)) return Task.FromResult(AuditSubmissionResult.Success);
            if (Throw) throw new IOException("private-provider-details");
            Events.Add(information);
            return Task.FromResult(Fail ? AuditSubmissionResult.Failure : AuditSubmissionResult.Success);
        }
    }

    private sealed class ProductAuditLogger : ILogger<ProductOperations>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Assert.Equal(LogLevel.Error, level);
            Assert.Null(exception);
            Messages.Add(formatter(state, exception));
        }
    }
}
