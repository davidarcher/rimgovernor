using System.Text.RegularExpressions;

namespace RimGovernor.Brain.Store;

/// <summary>
/// A store's last validated review. The review is read several times per transaction (an admission's safeguards,
/// overlap and resource checks each load it), and reading, decoding, re-encoding and validating its payload was the
/// brain's largest allocator, so the review is kept until the rounds row may have changed. It is told the way
/// <see cref="PlanCache"/> is: <see cref="StoreTransaction.Execute"/> clears it for a statement that writes the row,
/// a rollback clears it when the transaction wrote, and a commit by another connection changes SQLite's
/// <c>data_version</c>, which a load compares.
/// </summary>
internal sealed partial class ReviewSlot
{
    private readonly object _lock = new();
    private ReviewState? _review;
    private long _version = -1;
    private bool _wrote;

    [GeneratedRegex(@"^\s*(?:INSERT(?:\s+OR\s+\w+)?\s+INTO|REPLACE\s+INTO|UPDATE(?:\s+OR\s+\w+)?|DELETE\s+FROM)\s+rounds\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Write();

    /// <summary>The kept review when nothing can have changed the row since it was read at <paramref name="version"/>.</summary>
    public ReviewState? Get(long version)
    {
        lock (_lock)
        {
            return version == _version ? _review : null;
        }
    }

    public void Put(long version, ReviewState review)
    {
        lock (_lock)
        {
            _review = review;
            _version = version;
        }
    }

    public void Observe(string sql)
    {
        if (!Write().IsMatch(sql))
        {
            return;
        }
        lock (_lock)
        {
            _review = null;
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
                _review = null;
            }
            _wrote = false;
        }
    }
}
