using CSharpFunctionalExtensions;
using Npgsql;
using Retravion.Application.Product;
using ProductState = Retravion.Domain.Product.Product;

namespace Retravion.Infrastructure.Product;

public sealed class PostgresProductStore(string connectionString) : IProductStore
{
    public async Task<Result> Create(ProductState product, CancellationToken cancellationToken = default)
    {
        var result = await Execute(async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO retravion_product.product (id, name, description, is_active)
                VALUES ($1, $2, $3, $4) ON CONFLICT (id) DO NOTHING
                """, connection, transaction);
            command.Parameters.AddWithValue(product.Id);
            command.Parameters.AddWithValue(product.Name);
            command.Parameters.AddWithValue(product.Description);
            command.Parameters.AddWithValue(product.IsActive);
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1
                ? Result.Success(true) : Result.Failure<bool>("That Product ID is already in use.");
        }, cancellationToken);
        return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
    }

    public Task<Result<Maybe<ProductState>>> Find(string id, CancellationToken cancellationToken = default) =>
        Execute(async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                SELECT id, name, description, is_active FROM retravion_product.product WHERE id = $1
                """, connection, transaction);
            command.Parameters.AddWithValue(id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return Result.Success(Maybe<ProductState>.None);
            var product = ProductState.Restore(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3));
            return product.IsSuccess ? Result.Success(Maybe<ProductState>.From(product.Value))
                : Result.Failure<Maybe<ProductState>>("The persisted Product is invalid.");
        }, cancellationToken);

    public async Task<Result> ChangeStatus(ProductState expected, ProductState replacement,
        CancellationToken cancellationToken = default)
    {
        if (expected.Id != replacement.Id || expected.Name != replacement.Name ||
            expected.Description != replacement.Description || expected.IsActive == replacement.IsActive)
            return Result.Failure("Invalid Product status change.");
        var result = await Execute(async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                UPDATE retravion_product.product SET is_active = $1
                WHERE id = $2 AND is_active = $3
                """, connection, transaction);
            command.Parameters.AddWithValue(replacement.IsActive);
            command.Parameters.AddWithValue(expected.Id);
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
            // Match the existing first-use schema strategy; serialize DDL across hosts.
            await using (var setup = new NpgsqlCommand("""
                SELECT pg_advisory_xact_lock(821749620110);
                CREATE SCHEMA IF NOT EXISTS retravion_product;
                CREATE TABLE IF NOT EXISTS retravion_product.product (
                    id text COLLATE "C" PRIMARY KEY CHECK (length(btrim(id)) > 0 AND char_length(id) <= 100),
                    name text NOT NULL CHECK (length(btrim(name)) > 0),
                    description text NOT NULL,
                    is_active boolean NOT NULL DEFAULT TRUE
                );
                """, connection, transaction))
                await setup.ExecuteNonQueryAsync(cancellationToken);
            var result = await operation(connection, transaction);
            if (result.IsSuccess) await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (NpgsqlException exception) when (!cancellationToken.IsCancellationRequested &&
            (exception.IsTransient || exception is PostgresException
            {
                SqlState: PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.ReadOnlySqlTransaction
            }))
        {
            // Do not retry writes: a lost commit acknowledgement may mean a write succeeded.
            return Result.Failure<T>("The Product operation could not be confirmed. Retrieve the Product before retrying.");
        }
    }
}
