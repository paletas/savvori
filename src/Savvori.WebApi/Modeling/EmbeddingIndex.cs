using System.Numerics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Savvori.WebApi.Modeling;

/// <summary>float32 little-endian packing for the <c>Vector</c> column.</summary>
public static class VectorCodec
{
    public static byte[] ToBytes(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[] FromBytes(byte[] bytes)
    {
        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, vector.Length * sizeof(float));
        return vector;
    }

    /// <summary>Returns a unit-length copy, or null for a zero/invalid vector.</summary>
    public static float[]? Normalize(float[] vector)
    {
        double sum = 0;
        foreach (var x in vector) sum += (double)x * x;
        if (sum <= 0 || double.IsNaN(sum) || double.IsInfinity(sum)) return null;
        var inv = (float)(1.0 / Math.Sqrt(sum));
        var result = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++) result[i] = vector[i] * inv;
        return result;
    }
}

public sealed record IndexEntry(Guid StoreProductId, Guid ChainId, float[] Vector);

public sealed record Neighbor(int Row, double Cosine);

/// <summary>An immutable view of the index: every vector comes from one model identity, so all are comparable.</summary>
public sealed class IndexSnapshot(IReadOnlyList<IndexEntry> entries, ModelInfo? identity, int dimension)
{
    public static readonly IndexSnapshot Empty = new([], null, 0);

    public IReadOnlyList<IndexEntry> Entries { get; } = entries;
    public ModelInfo? Identity { get; } = identity;
    public int Dimension { get; } = dimension;
    public int Count => Entries.Count;

    /// <summary>
    /// Brute-force top-<paramref name="k"/> neighbours of one row among entries of a DIFFERENT chain with
    /// cosine at least <paramref name="minCosine"/>, best first.
    /// </summary>
    public List<Neighbor> TopNeighbors(int row, int k, double minCosine)
    {
        var self = Entries[row];
        var best = new List<Neighbor>(k + 1);
        for (var j = 0; j < Entries.Count; j++)
        {
            if (Entries[j].ChainId == self.ChainId) continue;
            var cos = Dot(self.Vector, Entries[j].Vector);
            if (cos < minCosine) continue;
            if (best.Count == k && cos <= best[^1].Cosine) continue;

            var at = best.FindIndex(n => n.Cosine < cos);
            best.Insert(at < 0 ? best.Count : at, new Neighbor(j, cos));
            if (best.Count > k) best.RemoveAt(best.Count - 1);
        }
        return best;
    }

    public static float Dot(float[] a, float[] b)
    {
        var width = Vector<float>.Count;
        var acc = Vector<float>.Zero;
        var i = 0;
        for (; i <= a.Length - width; i += width)
            acc += new Vector<float>(a, i) * new Vector<float>(b, i);
        var sum = Vector.Sum(acc);
        for (; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }
}

/// <summary>
/// In-memory brute-force index over stored embeddings of active products (about 18k x 1024 floats). Built lazily
/// and refreshed incrementally from the database. Only vectors of ONE model identity (name + digest + dimension)
/// are ever served: the identity of the most recently embedded vector wins, and vectors from any other identity
/// are excluded until they are recomputed. No vector database; nothing here calls the model.
/// </summary>
public sealed class EmbeddingIndex(IServiceScopeFactory scopes, IOptions<ModelOptions> options)
{
    private sealed record Row(Guid Id, Guid ChainId, byte[] Vector, string Model, string Digest, int Dim, DateTime EmbeddedAt);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, (IndexEntry Entry, ModelInfo Identity, int Dim)> _entries = [];
    private readonly HashSet<Guid> _seen = [];
    private ModelInfo? _identity;
    private int _dimension;
    private DateTime _watermark = DateTime.MinValue;

    public IndexSnapshot Current { get; private set; } = IndexSnapshot.Empty;

    /// <summary>Brings the index up to date with the database and returns the fresh snapshot.</summary>
    public async Task<IndexSnapshot> RefreshAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SavvoriDbContext>();
            var model = options.Value.EmbeddingModel;

            var live = from e in db.StoreProductEmbeddings
                       join sp in db.StoreProducts on e.StoreProductId equals sp.Id
                       where sp.IsActive && e.ModelName == model
                       select new { e, sp.StoreChainId };

            // Rows can disappear (product deactivated/deleted) and an incremental read cannot see that: rebuild.
            var dbCount = await live.CountAsync(ct);
            if (dbCount < _seen.Count) Reset();

            var changed = await live
                .Where(x => x.e.EmbeddedAt >= _watermark)
                .Select(x => new Row(x.e.StoreProductId, x.StoreChainId, x.e.Vector, x.e.ModelName,
                    x.e.ModelDigest, x.e.Dimension, x.e.EmbeddedAt))
                .ToListAsync(ct);

            if (changed.Count > 0)
            {
                var newest = changed.MaxBy(r => r.EmbeddedAt)!;
                _watermark = newest.EmbeddedAt;
                var identity = new ModelInfo(newest.Model, newest.Digest);
                if (_identity != identity || _dimension != newest.Dim)
                {
                    _identity = identity;
                    _dimension = newest.Dim;
                }

                foreach (var row in changed)
                {
                    _seen.Add(row.Id);
                    _entries.Remove(row.Id);
                    var rowIdentity = new ModelInfo(row.Model, row.Digest);
                    if (rowIdentity != _identity || row.Dim != _dimension || row.Vector.Length != row.Dim * sizeof(float))
                        continue;
                    if (VectorCodec.Normalize(VectorCodec.FromBytes(row.Vector)) is { } unit)
                        _entries[row.Id] = (new IndexEntry(row.Id, row.ChainId, unit), rowIdentity, row.Dim);
                }

                // A newer identity may have taken over: drop everything from any other model.
                foreach (var stale in _entries.Where(kv => kv.Value.Identity != _identity || kv.Value.Dim != _dimension)
                             .Select(kv => kv.Key).ToList())
                    _entries.Remove(stale);
            }

            Current = _entries.Count == 0
                ? IndexSnapshot.Empty
                : new IndexSnapshot(_entries.Values.Select(v => v.Entry).ToList(), _identity, _dimension);
            return Current;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Reset()
    {
        _entries.Clear();
        _seen.Clear();
        _identity = null;
        _dimension = 0;
        _watermark = DateTime.MinValue;
    }
}
