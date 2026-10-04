using Microsoft.EntityFrameworkCore;
using Savvori.Shared;

namespace Savvori.WebApi;

/// <summary>
/// Follows the MatchMerge audit trail from a retired (deleted) canonical product to whichever
/// product currently owns its listings. Callers holding a stale id (a cached id in another app's
/// database, or a request that raced a merge) get redirected instead of a bare 404. Read-only —
/// never touches the matching pipeline (see Modeling/MatchApplier.cs).
/// </summary>
public sealed class ProductMergeResolver(SavvoriDbContext db)
{
    public async Task<(Product? Product, Guid? ResolvedFrom)> ResolveAsync(Guid id, CancellationToken ct = default)
    {
        var product = await db.Products.Include(p => p.ProductCategory).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (product is not null) return (product, null);

        var current = id;
        var visited = new HashSet<Guid> { id };
        for (var i = 0; i < 20; i++)
        {
            var merge = await db.MatchMerges
                .Where(m => m.RetiredProductId == current && m.UndoneAt == null)
                .OrderByDescending(m => m.AppliedAt)
                .FirstOrDefaultAsync(ct);
            if (merge is null) return (null, null);

            current = merge.SurvivorProductId;
            if (!visited.Add(current)) return (null, null); // cycle guard, shouldn't happen

            product = await db.Products.Include(p => p.ProductCategory).FirstOrDefaultAsync(p => p.Id == current, ct);
            if (product is not null) return (product, id);
        }
        return (null, null);
    }
}
