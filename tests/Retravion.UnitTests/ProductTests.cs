using Retravion.Application.Product;
using Retravion.Application.Product.Create;
using Retravion.Application.Product.Description;
using Retravion.Application.Product.Retrieve;
using Retravion.Application.Product.Status;
using Retravion.Application.Product.Edit;
using Retravion.Domain.Product;
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
        Assert.True((await new CreateProduct(store).Execute(id, name, ProductKind.System, null)).IsFailure);
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("😀")]
    public async Task IdLimitCountsUnicodeScalarsAndRejectsOversizedIdsBeforePersistence(string character)
    {
        var boundary = string.Concat(Enumerable.Repeat(character, 100));
        Assert.True(ProductState.Create(boundary, "Name", ProductKind.System, "").IsSuccess);
        Assert.True(ProductState.Restore(Guid.NewGuid(), boundary, "Name", ProductKind.System, "", false).IsSuccess);
        var oversized = boundary + character;
        Assert.True(ProductState.Restore(Guid.NewGuid(), oversized, "Name", ProductKind.System, "", true).IsFailure);
        var store = new Store();
        var result = await new CreateProduct(store).Execute(oversized, "Name", ProductKind.System, "");
        Assert.True(result.IsFailure);
        Assert.Equal("A Product ID must contain at most 100 Unicode characters.", result.Error);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public void ImmutableProductHasExplicitTransitionsAndValidatesRestoration()
    {
        var active = ProductState.Create("custom ID", "Name", ProductKind.System, null).Value;
        Assert.Equal("", active.Description);
        Assert.True(active.IsActive);
        Assert.True(active.Reactivate().IsFailure);
        var retired = active.Retire().Value;
        Assert.True(active.IsActive);
        Assert.False(retired.IsActive);
        Assert.True(retired.Retire().IsFailure);
        Assert.Equal(active, retired.Reactivate().Value);
        Assert.True(ProductState.Restore(Guid.NewGuid(), "id", "Name", ProductKind.System, null, true).IsFailure);
        Assert.True(ProductState.Restore(Guid.NewGuid(), "id", " ", ProductKind.System, "", false).IsFailure);
        Assert.All(typeof(ProductState).GetProperties(), property => Assert.Null(property.SetMethod));
    }

    [Fact]
    public async Task ApplicationPreservesFailuresAndAbsenceWithoutStatusWrites()
    {
        var store = new Store();
        var retrieve = new RetrieveProduct(store);
        Assert.Equal("Product not found.", (await retrieve.Execute("missing")).Error);
        Assert.True((await new ChangeProductStatus(store).Execute("missing", Guid.NewGuid(), false)).IsFailure);
        Assert.Equal(0, store.Writes);
        store.Error = "storage failed";
        Assert.Equal(store.Error, (await retrieve.Execute("id")).Error);
        Assert.Equal(store.Error, (await new CreateProduct(store).Execute("id", "Name", ProductKind.System, "")).Error);
        store.Error = null;
        store.Product = ProductState.Create("id", "Name", ProductKind.System, "Description").Value;
        var status = new ChangeProductStatus(store);
        Assert.True((await status.Execute("id", store.Product.InternalId, true)).IsFailure);
        Assert.Equal(1, store.Writes); // Only the failed create above reached a write.
        store.WriteError = "status failed";
        Assert.Equal(store.WriteError, (await status.Execute("id", store.Product.InternalId, false)).Error);
        Assert.Equal(2, store.Writes);
        Assert.True(store.Product.IsActive);
    }

    [Fact]
    public async Task DescriptionUpdatesPreserveIdentityAndStatusAndPropagateFailures()
    {
        var original = ProductState.Create("id", "Name", ProductKind.System, "original").Value;
        var changed = original.Edit(original.Id, original.Name, original.Kind, "# Heading\n\n**Formatted**").Value;
        Assert.Equal("original", original.Description);
        Assert.Equal(original.Id, changed.Id);
        Assert.Equal(original.Name, changed.Name);
        Assert.True(changed.IsActive);
        Assert.Equal("", changed.Edit(changed.Id, changed.Name, changed.Kind, null).Value.Description);
        var retired = original.Retire().Value;
        Assert.True(retired.Edit(retired.Id, retired.Name, retired.Kind, "forbidden").IsFailure);
        var store = new Store();
        var update = new UpdateProductDescription(store);
        Assert.Equal("Product not found.", (await update.Execute("id", original.InternalId, "", "new")).Error);
        Assert.Equal(0, store.Writes);
        store.Error = "read failed";
        Assert.Equal(store.Error, (await update.Execute("id", original.InternalId, "", "new")).Error);
        Assert.Equal(0, store.Writes);
        store.Error = null;
        store.Product = original;
        Assert.True((await update.Execute("id", original.InternalId, "stale", "new")).IsFailure);
        Assert.Equal(0, store.Writes);
        store.WriteError = "write failed";
        Assert.Equal(store.WriteError, (await update.Execute("id", original.InternalId, "original", "new")).Error);
        Assert.Equal(original, store.Product);
        store.WriteError = null;
        var result = await update.Execute("id", original.InternalId, "original", changed.Description);
        Assert.True(result.IsSuccess);
        Assert.Equal(changed, result.Value);
        Assert.Equal(changed, store.Product);
    }

    [Fact]
    public async Task RequiredFieldsAndAtomicReactivationAreEnforced()
    {
        Assert.True(ProductState.Create("id", "name", null, "").IsFailure);
        Assert.True(ProductState.Create("id", "name", (ProductKind)999, "").IsFailure);
        Assert.True(ProductState.Restore(Guid.Empty, "id", "name", ProductKind.System, "", true).IsFailure);
        foreach (var kind in Enum.GetValues<ProductKind>())
        {
            var created = ProductState.Create("  Ä id  ", "  Name  ", kind, null).Value;
            Assert.NotEqual(Guid.Empty, created.InternalId);
            Assert.Equal("Ä id", created.Id);
            Assert.Equal("Name", created.Name);
            Assert.Equal(kind, created.Kind);
        }
        var original = ProductState.Create("old", "name", ProductKind.System, "original").Value;
        var retired = original.Retire().Value;
        var store = new Store { Product = retired };
        var edit = new EditProduct(store);
        Assert.True((await edit.Execute(retired, "new", "changed", ProductKind.Firmware, "updated", false)).IsFailure);
        Assert.Equal(0, store.Writes);
        var result = await edit.Execute(retired, " new ", " changed ", ProductKind.Firmware, "updated", true);
        Assert.True(result.IsSuccess);
        Assert.Equal(original.InternalId, result.Value.InternalId);
        Assert.Equal("new", result.Value.Id);
        Assert.Equal("changed", result.Value.Name);
        Assert.Equal(ProductKind.Firmware, result.Value.Kind);
        Assert.Equal("updated", result.Value.Description);
        Assert.True(result.Value.IsActive);
        Assert.Equal(result.Value, store.Product);
        Assert.Equal(1, store.Writes);
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
        public Task<Result> Update(ProductState expected, ProductState replacement, CancellationToken cancellationToken = default)
        {
            Calls++; Writes++;
            if (WriteError is not null) return Task.FromResult(Result.Failure(WriteError));
            Product = replacement;
            return Task.FromResult(Result.Success());
        }
    }
}
