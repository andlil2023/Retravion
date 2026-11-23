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

    public Task<Result<DraftSpecification>> Create(DraftSpecification draft, AuditActor.Identified actor, DateTimeOffset occurredAt,
        Func<Task<bool>> authorized, CancellationToken cancellationToken = default) =>
        Execute(draft.ProductInternalId, authorized, async (connection, transaction) =>
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO retravion_specification.specification (internal_id, product_internal_id, id, title, description, status)
                VALUES ($1, $2, $3, $4, $5, 'Draft')
                """, connection, transaction);
            insert.Parameters.AddWithValue(draft.InternalId);
            insert.Parameters.AddWithValue(draft.ProductInternalId);
            insert.Parameters.AddWithValue(draft.Id);
            insert.Parameters.AddWithValue(draft.Title);
            insert.Parameters.AddWithValue(draft.Description);
            try
            {
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (PostgresException exception) when (!cancellationToken.IsCancellationRequested && IsUnindexableId(exception))
            {
                return Result.Failure<DraftSpecification>("The Specification ID is too large to store.");
            }

            var values = new SpecificationAuditValues(draft.ProductInternalId, draft.Id, draft.Title, draft.Description, "Draft");
            await PostgresAuditSubmission.SubmitInTransactionAsync(connection, transaction,
                new AuditInformation(actor, occurredAt, "specification.create", new AuditTarget.Identified(draft.InternalId.ToString()),
                    AuditActionOutcome.Success, specificationChange: new SpecificationAuditDetails(draft.InternalId, null, values)),
                cancellationToken);
            return Result.Success(draft);
        }, cancellationToken);

    // The ID is the only variable-width index key on this insert. PostgreSQL may reject
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
