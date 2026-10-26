namespace Savvori.Shared;

public class ScrapingJob
{
    public Guid Id { get; set; }
    public Guid StoreChainId { get; set; }
    public StoreChain StoreChain { get; set; } = null!;
    public ScrapingJobStatus Status { get; set; } = ScrapingJobStatus.Pending;
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int ProductsScraped { get; set; }
    public string? ErrorMessage { get; set; }
    public int ProductsReturned { get; set; }
    public string? HealthReason { get; set; }
    public int WarningCount { get; set; }
    public int StaleMarked { get; set; }
    public bool StaleMarkingSkipped { get; set; }
    public int? PreviousHealthyCount { get; set; }
    public double? DropRatio { get; set; }
    public List<ScrapingLog> Logs { get; set; } = new();
    public List<ScrapingRunOutcome> Outcomes { get; set; } = new();
}

public class ScrapingRunOutcome
{
    public Guid Id { get; set; }
    public Guid ScrapingJobId { get; set; }
    public ScrapingJob ScrapingJob { get; set; } = null!;
    public OutcomeKind Kind { get; set; }
    public string Key { get; set; } = "";
    public string Url { get; set; } = "";
    public OutcomeResult Result { get; set; }
    public int PagesFetched { get; set; }
    public int ProductsFound { get; set; }
    public int TilesSeen { get; set; }
    public int Retries { get; set; }
    public string StatusCounts { get; set; } = "";
    public string? FirstError { get; set; }
}

public class ScrapingLog
{
    public Guid Id { get; set; }
    public Guid ScrapingJobId { get; set; }
    public ScrapingJob ScrapingJob { get; set; } = null!;
    public ScrapingLogLevel Level { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}
