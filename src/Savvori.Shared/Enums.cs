namespace Savvori.Shared;

public enum ProductUnit { Unit, Kg, G, L, Ml, Pack }

public enum ScrapingJobStatus { Pending, Running, Completed, Failed }

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
