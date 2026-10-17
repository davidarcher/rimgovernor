namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The runtime half of the royalty family (#3261): one owned read gives the royalty facts the permit ranking runs on
/// and, for the psylink Need, the candidates and level-ups with the held neuroformer items (a native read made only
/// while a colonist waits and the census does not already count none held). Both Needs work through their pure
/// proposals: the permit's pawn setting, and the psylink Need's hold.
/// </summary>
public sealed class RoyaltyRounds
{
    private readonly Rounder _reviewer;
    private readonly FacadeNativeClient? _psylink;
    private readonly bool _permits;

    /// <summary>The family with the permits Need when <paramref name="permits"/>, and the psylink Need read through <paramref name="psylink"/> when given.</summary>
    public RoyaltyRounds(Rounder reviewer, bool permits, FacadeNativeClient? psylink)
    {
        _reviewer = reviewer ?? throw new ControlException("RoyaltyRounds: reviewer is missing");
        if (!permits && psylink is null)
        {
            throw new ControlException("RoyaltyRounds: no royalty Need is composed");
        }
        _permits = permits;
        _psylink = psylink;
    }

    /// <summary>An optional entry woken by its pawn settings.</summary>
    public static FamilySchedule Schedule { get; } = new()
    {
        Kinds = [ActionKind.PawnSettings],
    };

    /// <summary>The family's scheduler entry.</summary>
    public FamilyRunner Runner() => new FamilyRunner<RoyaltyFamilyFacts>(new RoyaltyFamily(_permits, _psylink is not null), ReadAsync, Schedule);

    /// <summary>
    /// Reads the family's facts in <paramref name="scope"/>'s world. Throws when the read is from another world, older
    /// than the review, or slower than the review's age bound. The immediate pass reads nothing.
    /// </summary>
    internal async Task<RoyaltyFamilyFacts> ReadAsync(FamilyReadScope scope, CancellationToken call)
    {
        if (scope.Immediate)
        {
            return new RoyaltyFamilyFacts();
        }
        var started = _reviewer.Clock.Now();
        var review = await _reviewer.Player.Journal.LoadRoundsAsync(call).ConfigureAwait(false);
        var expected = await StepScope.StepScopeAsync(_reviewer.Native, call).ConfigureAwait(false);
        if (!RoundsBuildingPlanner.RoundsBuildingBoundary(expected, scope.World, review.Tick))
        {
            throw new ControlException("royalty read: the scope is another world or older than the review");
        }
        var read = await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, Unknown<EquatableArray<ConstructionClaim>>(), call).ConfigureAwait(false);
        var facts = new RoyaltyFamilyFacts { Royalty = read.Projection.Facts.Royalty };
        if (_psylink is not null)
        {
            facts = facts with { PsylinkOwed = await PsylinkOwedAsync(_psylink, Boundary.Identity(scope.World), read.Projection, call).ConfigureAwait(false) };
        }
        var elapsed = _reviewer.Clock.Now() - started;
        if (elapsed < TimeSpan.Zero || elapsed > _reviewer.MaxAge)
        {
            throw new ControlException("royalty read: the read took too long");
        }
        return facts;
    }

    /// <summary>
    /// PsylinkOwed over the projection's royalty and pawn rows: the held neuroformer items are read natively only while
    /// a candidate or level-up colonist waits and the census does not count none held; an unreadable item list leaves
    /// them unknown.
    /// </summary>
    internal static async Task<Fact<bool>> PsylinkOwedAsync(FacadeNativeClient native, C.Identity identity, ColonyProjection projection, CancellationToken ct)
    {
        var candidates = PolicyFunctions.PsylinkCandidates(projection.Royalty, projection.WorkPawns);
        var levelUps = PolicyFunctions.PsylinkLevelUps(projection.Royalty, projection.WorkPawns);
        var items = Unknown<EquatableArray<string>>();
        if (candidates.TryGetValue(out var first) && levelUps.TryGetValue(out var up) && first.Count + up.Count > 0 && projection.Royalty.TryGetValue(out var royalty))
        {
            if (royalty.Neuroformers.TryGetValue(PolicyConstants.PsylinkNeuroformer, out var stock) && stock.Held.TryGetValue(out var held) && held == 0)
            {
                items = Known<EquatableArray<string>>([]);
            }
            else if (royalty.Neuroformers.ContainsKey(PolicyConstants.PsylinkNeuroformer))
            {
                try
                {
                    var read = await native.ReadNeuroformerItemsAsync(identity, PolicyConstants.PsylinkNeuroformer, ct).ConfigureAwait(false);
                    items = Known<EquatableArray<string>>([.. read]);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // An unreadable neuroformer census leaves the items unknown.
                    items = Unknown<EquatableArray<string>>();
                }
            }
        }
        return PolicyFunctions.PsylinkOwed(candidates, levelUps, items);
    }
}
