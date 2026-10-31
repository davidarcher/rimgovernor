namespace RimGovernor.Brain.Policy;

/// <summary>How the free-bed pool's barracks is sized: proposed by the clustering's own price, or fixed by a bed-sharing alternative.</summary>
public abstract record BedShare
{
    private BedShare() { }

    /// <summary>The clustering's price proposes the size (Clusterer.ChooseBeds).</summary>
    public sealed record Proposed : BedShare;

    /// <summary>A bed-sharing alternative fixes the size: 1 keeps every bed private, the pool's count shares one barracks.</summary>
    public sealed record Fixed(int Size) : BedShare;
}

/// <summary>
/// The bed-sharing alternatives of the free-bed pool (RG-HABITAT-001): shared, private and the held provision compared
/// as layout alternatives by the shared ledger rather than fixed by the clustering's local price. The baseline keeps the
/// size the clustering proposes (BedMergeChoice); each deviation fixes another size through BedMergePrice.Share: every
/// bed its own room (1), one barracks of the whole pool, and the barracks the plan holds. Each is planned only through
/// RegroupAsync, where its forecast prices the mood its occupants sleep in (BedroomCredit) against what it costs to build,
/// and the baseline keeps ties.
/// </summary>
public static class LayoutBedShareRules
{
    /// <summary>The regroup decision kind of a bed-sharing alternative (A: the free-bed pool, B: the barracks size).</summary>
    public const string BedShareMove = "bed_share";

    /// <summary>The decision's A: the free-bed pool whose size the alternative fixes.</summary>
    public const string FreeBeds = "free_beds";

    /// <summary>The alternative's id, which also separates its trials' signatures from the baseline's.</summary>
    public static string IdOf(int size) => "beds:" + size.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The sizes compared against the proposed one: private (1), the whole pool shared, and the held barracks (clamped to
    /// the pool), each distinct and other than the proposal. None when the pool has fewer than two beds.
    /// </summary>
    public static IReadOnlyList<int> DeviationsOf(int proposed, int incumbent, int pool)
    {
        if (pool < 2) return [];
        return new[] { 1, pool, Math.Clamp(incumbent, 1, pool) }.Distinct().Where(k => k != proposed).Order().ToArray();
    }

    /// <summary>
    /// The baseline input followed by one input per bed-sharing deviation (on the baseline hosts), each fixing its size;
    /// the input itself alone when its graph has no priced free-bed pool.
    /// </summary>
    public static IReadOnlyList<HostedRegroupInput> AlternativesOf(HostedRegroupInput baseline)
    {
        var cluster = baseline.Input.Cluster;
        if (cluster.BedMerge is not { } price || LayoutClusterRules.BedPoolOf(cluster) is not { } pool) return [baseline];
        var proposed = LayoutClusterRules.BedMergeChoiceOf(cluster)!.Size;
        return [baseline, .. DeviationsOf(proposed, price.Incumbent, pool).Select(k => new HostedRegroupInput(
            baseline.Host with { Id = IdOf(k) },
            baseline.Input with { Cluster = cluster with { BedMerge = price with { Share = new BedShare.Fixed(k) } } }) { Share = new BedShare.Fixed(k) })];
    }
}
