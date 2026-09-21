using System.Security.Cryptography;
using System.Text;

namespace Savvori.WebApi.Modeling;

/// <summary>What must be recorded with every stored embedding so it can be trusted (or recomputed) later.</summary>
public sealed record EmbeddingMetadata(string ModelName, string ModelDigest, int Dimension, string InputTextHash);

public static class EmbeddingFreshness
{
    /// <summary>The text embedded for a listing: <c>"{brand} {name}"</c> lower-cased, brand omitted if already in the name.</summary>
    public static string BuildInputText(string? brand, string name)
    {
        name = name.Trim();
        var text = string.IsNullOrWhiteSpace(brand) ||
                   name.Contains(brand.Trim(), StringComparison.OrdinalIgnoreCase)
            ? name
            : $"{brand.Trim()} {name}";
        return text.ToLowerInvariant();
    }

    public static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>
    /// Stale when the model, its digest, the dimension or the exact input text changed.
    /// Stale vectors must be recomputed and never compared with current ones.
    /// </summary>
    public static bool IsStale(EmbeddingMetadata stored, EmbeddingMetadata current) =>
        stored.ModelName != current.ModelName ||
        stored.ModelDigest != current.ModelDigest ||
        stored.Dimension != current.Dimension ||
        stored.InputTextHash != current.InputTextHash;
}

/// <summary>Counts embeddings that need recomputing. Phase 2 supplies the real implementation.</summary>
public interface IStaleEmbeddingSource
{
    Task<int> CountStaleAsync(CancellationToken ct = default);
}

public sealed class NullStaleEmbeddingSource : IStaleEmbeddingSource
{
    public Task<int> CountStaleAsync(CancellationToken ct = default) => Task.FromResult(0);
}
