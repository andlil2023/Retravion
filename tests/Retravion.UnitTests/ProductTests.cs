using Retravion.Application.Product;
using Retravion.Application.Product.Create;
using Retravion.Application.Product.Description;
using Retravion.Application.Product.Retrieve;
using Retravion.Application.Product.Status;
using CSharpFunctionalExtensions;
using Xunit;
using ProductState = Retravion.Domain.Product.Product;

namespace Retravion.UnitTests;

public sealed class ProductTests
{
    [Theory]
    [InlineData(null, "Name")]
    [InlineData("", "Name")]
    [InlineData(" \t", "Name")]
    [InlineData("id", null)]
    [InlineData("id", "")]
    [InlineData("id", " \t\n")]
    public async Task MissingIdOrNameRejectsCreationWithoutPersistence(string? id, string? name)
    {
        var store = new Store();
        Assert.True((await new CreateProduct(store).Execute(id, name, null)).IsFailure);
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("😀")]
    public async Task IdLimitCountsUnicodeScalarsAndRejectsOversizedIdsBeforePersistence(string character)
    {
        var boundary = string.Concat(Enumerable.Repeat(character, 100));
        Assert.True(ProductState.Create(boundary, "Name", "").IsSuccess);
        Assert.True(ProductState.Restore(boundary, "Name", "", false).IsSuccess);
        var oversized = boundary + character;
        Assert.True(ProductState.Restore(oversized, "Name", "", true).IsFailure);
        var store = new Store();
        var result = await new CreateProduct(store).Execute(oversized, "Name", "");
        Assert.True(result.IsFailure);
        Assert.Equal("A Product ID must contain at most 100 Unicode characters.", result.Error);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public void ImmutableProductHasExplicitTransitionsAndValidatesRestoration()
    {
        var active = ProductState.Create("custom ID", "Name", null).Value;
        Assert.Equal("", active.Description);
        Assert.True(active.IsActive);
        Assert.True(active.Reactivate().IsFailure);
        var retired = active.Retire().Value;
        Assert.True(active.IsActive);
        Assert.False(retired.IsActive);
        Assert.True(retired.Retire().IsFailure);
        Assert.Equal(active, retired.Reactivate().Value);
        Assert.True(ProductState.Restore("id", "Name", null, true).IsFailure);
        Assert.True(ProductState.Restore("id", " ", "", false).IsFailure);
        Assert.All(typeof(ProductState).GetProperties(), property => Assert.Null(property.SetMethod));
    }

    [Fact]
    public async Task ApplicationPreservesFailuresAndAbsenceWithoutStatusWrites()
    {
        var store = new Store();
        var retrieve = new RetrieveProduct(store);
        Assert.Equal("Product not found.", (await retrieve.Execute("missing")).Error);
        Assert.True((await new ChangeProductStatus(store).Execute("missing", false)).IsFailure);
        Assert.Equal(0, store.Writes);
        store.Error = "storage failed";
        Assert.Equal(store.Error, (await retrieve.Execute("id")).Error);
        Assert.Equal(store.Error, (await new CreateProduct(store).Execute("id", "Name", "")).Error);
        store.Error = null;
        store.Product = ProductState.Create("id", "Name", "Description").Value;
        var status = new ChangeProductStatus(store);
        Assert.True((await status.Execute("id", true)).IsFailure);
        Assert.Equal(1, store.Writes); // Only the failed create above reached a write.
        store.WriteError = "status failed";
        Assert.Equal(store.WriteError, (await status.Execute("id", false)).Error);
        Assert.Equal(2, store.Writes);
        Assert.True(store.Product.IsActive);
    }

    [Fact]
    public async Task DescriptionUpdatesPreserveIdentityAndStatusAndPropagateFailures()
    {
        var original = ProductState.Create("id", "Name", "original").Value.Retire().Value;
        var changed = original.UpdateDescription("# Heading\n\n**Formatted**");
        Assert.Equal("original", original.Description);
        Assert.Equal(original.Id, changed.Id);
        Assert.Equal(original.Name, changed.Name);
        Assert.False(changed.IsActive);
        Assert.Equal("", changed.UpdateDescription(null).Description);
        var store = new Store();
        var update = new UpdateProductDescription(store);
        Assert.Equal("Product not found.", (await update.Execute("id", "", "new")).Error);
        Assert.Equal(0, store.Writes);
        store.Error = "read failed";
        Assert.Equal(store.Error, (await update.Execute("id", "", "new")).Error);
        Assert.Equal(0, store.Writes);
        store.Error = null;
        store.Product = original;
        Assert.True((await update.Execute("id", "stale", "new")).IsFailure);
        Assert.Equal(0, store.Writes);
        store.WriteError = "write failed";
        Assert.Equal(store.WriteError, (await update.Execute("id", "original", "new")).Error);
        Assert.Equal(original, store.Product);
        store.WriteError = null;
        var result = await update.Execute("id", "original", changed.Description);
        Assert.True(result.IsSuccess);
        Assert.Equal(changed, result.Value);
        Assert.Equal(changed, store.Product);
    }

    private sealed class Store : IProductStore
    {
        public int Calls;
        public int Writes;
        public string? Error;
        public string? WriteError;
        public ProductState? Product;
        public Task<Result> Create(ProductState product, CancellationToken cancellationToken = default)
        {
            Calls++; Writes++;
            return Task.FromResult(Error is null ? Result.Success() : Result.Failure(Error));
        }
        public Task<Result<Maybe<ProductState>>> Find(string id, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Error is null ? Result.Success(Maybe<ProductState>.From(Product))
                : Result.Failure<Maybe<ProductState>>(Error));
        }
        public Task<Result> UpdateDescription(ProductState expected, ProductState replacement, CancellationToken cancellationToken = default)
        {
            Calls++; Writes++;
            if (WriteError is not null) return Task.FromResult(Result.Failure(WriteError));
            Product = replacement;
            return Task.FromResult(Result.Success());
        }
        public Task<Result> ChangeStatus(ProductState expected, ProductState replacement, CancellationToken cancellationToken = default)
        {
            Calls++; Writes++;
            return Task.FromResult(WriteError is null ? Result.Success() : Result.Failure(WriteError));
        }
    }
}
