namespace RimGovernor.Brain.Runtime;

/// <summary>
/// The urgent builds placed since the review that binds them (#3537). The brain directs construction only for urgent
/// builds, the colony's sleeping shelter and a bedless colonist's bed (<see cref="ShelterFamily.Sleeping"/>'s placements);
/// vanilla builds routine blueprints. A placement lands after the review that committed it, so the clock stays held from
/// that commit until a rounds review whose step began after the placement settled has compared the placed sites: with
/// game time in between, deferred colonists would build them by vanilla work first. A comparison that does not select
/// them leaves them to vanilla. That review compares the urgent buildings alone of the colony's construction: routine sites,
/// planned or placed, are vanilla's (ReviewFacts.UrgentSites). Derived state in memory: a restart empties it.
/// </summary>
internal sealed class UrgentBuilds
{
    private readonly Lock _mu = new();
    private readonly Dictionary<PlanId, EquatableArray<Building>> _waiting = [];
    private EquatableArray<Building> _covered;

    /// <summary>Records an admitted urgent placement plan, the buildings it places, that waits for a review to read its sites.</summary>
    public void Placing(PlanId plan, IEnumerable<Building> sites)
    {
        lock (_mu)
        {
            _waiting[plan] = EquatableArray.From(sites);
        }
    }

    /// <summary>Whether an urgent placement still waits for a review to read its sites.</summary>
    public bool Waiting
    {
        get
        {
            lock (_mu)
            {
                return _waiting.Count > 0;
            }
        }
    }

    /// <summary>
    /// The waiting plans whose placement has <paramref name="settled"/> now: a review whose step reads after this covers
    /// them, and compares their buildings alone of the placed construction (<see cref="Covered"/>).
    /// </summary>
    public IReadOnlyList<PlanId> Settled(Func<PlanId, bool> settled)
    {
        KeyValuePair<PlanId, EquatableArray<Building>>[] waiting;
        lock (_mu)
        {
            waiting = [.. _waiting];
        }
        var covered = waiting.Where(w => settled(w.Key)).ToArray();
        lock (_mu)
        {
            _covered = EquatableArray.From(covered.SelectMany(w => w.Value).Distinct());
        }
        return [.. covered.Select(w => w.Key)];
    }

    /// <summary>The buildings of the plans the last <see cref="Settled"/> covered: the urgent sites the next review compares.</summary>
    public EquatableArray<Building> Covered
    {
        get
        {
            lock (_mu)
            {
                return _covered;
            }
        }
    }

    /// <summary>A review covering <paramref name="covered"/> has committed: their sites were compared.</summary>
    public void Reviewed(IEnumerable<PlanId> covered)
    {
        lock (_mu)
        {
            foreach (var plan in covered)
            {
                _waiting.Remove(plan);
            }
            _covered = [];
        }
    }

    /// <summary>
    /// Whether a placement plan's dispatch has settled: no action waits to be sent or answered, so its blueprints stand
    /// (or never will). A retired or unknown plan is settled.
    /// </summary>
    public static bool PlacementSettled(PlanState plan) =>
        plan.Retired || plan.Progress.All(p => p.View.Stage is not (Stage.Pending or Stage.Prepared or Stage.Dispatched));
}
