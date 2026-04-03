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
    public List<ScrapingLog> Logs { get; set; } = new();
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
