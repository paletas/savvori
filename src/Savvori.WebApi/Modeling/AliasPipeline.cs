using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Quartz;
using Savvori.Shared;
using Savvori.WebApi.Scraping;

namespace Savvori.WebApi.Modeling;

/// <summary>Input text, hashing and cleaning for search aliases, shared by the scanner and the handler.</summary>
public static class AliasInputs
{
    /// <summary>Bump when the translator prompt changes materially, so every product is regenerated.</summary>
    public const int PromptVersion = 2;
    public const string ModelSource = "model";
    public const string ManualSource = "manual";
    private const int MaxKeywords = 5;
    private const int MaxKeywordLength = 40;

    public static string Hash(string name, string? brand, string? category) =>
        EmbeddingFreshness.HashText($"v{PromptVersion}|{EmbeddingFreshness.BuildInputText(brand, name, category)}");

    /// <summary>Trims, drops empty/over-long/duplicate keywords (after accent folding) and caps the count.</summary>
    public static List<string> Clean(IEnumerable<string>? keywords)
    {
        var seen = new HashSet<string>();
        var result = new List<string>();
        foreach (var raw in keywords ?? [])
        {
            var k = raw?.Trim();
            if (string.IsNullOrEmpty(k) || k.Length > MaxKeywordLength) continue;
            var folded = ProductNormalizer.Normalize(k);
            if (folded.Length == 0 || !seen.Add(folded)) continue;
            result.Add(k);
            if (result.Count == MaxKeywords) break;
        }
        return result;
    }
}

/// <summary>Works out which products need search aliases and queues idempotent jobs for them. No model calls.</summary>
public sealed class AliasScanner(SavvoriDbContext db, ModelJobQueue queue, IOptions<ModelOptions> options)
{
    public sealed record Needs(int Products, List<(Guid Id, string Hash)> Todo);

    /// <summary>
    /// A product needs aliases when it has no model-made row for its current input hash (never done, or its
    /// name, brand or category changed, or the prompt version moved on). Only products with an active listing count.
    /// </summary>
    public async Task<Needs> FindNeedsAsync(CancellationToken ct = default)
    {
        var products = await db.Products.AsNoTracking()
            .Where(p => p.StoreProducts.Any(sp => sp.IsActive))
            .Select(p => new { p.Id, p.Name, p.Brand, Category = p.ProductCategory == null ? null : p.ProductCategory.Name })
            .ToListAsync(ct);
        var done = (await db.ProductSearchAliases.AsNoTracking()
                .Where(a => a.InputHash != null)
                .Select(a => new { a.ProductId, a.InputHash })
                .Distinct().ToListAsync(ct))
            .Select(a => (a.ProductId, a.InputHash!)).ToHashSet();

        // A product corrected by a person in every language has nothing left for the model to do.
        var fullyManual = (await db.ProductSearchAliases.AsNoTracking()
                .Where(a => a.Source == AliasInputs.ManualSource)
                .GroupBy(a => a.ProductId).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct))
            .Where(g => g.N >= OllamaProductTranslator.Languages.Length).Select(g => g.Key).ToHashSet();

        var todo = new List<(Guid, string)>();
        foreach (var p in products)
        {
            if (fullyManual.Contains(p.Id)) continue;
            var hash = AliasInputs.Hash(p.Name, p.Brand, p.Category);
            if (!done.Contains((p.Id, hash))) todo.Add((p.Id, hash));
        }
        return new Needs(products.Count, todo);
    }

    public async Task<(int Products, int Todo, int Enqueued)> ScanAsync(CancellationToken ct = default)
    {
        var needs = await FindNeedsAsync(ct);
        var batch = needs.Todo.Take(Math.Max(1, options.Value.Aliases.MaxJobsPerScan)).ToList();
        var enqueued = await queue.EnqueueManyAsync(ModelJobType.Translate, batch, ct);
        return (needs.Products, needs.Todo.Count, enqueued);
    }
}

/// <summary>
/// Asks the translator for generic names and keywords per language and stores one row per (product, language).
/// The rows only widen search; they never touch matching, merging or categories. A manual row is never overwritten.
/// </summary>
public sealed class TranslateJobHandler(
    SavvoriDbContext db, IProductTranslator translator, IOptions<ModelOptions> options, TimeProvider time) : IBatchModelJobHandler
{
    public ModelJobType Type => ModelJobType.Translate;

    public Task HandleAsync(ModelJob job, CancellationToken ct) => HandleBatchAsync([job], ct);

    public async Task HandleBatchAsync(IReadOnlyList<ModelJob> jobs, CancellationToken ct)
    {
        var ids = jobs.Select(j => j.SubjectId).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.Brand, Category = p.ProductCategory == null ? null : p.ProductCategory.Name })
            .ToDictionaryAsync(p => p.Id, ct);
        var manual = (await db.ProductSearchAliases.AsNoTracking()
                .Where(a => ids.Contains(a.ProductId) && a.Source == AliasInputs.ManualSource)
                .Select(a => new { a.ProductId, a.Language }).ToListAsync(ct))
            .Select(a => (a.ProductId, a.Language)).ToHashSet();

        // A job whose product vanished (e.g. retired by a merge) or whose text changed since queueing is obsolete:
        // the next scan queues a fresh job for the new text. Completing it without work is correct and idempotent.
        var work = new List<(Guid Id, string Hash, TranslateItem Item)>();
        foreach (var job in jobs)
        {
            if (!products.TryGetValue(job.SubjectId, out var p)) continue;
            if (AliasInputs.Hash(p.Name, p.Brand, p.Category) != job.PayloadHash) continue;
            if (OllamaProductTranslator.Languages.All(l => manual.Contains((p.Id, l)))) continue;
            if (work.All(w => w.Id != p.Id)) work.Add((p.Id, job.PayloadHash, new TranslateItem(p.Name, p.Brand, p.Category)));
        }

        var perRequest = Math.Max(1, options.Value.Aliases.ProductsPerRequest);
        foreach (var chunk in work.Chunk(perRequest))
        {
            var results = await translator.TranslateAsync(chunk.Select(w => w.Item).ToList(), ct);
            if (results.Count != chunk.Length)
                throw new ModelResponseException($"Expected {chunk.Length} results, got {results.Count}.");
            await StoreAsync(chunk, results, manual, ct);
        }
    }

    private async Task StoreAsync(
        (Guid Id, string Hash, TranslateItem Item)[] chunk, IReadOnlyList<TranslateResult> results,
        HashSet<(Guid, string)> manual, CancellationToken ct)
    {
        var chunkIds = chunk.Select(c => c.Id).ToList();
        var existing = await db.ProductSearchAliases.Where(a => chunkIds.Contains(a.ProductId))
            .ToDictionaryAsync(a => (a.ProductId, a.Language), ct);
        var now = time.GetUtcNow().UtcDateTime;
        var model = options.Value.JudgeModel;

        for (var i = 0; i < chunk.Length; i++)
        {
            foreach (var lang in OllamaProductTranslator.Languages)
            {
                if (manual.Contains((chunk[i].Id, lang))) continue;
                var keywords = AliasInputs.Clean(results[i].Keywords.GetValueOrDefault(lang));
                if (!existing.TryGetValue((chunk[i].Id, lang), out var row))
                {
                    row = new ProductSearchAlias { ProductId = chunk[i].Id, Language = lang };
                    db.ProductSearchAliases.Add(row);
                }
                row.Name = keywords.FirstOrDefault() ?? string.Empty;
                row.Keywords = string.Join(", ", keywords);
                row.SearchText = ProductNormalizer.Normalize(string.Join(' ', keywords));
                row.Source = AliasInputs.ModelSource;
                row.ModelName = model;
                row.InputHash = chunk[i].Hash;
                row.CreatedAt = now;
            }
        }
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Hourly: queue alias jobs for new/changed products. Skipped entirely while the feature flag is off.</summary>
[DisallowConcurrentExecution]
public sealed class AliasScanJob(
    IOptions<ModelOptions> options, IServiceScopeFactory scopes, ModelTelemetry telemetry, ILogger<AliasScanJob> logger) : IJob
{
    private const string JobName = "alias-scan";

    public async Task Execute(IJobExecutionContext context)
    {
        if (!options.Value.Enabled) return;
        using var activity = telemetry.StartRunActivity(JobName);
        telemetry.RunsStarted.Add(1, new KeyValuePair<string, object?>("job.name", JobName));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var scope = scopes.CreateScope();
            var (products, todo, enqueued) = await scope.ServiceProvider.GetRequiredService<AliasScanner>()
                .ScanAsync(context.CancellationToken);
            logger.LogInformation("Alias scan: {Products} products, {Todo} need aliases, {Enqueued} jobs queued.",
                products, todo, enqueued);
            telemetry.RunsCompleted.Add(1, new KeyValuePair<string, object?>("job.name", JobName));
        }
        catch
        {
            telemetry.RunsFailed.Add(1, new KeyValuePair<string, object?>("job.name", JobName));
            throw;
        }
        finally
        {
            telemetry.RunDurationMs.Record(sw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("job.name", JobName));
        }
    }
}
