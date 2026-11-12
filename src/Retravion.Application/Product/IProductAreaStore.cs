using CSharpFunctionalExtensions;
using Retravion.Application.AuditTrail;
using Retravion.Domain.Product;

namespace Retravion.Application.Product;

public sealed record ProductAreaSnapshot(ProductArea Area, long Revision);
public sealed record ProductAreaHistory(AuditActor Actor, DateTimeOffset OccurredAt, string Action, ProductAreaAuditDetails Change);
public sealed record ProductAreaRetrieval(IReadOnlyList<ProductAreaSnapshot> Areas, IReadOnlyList<ProductAreaHistory> History,
    ProductAreaHistory? Operation);

/// <summary>Product area reads and web-only administration; write authorization is rechecked inside the transaction.</summary>
public interface IProductAreaStore
{
    Task<Result<ProductAreaRetrieval>> Retrieve(Guid product, Guid? operation = null, CancellationToken token = default);
    Task<Result<ProductAreaSnapshot>> Save(Guid operation, ProductAreaSnapshot? expected, ProductArea replacement,
        AuditActor actor, DateTimeOffset time, Func<Task<bool>> authorized, CancellationToken token = default);
}
