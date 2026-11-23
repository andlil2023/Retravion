using CSharpFunctionalExtensions;
using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Application.Specification;
using Retravion.Domain.Specification;
using Retravion.Infrastructure.AuditTrail;

namespace Retravion.Infrastructure.Specification;

public sealed class PostgresSpecificationWriteStore(string connectionString,
    Func<NpgsqlTransaction, CancellationToken, Task>? commit = null) : ISpecificationWriteStore
{
    private const string ProductIdIndex = "specification_product_id_unique";
    private const string SubmitForReviewAction = "specification.submit-for-review";
    private const string ReturnToDraftAction = "specification.return-to-draft";

    public Task<Result<DraftSpecification>> Create(DraftSpecification draft, AuditActor.Identified actor, DateTimeOffset occurredAt,
        Func<Task<bool>> authorized, CancellationToken cancellationToken = default) =>
        Execute(draft.ProductInternalId, authorized, async (connection, transaction) =>
        {
            if (draft.Links.Targets.Count != 0)
                return Result.Failure<DraftSpecification>("Set links after creating the Draft Specification.");
            await using var insert = new NpgsqlCommand("""
                INSERT INTO retravion_specification.specification (internal_id, product_internal_id, id, title, description, status)
                VALUES ($1, $2, $3, $4, $5, 'Draft')
                """, connection, transaction);
            insert.Parameters.AddWithValue(draft.InternalId);
            insert.Parameters.AddWithValue(draft.ProductInternalId);
            insert.Parameters.AddWithValue(draft.Id);
            insert.Parameters.AddWithValue(draft.Title);
            insert.Parameters.AddWithValue(draft.Description);
            var saved = await SaveContent(insert, cancellationToken);
            if (saved.IsFailure) return Result.Failure<DraftSpecification>(saved.Error);

            await AuditChange(connection, transaction, actor, occurredAt, "specification.create", draft.InternalId, null,
                Snapshot(draft), cancellationToken);
            return Result.Success(draft);
        }, cancellationToken);

    public Task<Result<DraftSpecification>> EditContent(Guid productInternalId, Guid internalId, string? id, string? title,
        string? description, AuditActor.Identified actor, DateTimeOffset occurredAt, Func<Task<bool>> authorized,
        CancellationToken cancellationToken = default) =>
        Execute(productInternalId, authorized, async (connection, transaction) =>
        {
            var current = await LockDraft(connection, transaction, productInternalId, internalId, "content", cancellationToken);
            if (current.IsFailure) return current;
            var before = Snapshot(current.Value);
            var edit = current.Value.EditContent(id, title, description);
            if (edit.IsFailure) return edit;
            var after = Snapshot(edit.Value);
            if (before == after) return edit;

            await using var update = new NpgsqlCommand("""
                UPDATE retravion_specification.specification SET id = $3, title = $4, description = $5
                WHERE product_internal_id = $1 AND internal_id = $2 AND status = 'Draft'
                """, connection, transaction);
            update.Parameters.AddWithValue(productInternalId);
            update.Parameters.AddWithValue(internalId);
            update.Parameters.AddWithValue(after.Id);
            update.Parameters.AddWithValue(after.Title);
            update.Parameters.AddWithValue(after.Description);
            var saved = await SaveContent(update, cancellationToken);
            if (saved.IsFailure) return Result.Failure<DraftSpecification>(saved.Error);

            await AuditChange(connection, transaction, actor, occurredAt, "specification.edit-content", internalId, before, after,
                cancellationToken);
            return edit;
        }, cancellationToken);

    public Task<Result<DraftSpecification>> EditLinks(Guid productInternalId, Guid internalId, IEnumerable<SpecificationLink> targets,
        AuditActor.Identified actor, DateTimeOffset occurredAt, Func<Task<bool>> authorized,
        CancellationToken cancellationToken = default) =>
        Execute(productInternalId, authorized, async (connection, transaction) =>
        {
            var current = await LockDraft(connection, transaction, productInternalId, internalId, "links", cancellationToken);
            if (current.IsFailure) return current;
            var edit = current.Value.EditLinks(targets);
            if (edit.IsFailure || current.Value.Links.Equals(edit.Value.Links)) return edit;

            foreach (var target in edit.Value.Links.Targets)
            {
                var found = await PostgresSpecificationVersionSource.For(target.Kind)
                    .Resolve(connection, transaction, productInternalId, target.VersionId, cancellationToken);
                if (found.HasNoValue)
                    return Result.Failure<DraftSpecification>($"The selected {target.Kind} version was not found in this Product.");
            }

            await using (var remove = new NpgsqlCommand("""
                DELETE FROM retravion_specification.link WHERE specification_internal_id = $1
                """, connection, transaction))
            {
                remove.Parameters.AddWithValue(internalId);
                await remove.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var insert = new NpgsqlCommand("""
                INSERT INTO retravion_specification.link (specification_internal_id, kind, version_id)
                SELECT $1, kind, version_id FROM unnest($2::smallint[], $3::uuid[]) AS targets(kind, version_id)
                """, connection, transaction))
            {
                insert.Parameters.AddWithValue(internalId);
                insert.Parameters.AddWithValue(edit.Value.Links.Targets.Select(link => (short)link.Kind).ToArray());
                insert.Parameters.AddWithValue(edit.Value.Links.Targets.Select(link => link.VersionId).ToArray());
                if (await insert.ExecuteNonQueryAsync(cancellationToken) != edit.Value.Links.Targets.Count)
                    return Result.Failure<DraftSpecification>("The Specification links could not be saved.");
            }

            await AuditChange(connection, transaction, actor, occurredAt, "specification.edit-links", internalId,
                LinkSnapshot(current.Value), LinkSnapshot(edit.Value), cancellationToken);
            return edit;
        }, cancellationToken);

    public Task<Result<InReviewSpecification>> SubmitForReview(Guid productInternalId, Guid internalId, AuditActor.Identified actor,
        DateTimeOffset occurredAt, Func<Task<bool>> authorized, CancellationToken cancellationToken = default) =>
        Transition<DraftSpecification, InReviewSpecification>(productInternalId, internalId, actor, occurredAt, authorized,
            SubmitForReviewAction, "Draft", "In Review", "Only Draft Specifications can be submitted for review.",
            draft => draft.SubmitForReview(), null, cancellationToken);

    public Task<Result<DraftSpecification>> ReturnToDraft(Guid productInternalId, Guid internalId, string? reason,
        AuditActor.Identified actor, DateTimeOffset occurredAt, Func<Task<bool>> authorized,
        CancellationToken cancellationToken = default) =>
        Transition<InReviewSpecification, DraftSpecification>(productInternalId, internalId, actor, occurredAt, authorized,
            ReturnToDraftAction, "In Review", "Draft", "Only In Review Specifications can be returned to Draft.",
            review => review.ReturnToDraft(reason), reason, cancellationToken);

    private Task<Result<TNext>> Transition<TCurrent, TNext>(Guid productId, Guid internalId, AuditActor.Identified actor,
        DateTimeOffset occurredAt, Func<Task<bool>> authorized, string action, string before, string after, string invalidState,
        Func<TCurrent, Result<TNext>> transition, string? reason, CancellationToken token)
        where TCurrent : DevelopmentSpecification where TNext : DevelopmentSpecification =>
        Execute(productId, authorized, async (connection, transaction) =>
        {
            var current = await LockSpecification<TCurrent>(connection, transaction, productId, internalId, before, invalidState, token);
            if (current.IsFailure) return Result.Failure<TNext>(current.Error);
            var next = transition(current.Value);
            if (next.IsFailure) return next;

            await using var update = new NpgsqlCommand("""
                UPDATE retravion_specification.specification SET status = $3
                WHERE product_internal_id = $1 AND internal_id = $2 AND status = $4
                """, connection, transaction);
            update.Parameters.AddWithValue(productId);
            update.Parameters.AddWithValue(internalId);
            update.Parameters.AddWithValue(after);
            update.Parameters.AddWithValue(before);
            if (await update.ExecuteNonQueryAsync(token) != 1)
                return Result.Failure<TNext>("The Specification state could not be saved.");

            await AuditChange(connection, transaction, actor, occurredAt, action, internalId,
                Snapshot(current.Value, before), Snapshot(next.Value, after), token, reason);
            return next;
        }, token);

    private static Task<Result<DraftSpecification>> LockDraft(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid productId, Guid internalId, string field, CancellationToken token) =>
        LockSpecification<DraftSpecification>(connection, transaction, productId, internalId, "Draft",
            $"Only Draft Specification {field} can be edited.", token);

    private static async Task<Result<T>> LockSpecification<T>(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid productId, Guid internalId, string expectedStatus, string invalidState, CancellationToken token)
        where T : DevelopmentSpecification
    {
        await using (var locked = new NpgsqlCommand("""
            SELECT status FROM retravion_specification.specification
            WHERE product_internal_id = $1 AND internal_id = $2 FOR UPDATE
            """, connection, transaction))
        {
            locked.Parameters.AddWithValue(productId);
            locked.Parameters.AddWithValue(internalId);
            if (await locked.ExecuteScalarAsync(token) is not string status)
                return Result.Failure<T>("The Specification was not found in this Product.");
            if (status != expectedStatus) return Result.Failure<T>(invalidState);
        }

        // Read child rows in a new statement after acquiring the lock, including any just-committed link edits.
        await using var read = new NpgsqlCommand(PostgresSpecificationReadStore.Select + " AND internal_id = $2", connection, transaction);
        read.Parameters.AddWithValue(productId);
        read.Parameters.AddWithValue(internalId);
        await using var reader = await read.ExecuteReaderAsync(token);
        if (await reader.ReadAsync(token))
        {
            var mapped = PostgresSpecificationReadStore.Map(reader);
            if (mapped.IsSuccess && mapped.Value is T current) return Result.Success(current);
        }
        return Result.Failure<T>("The persisted Specification is invalid.");
    }

    private static SpecificationAuditValues LinkSnapshot(DraftSpecification draft) => Snapshot(draft) with
    {
        Links = draft.Links.Targets.Select(link => new SpecificationAuditLink(link.Kind.ToString(), link.VersionId)).ToArray()
    };

    private static SpecificationAuditValues Snapshot(DraftSpecification draft) => Snapshot(draft, "Draft");

    private static SpecificationAuditValues Snapshot(DevelopmentSpecification specification, string status) =>
        new(specification.ProductInternalId, specification.Id, specification.Title, specification.Description, status);

    private static Task AuditChange(NpgsqlConnection connection, NpgsqlTransaction transaction, AuditActor.Identified actor,
        DateTimeOffset occurredAt, string action, Guid internalId, SpecificationAuditValues? before, SpecificationAuditValues after,
        CancellationToken cancellationToken, string? reason = null) =>
        PostgresAuditSubmission.SubmitInTransactionAsync(connection, transaction,
            new AuditInformation(actor, occurredAt, action, new AuditTarget.Identified(internalId.ToString()),
                AuditActionOutcome.Success, specificationChange: new SpecificationAuditDetails(internalId, before, after, reason)),
            cancellationToken);

    private static async Task<Result> SaveContent(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1
                ? Result.Success() : Result.Failure("The Specification operation could not be completed.");
        }
        catch (PostgresException exception) when (!cancellationToken.IsCancellationRequested && IsUnindexableId(exception))
        {
            return Result.Failure("The Specification ID is too large to store.");
        }
    }

    // The ID is the only variable-width index key on a content write. PostgreSQL may reject
    // the tuple before the B-tree size check, in which case it does not report an index name.
    private static bool IsUnindexableId(PostgresException exception) =>
        exception.SqlState == PostgresErrorCodes.ProgramLimitExceeded && exception is
            ({ Routine: "_bt_check_third_page", ConstraintName: ProductIdIndex } or { Routine: "index_form_tuple_context" });

    private async Task<Result<T>> Execute<T>(Guid productInternalId, Func<Task<bool>> authorized,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<Result<T>>> change, CancellationToken cancellationToken)
    {
        if (!await authorized()) return Result.Failure<T>("Access denied.");
        var commitStarted = false;
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await PostgresSpecificationReadStore.EnsureSchema(connection, transaction, cancellationToken);

            // Product owns the active flag. Hold a row lock through commit so retirement cannot race a write.
            await using (var product = new NpgsqlCommand("""
                SELECT is_active FROM retravion_product.product WHERE internal_id = $1 FOR SHARE
                """, connection, transaction))
            {
                product.Parameters.AddWithValue(productInternalId);
                if (await product.ExecuteScalarAsync(cancellationToken) is not true)
                    return Result.Failure<T>("The responsible Product is retired or no longer exists.");
            }

            var result = await change(connection, transaction);
            if (result.IsFailure) return result;
            if (!await authorized()) return Result.Failure<T>("Access denied.");
            cancellationToken.ThrowIfCancellationRequested();
            commitStarted = true;
            if (commit is null) await transaction.CommitAsync(cancellationToken);
            else await commit(transaction, cancellationToken);
            return result;
        }
        catch (Exception) when (commitStarted)
        {
            return Result.Failure<T>("The Specification operation outcome could not be confirmed. Check the current records before retrying; do not retry automatically.");
        }
        catch (PostgresException exception) when (!cancellationToken.IsCancellationRequested &&
            exception.SqlState == PostgresErrorCodes.UniqueViolation && exception.ConstraintName == ProductIdIndex)
        {
            return Result.Failure<T>("That Specification ID is already in use in this Product.");
        }
        catch (NpgsqlException exception) when (!cancellationToken.IsCancellationRequested &&
            (exception.IsTransient || exception is PostgresException
            {
                SqlState: PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.ReadOnlySqlTransaction
            }))
        {
            return Result.Failure<T>("The Specification operation could not be completed.");
        }
        catch (NpgsqlException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Surface unexpected faults after rollback without retaining provider details or submitted values.
            throw new InvalidOperationException("An unexpected Specification storage error occurred.");
        }
    }
}
