using CSharpFunctionalExtensions;
using Npgsql;
using Retravion.Application.Product;
using Retravion.Domain.Product;
using ProductState = Retravion.Domain.Product.Product;

namespace Retravion.Infrastructure.Product;

public sealed class PostgresProductStore(string connectionString) : IProductStore
{
    public async Task<Result> Create(ProductState product, CancellationToken cancellationToken = default)
    {
        var result = await Execute(async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO retravion_product.product (internal_id, id, name, kind, description, is_active)
                VALUES ($1, $2, $3, $4, $5, $6) ON CONFLICT DO NOTHING
                """, connection, transaction);
            command.Parameters.AddWithValue(product.InternalId);
            command.Parameters.AddWithValue(product.Id);
            command.Parameters.AddWithValue(product.Name);
            command.Parameters.AddWithValue((int)product.Kind);
            command.Parameters.AddWithValue(product.Description);
            command.Parameters.AddWithValue(product.IsActive);
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1
                ? Result.Success(true) : Result.Failure<bool>("That Product ID is already in use.");
        }, cancellationToken);
        return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
    }

    public Task<Result<IReadOnlyList<ProductState>>> List(CancellationToken cancellationToken = default) =>
        Execute(async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                SELECT internal_id, id, name, kind, description, is_active FROM retravion_product.product ORDER BY id
                """, connection, transaction);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var products = new List<ProductState>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var product = ProductState.Restore(reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                    (ProductKind)reader.GetInt32(3), reader.GetString(4), reader.GetBoolean(5));
                if (product.IsFailure) return Result.Failure<IReadOnlyList<ProductState>>("The persisted Product is invalid.");
                products.Add(product.Value);
            }
            return Result.Success<IReadOnlyList<ProductState>>(products);
        }, cancellationToken);

    public Task<Result<Maybe<ProductState>>> Find(string id, CancellationToken cancellationToken = default) =>
        Execute(async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                SELECT internal_id, id, name, kind, description, is_active FROM retravion_product.product
                WHERE id COLLATE retravion_product.product_case_fold = $1 COLLATE retravion_product.product_case_fold
                """, connection, transaction);
            command.Parameters.AddWithValue(id.Trim());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return Result.Success(Maybe<ProductState>.None);
            var product = ProductState.Restore(reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                (ProductKind)reader.GetInt32(3), reader.GetString(4), reader.GetBoolean(5));
            return product.IsSuccess ? Result.Success(Maybe<ProductState>.From(product.Value))
                : Result.Failure<Maybe<ProductState>>("The persisted Product is invalid.");
        }, cancellationToken);

    public async Task<Result> Update(ProductState expected, ProductState replacement, CancellationToken cancellationToken = default)
    {
        if (expected.InternalId != replacement.InternalId ||
            (!expected.IsActive && !replacement.IsActive) ||
            (expected.IsActive && !replacement.IsActive &&
             (expected.Id != replacement.Id || expected.Name != replacement.Name || expected.Kind != replacement.Kind ||
              expected.Description != replacement.Description)))
            return Result.Failure("Invalid Product update.");
        var result = await Execute(async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                UPDATE retravion_product.product SET id = $1, name = $2, kind = $3, description = $4, is_active = $5
                WHERE internal_id = $6 AND id = $7 AND name = $8 AND kind = $9 AND description = $10 AND is_active = $11
                """, connection, transaction);
            command.Parameters.AddWithValue(replacement.Id);
            command.Parameters.AddWithValue(replacement.Name);
            command.Parameters.AddWithValue((int)replacement.Kind);
            command.Parameters.AddWithValue(replacement.Description);
            command.Parameters.AddWithValue(replacement.IsActive);
            command.Parameters.AddWithValue(expected.InternalId);
            command.Parameters.AddWithValue(expected.Id);
            command.Parameters.AddWithValue(expected.Name);
            command.Parameters.AddWithValue((int)expected.Kind);
            command.Parameters.AddWithValue(expected.Description);
            command.Parameters.AddWithValue(expected.IsActive);
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1
                ? Result.Success(true) : Result.Failure<bool>("The Product changed or no longer exists. Retrieve it again.");
        }, cancellationToken);
        return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
    }

    private async Task<Result<T>> Execute<T>(Func<NpgsqlConnection, NpgsqlTransaction, Task<Result<T>>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var setup = new NpgsqlCommand("""
                SELECT pg_advisory_xact_lock(821749620110);
                CREATE SCHEMA IF NOT EXISTS retravion_product;
                CREATE COLLATION IF NOT EXISTS retravion_product.product_case_fold (provider = icu, locale = 'und-u-ks-level2', deterministic = false);
                CREATE TABLE IF NOT EXISTS retravion_product.product (
                    internal_id uuid PRIMARY KEY CHECK (internal_id <> '00000000-0000-0000-0000-000000000000'),
                    id text NOT NULL CHECK (length(btrim(id)) > 0 AND char_length(id) <= 100),
                    name text NOT NULL CHECK (length(btrim(name)) > 0),
                    kind integer NOT NULL CHECK (kind BETWEEN 0 AND 3),
                    description text NOT NULL,
                    is_active boolean NOT NULL DEFAULT TRUE
                );
                CREATE UNIQUE INDEX IF NOT EXISTS product_id_case_insensitive ON retravion_product.product (id COLLATE retravion_product.product_case_fold);
                """, connection, transaction))
                await setup.ExecuteNonQueryAsync(cancellationToken);
            var result = await operation(connection, transaction);
            if (result.IsSuccess) await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (PostgresException exception) when (!cancellationToken.IsCancellationRequested && exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Result.Failure<T>("That Product ID is already in use.");
        }
        catch (NpgsqlException exception) when (!cancellationToken.IsCancellationRequested &&
            (exception.IsTransient || exception is PostgresException
            {
                SqlState: PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.ReadOnlySqlTransaction
            }))
        {
            return Result.Failure<T>("The Product operation could not be confirmed. Retrieve the Product before retrying.");
        }
    }
}
