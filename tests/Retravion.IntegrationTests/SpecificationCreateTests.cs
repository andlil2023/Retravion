using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Application.Specification;
using Retravion.Domain.Specification;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Specification;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.Product;
using Retravion.Web.Features.Specification;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed partial class SpecificationReadTests
{
    private static readonly DateTimeOffset CreateTime = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
    private static readonly AuditActor.Identified CreateActor = new("specification-author");

    [PostgresFact]
    public async Task SpecificationCreateStoresDraftAndInitialAuditWithExactUtf8Boundary()
    {
        await using var db = await Database.Create();
        var products = new PostgresProductStore(db.ConnectionString);
        var product = await Product(products, "first");
        var other = await Product(products, "second");
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var sessions = scope.ServiceProvider.GetRequiredService<SessionTicketStore>();
        var principal = await Principal(sessions, "User", now: CreateTime);
        var markdown = new string('é', 524288);
        var result = await operations.Create(principal, product.InternalId, "\t DEV-42\u2003", "A title", markdown);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);
        var draft = result.Value;
        Assert.NotEqual(Guid.Empty, draft.InternalId);
        Assert.Equal(product.InternalId, draft.ProductInternalId);
        Assert.Equal("DEV-42", draft.Id);
        Assert.Equal("A title", draft.Title);
        Assert.Equal(markdown, draft.Description);
        Assert.Equal(draft, (await operations.Find(principal, product.InternalId, draft.InternalId)).Value.Value);
        Assert.Equal("", (await operations.Create(principal, other.InternalId, "dev-42", "Second", null)).Value.Description);
        var events = await CreateEvents(db);
        Assert.Equal(2, events.Count);
        var audit = events[0];
        Assert.Equal("User", audit.Actor);
        Assert.Equal(CreateTime.UtcTicks, audit.UtcTicks);
        Assert.Equal("identified", audit.TargetKind);
        Assert.Equal(draft.InternalId.ToString(), audit.Target);
        Assert.Equal("success", audit.Outcome);
        Assert.Equal(new SpecificationAuditDetails(draft.InternalId, null,
            new SpecificationAuditValues(product.InternalId, "DEV-42", "A title", markdown, "Draft")), audit.Change);
    }

    [PostgresFact]
    public async Task SpecificationCreateRejectsInvalidDuplicateRetiredAndMissingWithoutAuditing()
    {
        await using var db = await Database.Create();
        var products = new PostgresProductStore(db.ConnectionString);
        var product = await Product(products, "first");
        var retired = await Product(products, "retired", retired: true);
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var principal = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        Assert.True((await operations.Create(principal, product.InternalId, "DEV-42", "Original", null)).IsSuccess);
        foreach (var (id, title, description) in new (string?, string?, string?)[]
        {
            (" dev-42 ", "Duplicate", null), (null, "Title", null), ("  ", "Title", null), ("NO-TITLE", "\t", null),
            ("OVERSIZE", "Title", new string('é', 524288) + "!"), ("NUL", "Title", "\0"), ("UNICODE", "\uD800", null)
        })
            Assert.True((await operations.Create(principal, product.InternalId, id, title, description)).IsFailure);
        foreach (var unavailable in new[] { retired.InternalId, Guid.NewGuid() })
        {
            Assert.Contains("retired or no longer exists", (await operations.Create(principal, unavailable, "NEW", "Title", null)).Error);
            Assert.Empty((await operations.List(principal, unavailable)).Value);
        }
        Assert.Equal("Original", Assert.Single((await operations.List(principal, product.InternalId)).Value).Title);
        Assert.Single(await CreateEvents(db));
    }

    [PostgresFact]
    public async Task SpecificationCreateRejectsIdsThatExceedPostgresIndexLimitsWithoutAuditing()
    {
        await using var db = await Database.Create();
        var product = await Product(new PostgresProductStore(db.ConnectionString), "first");
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var principal = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        // Varied ASCII exercises both PostgreSQL index limits; repeated characters can be compressed.
        var id = string.Concat(Enumerable.Range(0, 128).Select(i => Convert.ToHexString(SHA256.HashData(BitConverter.GetBytes(i)))));
        foreach (var length in new[] { 4096, 8192 })
        {
            var failed = await operations.Create(principal, product.InternalId, id[..length], "Private title", "Private description");
            Assert.Equal("The Specification ID is too large to store.", failed.Error);
        }

        Assert.Empty((await operations.List(principal, product.InternalId)).Value);
        var valid = (await operations.Create(principal, product.InternalId, "DEV-1", "Title", null)).Value;
        Assert.Equal(valid.InternalId.ToString(), Assert.Single(await CreateEvents(db)).Target);
    }

    [PostgresFact]
    public async Task SpecificationCreateDoesNotTreatAnUnrelatedPostgresProgramLimitAsInvalidInput()
    {
        await using var db = await Database.Create();
        var product = await Product(new PostgresProductStore(db.ConnectionString), "first");
        var reads = new PostgresSpecificationReadStore(db.ConnectionString);
        Assert.Empty((await reads.List(product.InternalId)).Value);
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var fault = new NpgsqlCommand("""
            CREATE FUNCTION public.fail_specification_insert() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE program_limit_exceeded USING MESSAGE = 'private provider details'; END $$;
            CREATE TRIGGER fail_insert BEFORE INSERT ON retravion_specification.specification
            FOR EACH ROW EXECUTE FUNCTION public.fail_specification_insert();
            """, connection);
        await fault.ExecuteNonQueryAsync();
        var draft = DraftSpecification.Create(product.InternalId, "PRIVATE-ID", "PRIVATE-TITLE", "SECRET").Value;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PostgresSpecificationWriteStore(db.ConnectionString).Create(draft, CreateActor, CreateTime, () => Task.FromResult(true)));

        Assert.Equal("An unexpected Specification storage error occurred.", exception.Message);
        Assert.Null(exception.InnerException);
        Assert.Empty((await reads.List(product.InternalId)).Value);
    }

    [PostgresFact]
    public async Task SpecificationCreateRollsBackOnAuditFailure()
    {
        await using var db = await Database.Create();
        var product = await Product(new PostgresProductStore(db.ConnectionString), "first");
        var reads = new PostgresSpecificationReadStore(db.ConnectionString);
        Assert.Empty((await reads.List(product.InternalId)).Value);
        await InstallAuditFault(db, "RAISE insufficient_privilege USING MESSAGE = 'private provider details';");
        var writes = new PostgresSpecificationWriteStore(db.ConnectionString);
        var draft = DraftSpecification.Create(product.InternalId, "PRIVATE-ID", "PRIVATE-TITLE", "SECRET").Value;
        var failed = await writes.Create(draft, CreateActor, CreateTime, () => Task.FromResult(true));
        Assert.Equal("The Specification operation could not be completed.", failed.Error);
        Assert.Empty((await reads.List(product.InternalId)).Value);
        Assert.Empty(await CreateEvents(db));
    }

    [PostgresFact]
    public async Task SpecificationCreateRollsBackAndSurfacesUnexpectedSchemaFaultWithoutProviderDetails()
    {
        await using var db = await Database.Create();
        var product = await Product(new PostgresProductStore(db.ConnectionString), "first");
        // Fail inside audit submission, after the Draft has been inserted in the same transaction.
        await InstallAuditFault(db, "PERFORM private_provider_details FROM retravion_audit.event;");
        var writes = new PostgresSpecificationWriteStore(db.ConnectionString);
        var draft = DraftSpecification.Create(product.InternalId, "PRIVATE-ID", "PRIVATE-TITLE", "SECRET").Value;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writes.Create(draft, CreateActor, CreateTime, () => Task.FromResult(true)));

        Assert.Equal("An unexpected Specification storage error occurred.", exception.Message);
        Assert.Null(exception.InnerException);
        Assert.Empty((await new PostgresSpecificationReadStore(db.ConnectionString).List(product.InternalId)).Value);
        Assert.Empty(await CreateEvents(db));
    }

    private static async Task InstallAuditFault(Database db, string triggerStatement)
    {
        var submission = new PostgresAuditSubmission(db.ConnectionString);
        Assert.Equal(AuditSubmissionResult.Success, await submission.SubmitAsync(new AuditInformation(CreateActor,
            CreateTime, "test.baseline", new AuditTarget.Unknown(), AuditActionOutcome.Success)));
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var fail = new NpgsqlCommand($"""
            CREATE FUNCTION public.fail_specification_audit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN {triggerStatement} RETURN NEW; END $$;
            CREATE TRIGGER fail_audit BEFORE INSERT ON retravion_audit.event
            FOR EACH ROW EXECUTE FUNCTION public.fail_specification_audit();
            """, connection);
        await fail.ExecuteNonQueryAsync();
    }

    [PostgresFact]
    public async Task SpecificationCreateRollsBackAndAuditsDenialIfSessionExpiresBeforeCommit()
    {
        await using var db = await Database.Create();
        var product = await Product(new PostgresProductStore(db.ConnectionString), "first");
        var clock = new CreateClock();
        var writes = new ExpiringWriteStore(new PostgresSpecificationWriteStore(db.ConnectionString), clock);
        await using var factory = CreateFactory(db, clock, writes);
        using var scope = factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var principal = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);

        Assert.Equal("Access denied.", (await operations.Create(principal, product.InternalId, "PRIVATE-ID", "PRIVATE-TITLE", "SECRET")).Error);
        Assert.Equal(2, writes.AccessChecks);
        Assert.Empty((await new PostgresSpecificationReadStore(db.ConnectionString).List(product.InternalId)).Value);
        var audit = Assert.Single(await CreateEvents(db));
        Assert.Equal(new CreateEvent("User", CreateTime.UtcTicks, "unknown", null, "failure", null), audit);
    }

    [PostgresFact]
    public async Task SpecificationCreateAuditsRevokedAndUnauthorizedCallsAndLogsDenialFailuresSafely()
    {
        await using var db = await Database.Create();
        var product = await Product(new PostgresProductStore(db.ConnectionString), "first");
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var sessions = services.GetRequiredService<SessionTicketStore>();
        var operations = services.GetRequiredService<SpecificationOperations>();
        var revoked = await Principal(sessions, "User", now: CreateTime);
        await sessions.RemoveAsync(revoked.FindFirstValue(SessionTicketStore.SessionClaim)!);
        var wrongRole = await Principal(sessions, "Other", now: CreateTime);
        var restricted = await Principal(sessions, "User", restricted: true, now: CreateTime);
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        foreach (var principal in new[] { revoked, wrongRole, restricted, anonymous })
            Assert.Equal("Access denied.", (await operations.Create(principal, product.InternalId, "PRIVATE-ID", "PRIVATE-TITLE", "SECRET")).Error);
        var events = await CreateEvents(db);
        Assert.Equal(new string?[] { "User", "Other", "User", null }, events.Select(e => e.Actor));
        Assert.All(events, audit =>
        {
            Assert.Equal(CreateTime.UtcTicks, audit.UtcTicks);
            Assert.Equal("failure", audit.Outcome);
            Assert.Equal("unknown", audit.TargetKind);
            Assert.Null(audit.Target);
            Assert.Null(audit.Change);
        });

        var reads = new PostgresSpecificationReadStore(db.ConnectionString);
        foreach (var throws in new[] { false, true })
        {
            var logger = new CreateLogger();
            var fault = new DenialFault(throws);
            var failing = new SpecificationOperations(services.GetRequiredService<ProductOperations>(), reads,
                new PostgresSpecificationWriteStore(db.ConnectionString), sessions, fault, new CreateClock(), logger);
            Assert.Equal("Access denied.", (await failing.Create(revoked, product.InternalId, "PRIVATE-ID", "PRIVATE-TITLE", "SECRET")).Error);
            Assert.Equal(1, fault.Calls);
            Assert.Equal((LogLevel.Error, "Unauthorized Specification change audit submission failed.", (Exception?)null),
                Assert.Single(logger.Entries));
        }
        Assert.Empty((await reads.List(product.InternalId)).Value);
        Assert.Equal(4, (await CreateEvents(db)).Count);
    }

    [PostgresFact]
    public async Task SpecificationCreateReportsUncertainCommitWithOrWithoutDurableOutcomeAndNeverRetries()
    {
        await using var db = await Database.Create();
        var product = await Product(new PostgresProductStore(db.ConnectionString), "first");
        var reads = new PostgresSpecificationReadStore(db.ConnectionString);
        foreach (var committed in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            var attempts = 0;
            var writes = new PostgresSpecificationWriteStore(db.ConnectionString, async (transaction, token) =>
            {
                attempts++;
                if (committed)
                {
                    await transaction.CommitAsync(token);
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                }
                throw new IOException("private provider details");
            });
            var draft = DraftSpecification.Create(product.InternalId, $"DEV-{committed}", "Title", null).Value;
            var result = await writes.Create(draft, CreateActor, CreateTime, () => Task.FromResult(true), cancellation.Token);
            Assert.Contains("outcome could not be confirmed", result.Error);
            Assert.DoesNotContain("private", result.Error);
            Assert.Equal(1, attempts);
            Assert.Equal(committed, (await reads.Find(product.InternalId, draft.InternalId)).Value.HasValue);
        }
        var audit = Assert.Single(await CreateEvents(db));
        Assert.Equal(Assert.Single((await reads.List(product.InternalId)).Value).InternalId.ToString(), audit.Target);
    }

    [PostgresFact]
    public async Task SpecificationCreateHoldsTheActiveProductLockThroughAuditAndCommit()
    {
        await using var db = await Database.Create();
        var products = new PostgresProductStore(db.ConnectionString);
        var product = await Product(products, "first");
        // Initialize the schema first so only the row lock, not first-use DDL, competes with retirement.
        var reads = new PostgresSpecificationReadStore(db.ConnectionString);
        Assert.Empty((await reads.List(product.InternalId)).Value);
        PostgresException? retirementLockConflict = null;
        var writes = new PostgresSpecificationWriteStore(db.ConnectionString, async (transaction, token) =>
        {
            await using var connection = new NpgsqlConnection(db.ConnectionString);
            await connection.OpenAsync(token);
            await using var retirementLock = new NpgsqlCommand("""
                SELECT is_active FROM retravion_product.product WHERE internal_id = $1 FOR NO KEY UPDATE NOWAIT
                """, connection);
            retirementLock.Parameters.AddWithValue(product.InternalId);
            try { await retirementLock.ExecuteScalarAsync(token); }
            catch (PostgresException exception) { retirementLockConflict = exception; }
            await transaction.CommitAsync(token);
        });
        var draft = DraftSpecification.Create(product.InternalId, "DEV-1", "Title", null).Value;
        var result = await writes.Create(draft, CreateActor, CreateTime, () => Task.FromResult(true));
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, retirementLockConflict?.SqlState);
        Assert.True((await products.Update(product, product.Retire().Value)).IsSuccess);
        var second = DraftSpecification.Create(product.InternalId, "DEV-2", "Title", null).Value;
        Assert.Contains("retired or no longer exists", (await writes.Create(second, CreateActor, CreateTime, () => Task.FromResult(true))).Error);
        Assert.Equal(draft, Assert.Single((await reads.List(product.InternalId)).Value));
        Assert.Single(await CreateEvents(db));
    }

    private static WebApplicationFactory<Program> CreateFactory(Database db, CreateClock? clock = null,
        ISpecificationWriteStore? writes = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = db.ConnectionString,
                    ["InitialAdmin:Username"] = "specification-test-admin",
                    ["InitialAdmin:Password"] = "SpecificationTest123!"
                }));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(clock ?? new CreateClock());
                if (writes is not null) services.AddSingleton(writes);
            });
        });

    private sealed class CreateClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = CreateTime;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ExpiringWriteStore(ISpecificationWriteStore inner, CreateClock clock) : ISpecificationWriteStore
    {
        public int AccessChecks { get; private set; }

        public Task<Result<DraftSpecification>> Create(DraftSpecification draft,
            AuditActor.Identified actor, DateTimeOffset occurredAt, Func<Task<bool>> authorized,
            CancellationToken cancellationToken = default) =>
            inner.Create(draft, actor, occurredAt, () =>
            {
                // Leave access intact until the real store has staged both the Draft and its success audit.
                if (++AccessChecks == 2) clock.Now = CreateTime.AddMinutes(11);
                return authorized();
            }, cancellationToken);

        public Task<Result<DraftSpecification>> EditContent(Guid productInternalId, Guid internalId, string? id,
            string? title, string? description, AuditActor.Identified actor, DateTimeOffset occurredAt, Func<Task<bool>> authorized,
            CancellationToken cancellationToken = default) =>
            inner.EditContent(productInternalId, internalId, id, title, description, actor, occurredAt, authorized, cancellationToken);

        public Task<Result<DraftSpecification>> EditLinks(Guid productInternalId, Guid internalId, IEnumerable<SpecificationLink> targets,
            AuditActor.Identified actor, DateTimeOffset occurredAt, Func<Task<bool>> authorized,
            CancellationToken cancellationToken = default) =>
            inner.EditLinks(productInternalId, internalId, targets, actor, occurredAt, authorized, cancellationToken);

        public Task<Result> ChangeAssignment(Guid productInternalId, Guid internalId, string? assignee,
            AuditActor.Identified actor, DateTimeOffset occurredAt, Func<Task<bool>> authorized,
            CancellationToken cancellationToken = default) =>
            inner.ChangeAssignment(productInternalId, internalId, assignee, actor, occurredAt, authorized, cancellationToken);

        public Task<Result<SpecificationAiReview>> SubmitAiReview(Guid productInternalId, Guid internalId, SpecificationAiReviewInput input,
            bool userApproved, AuditActor.Identified actor, DateTimeOffset occurredAt, Func<Task<bool>> authorized,
            CancellationToken cancellationToken = default) =>
            inner.SubmitAiReview(productInternalId, internalId, input, userApproved, actor, occurredAt, authorized, cancellationToken);

        public Task<Result<InReviewSpecification>> SubmitForReview(Guid productInternalId, Guid internalId, AuditActor.Identified actor,
            DateTimeOffset occurredAt, Func<Task<bool>> authorized, CancellationToken cancellationToken = default) =>
            inner.SubmitForReview(productInternalId, internalId, actor, occurredAt, authorized, cancellationToken);

        public Task<Result<Retravion.Application.Specification.SpecificationApproval>> Approve(Guid productInternalId, Guid internalId,
            AuditActor.Identified actor, DateTimeOffset occurredAt, Func<Task<bool>> authorized, CancellationToken cancellationToken = default) =>
            inner.Approve(productInternalId, internalId, actor, occurredAt, authorized, cancellationToken);

        public Task<Result<DraftSpecification>> Reopen(Guid productInternalId, Guid internalId, string? reason,
            bool noActiveOrUncertainRunAttested, AuditActor.Identified actor, DateTimeOffset occurredAt,
            Func<Task<bool>> authorized, CancellationToken cancellationToken = default) =>
            inner.Reopen(productInternalId, internalId, reason, noActiveOrUncertainRunAttested, actor, occurredAt, authorized, cancellationToken);

        public Task<Result<DraftSpecification>> ReturnToDraft(Guid productInternalId, Guid internalId, string? reason,
            AuditActor.Identified actor, DateTimeOffset occurredAt, Func<Task<bool>> authorized,
            CancellationToken cancellationToken = default) =>
            inner.ReturnToDraft(productInternalId, internalId, reason, actor, occurredAt, authorized, cancellationToken);
    }

    private static Task<List<CreateEvent>> CreateEvents(Database db) => SpecificationEvents(db, "specification.create");

    private static async Task<List<CreateEvent>> SpecificationEvents(Database db, string action)
    {
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var query = new NpgsqlCommand("""
            SELECT actor_identifier, occurred_at_utc_ticks, target_kind, target_identifier, outcome, specification_change
            FROM retravion_audit.event WHERE action = $1 ORDER BY id
            """, connection);
        query.Parameters.AddWithValue(action);
        await using var reader = await query.ExecuteReaderAsync();
        var events = new List<CreateEvent>();
        while (await reader.ReadAsync())
            events.Add(new(reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetInt64(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : JsonSerializer.Deserialize<SpecificationAuditDetails>(reader.GetString(5))));
        return events;
    }

    private sealed record CreateEvent(string? Actor, long UtcTicks, string TargetKind, string? Target,
        string Outcome, SpecificationAuditDetails? Change);

    private sealed class DenialFault(bool throws) : IAuditSubmission
    {
        public int Calls { get; private set; }

        public Task<AuditSubmissionResult> SubmitAsync(AuditInformation information, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (throws) throw new InvalidOperationException("PRIVATE-ID SECRET provider details");
            return Task.FromResult(AuditSubmissionResult.Failure);
        }
    }

    private sealed class CreateLogger : ILogger<SpecificationOperations>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
