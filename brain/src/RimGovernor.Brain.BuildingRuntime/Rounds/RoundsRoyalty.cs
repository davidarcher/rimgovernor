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
            return new RoyaltyFamilyFacts { Royalty = new RoyaltyState.Inactive(), PsylinkOwed = false };
        }
        var started = _reviewer.Clock.Now();
        var review = await _reviewer.Player.Journal.LoadRoundsAsync(call).ConfigureAwait(false);
        var expected = await StepScope.StepScopeAsync(scope, _reviewer.Native, call).ConfigureAwait(false);
        if (!RoundsBuildingPlanner.RoundsBuildingBoundary(expected, scope.World, review.Tick))
        {
            throw new ControlException("royalty read: the scope is another world or older than the review");
        }
        var read = await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, Unknown<EquatableArray<ConstructionClaim>>(), call).ConfigureAwait(false);
        var facts = new RoyaltyFamilyFacts
        {
            Royalty = read.Projection.Facts.Royalty,
            PsylinkOwed = _psylink is not null && await PsylinkOwedAsync(_psylink, Boundary.Identity(scope.World), read.Projection, call).ConfigureAwait(false),
        };
        var elapsed = _reviewer.Clock.Now() - started;
        if (elapsed < TimeSpan.Zero || elapsed > _reviewer.MaxAge)
        {
            throw new ControlException("royalty read: the read took too long");
        }
        return facts;
    }

    /// <summary>
    /// The royalty/psylink Need's predicate over the projection's royalty and pawn rows: a use is owed while a candidate
    /// or level-up colonist waits and a psylink neuroformer is held. None is owed with Royalty inactive. The held items
    /// are read natively only while a colonist waits and the census does not count none held. Throws while the pawn
    /// rows are unread; a failed item read throws.
    /// </summary>
    internal static async Task<bool> PsylinkOwedAsync(FacadeNativeClient native, C.Identity identity, ColonyProjection projection, CancellationToken ct)
    {
        if (projection.Facts.Royalty is not RoyaltyState.Active { Facts: var royalty })
        {
            return false;
        }
        if (!PsylinkRules.PsylinkCandidates(royalty, projection.WorkPawns).TryGetValue(out var first)
            || !PsylinkRules.PsylinkLevelUps(royalty, projection.WorkPawns).TryGetValue(out var up))
        {
            throw new RequiredFactException("royalty/psylink work pawns");
        }
        if (first.Count + up.Count == 0 || !royalty.Neuroformers.TryGetValue(PsylinkRules.PsylinkNeuroformer, out var stock) || stock.Held == 0)
        {
            return false;
        }
        var held = await native.ReadNeuroformerItemsAsync(identity, PsylinkRules.PsylinkNeuroformer, ct).ConfigureAwait(false);
        return held.Count > 0;
    }
}
