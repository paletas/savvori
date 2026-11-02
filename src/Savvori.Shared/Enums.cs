namespace Savvori.Shared;

public enum ProductUnit { Unit, Kg, G, L, Ml, Pack }

public enum ScrapingJobStatus { Pending, Running, Completed, Failed, CompletedWithWarnings = 4, Degraded = 5 }

public enum OutcomeKind { Category, Term, Discovery, Canary }

public enum OutcomeResult { Ok, Failed, Redirected, Blocked, NoTiles, MissingFields, Recovered, ContentPage }

public static class OutcomeResults
{
    /// <summary>Results that are not failures: fine, recovered by the re-attempt, or a legitimately empty content page.</summary>
    public static readonly OutcomeResult[] NonFailures = [OutcomeResult.Ok, OutcomeResult.Recovered, OutcomeResult.ContentPage];

    public static bool IsFailure(OutcomeResult result) => !NonFailures.Contains(result);
}

public enum ScrapingLogLevel { Info, Warning, Error }

public enum MatchStatus
{
    /// <summary>StoreProduct has not been matched to a canonical product yet.</summary>
    Unmatched,
    /// <summary>Matched automatically by the processor (EAN or brand+name+size+unit).</summary>
    AutoMatched,
    /// <summary>Matched or corrected by an admin.</summary>
    ManualMatched,
    /// <summary>Matching was attempted but failed; requires manual intervention.</summary>
    Failed
}
