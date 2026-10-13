using System.Text.RegularExpressions;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Store;

/// <summary>
/// Keeps the decoded state of each non-retired plan between reads (Go <c>planCache</c>).
/// </summary>
/// <remarks>
/// Decoding a plan (its action rows, dependencies, every transition replayed into a progress, its admissions) is
/// the store's dominant allocation, and the planners read the whole catalog many times per scheduler step. Go
/// watched the connection with a pre-update hook; this port cannot hook the native library, so the cache is told
/// instead. <see cref="StoreTransaction.Execute"/> flushes it for any statement that writes one of the five tables
/// a plan is decoded from, <see cref="StoreTransaction.ExecuteForPlan"/> drops only the named plan, and a rollback
/// or a savepoint rollback flushes it when the transaction wrote. A write by another connection or process changes
/// SQLite's <c>data_version</c>, which flushes it too. The database has one connection, so a load runs only
/// between transactions or inside the one that is writing.
/// </remarks>
internal sealed partial class PlanCache
{
    private readonly object _lock = new();
    private readonly Dictionary<PlanId, PlanState> _plans = [];
    private long _version = -1;
    private bool _wrote;
    private long _hits;
    private long _misses;
    private long _drops;
    private long _flushes;

    public PlanCacheStats Stats
    {
        get
        {
            lock (_lock)
            {
                return new PlanCacheStats(_hits, _misses, _drops, _flushes);
            }
        }
    }

    [GeneratedRegex(@"^\s*(?:INSERT(?:\s+OR\s+\w+)?\s+INTO|REPLACE\s+INTO|UPDATE(?:\s+OR\s+\w+)?|DELETE\s+FROM)\s+(?:plans|actions|action_dependencies|transitions|admissions)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlanWrite();

    /// <summary>Flushes the cache when <paramref name="sql"/> writes a row a plan is decoded from.</summary>
    public void Observe(string sql)
    {
        if (!PlanWrite().IsMatch(sql))
        {
            return;
        }
        lock (_lock)
        {
            ClearLocked();
            _wrote = true;
        }
    }

    /// <summary>Drops one plan: a write changed only rows that feed it.</summary>
    public void Observe(PlanId plan)
    {
        lock (_lock)
        {
            if (_plans.Remove(plan))
            {
                _drops++;
            }
            _wrote = true;
        }
    }

    public void Committed()
    {
        lock (_lock)
        {
            _wrote = false;
        }
    }

    public void RolledBack()
    {
        lock (_lock)
        {
            if (_wrote)
            {
                ClearLocked();
            }
            _wrote = false;
        }
    }

    /// <summary>Flushes the cache when another connection has committed since the last read.</summary>
    public void SyncVersion(StoreTransaction tx)
    {
        var version = tx.DataVersion();
        lock (_lock)
        {
            if (version != _version)
            {
                ClearLocked();
                _version = version;
            }
        }
    }

    public bool TryGet(PlanId id, out PlanState state)
    {
        lock (_lock)
        {
            if (_plans.TryGetValue(id, out var held))
            {
                _hits++;
                state = held;
                return true;
            }
            _misses++;
            state = null!;
            return false;
        }
    }

    public void Put(PlanId id, PlanState state)
    {
        lock (_lock)
        {
            _plans[id] = state;
        }
    }

    private void ClearLocked()
    {
        _plans.Clear();
        _flushes++;
    }
}
