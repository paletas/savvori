using Savvori.Shared;

namespace Savvori.WebApi.Modeling;

/// <summary>
/// A candidate is "inactive" when it is still undecided (Proposed, PendingJudge or NeedsReview) and one of its two
/// products is no longer active. Computed, never stored. The status comparisons are written inline so EF translates them.
/// </summary>
public static class CandidateInactive
{
    public static bool IsUndecided(CandidateStatus s) =>
        s is CandidateStatus.Proposed or CandidateStatus.PendingJudge or CandidateStatus.NeedsReview;

    /// <summary>Everything except undecided candidates that have an inactive side.</summary>
    public static IQueryable<MatchCandidate> ExcludingInactiveUndecided(this IQueryable<MatchCandidate> q, SavvoriDbContext db) =>
        q.Where(c => (c.Status != CandidateStatus.Proposed && c.Status != CandidateStatus.PendingJudge && c.Status != CandidateStatus.NeedsReview)
                     || (db.StoreProducts.Any(sp => sp.Id == c.StoreProductAId && sp.IsActive)
                         && db.StoreProducts.Any(sp => sp.Id == c.StoreProductBId && sp.IsActive)));

    /// <summary>Only undecided candidates that have at least one inactive side.</summary>
    public static IQueryable<MatchCandidate> OnlyInactiveUndecided(this IQueryable<MatchCandidate> q, SavvoriDbContext db) =>
        q.Where(c => (c.Status == CandidateStatus.Proposed || c.Status == CandidateStatus.PendingJudge || c.Status == CandidateStatus.NeedsReview)
                     && !(db.StoreProducts.Any(sp => sp.Id == c.StoreProductAId && sp.IsActive)
                          && db.StoreProducts.Any(sp => sp.Id == c.StoreProductBId && sp.IsActive)));
}
