namespace RimGovernor.Brain.Policy;

/// <summary>
/// The formation hunts the plan opened, in memory: their prey are admitted when the plan opens them
/// (HuntRequest, which makes the military/combat Need unmet), and a formation channel of admitted prey is
/// Designated from then, so the delivery credit commits it and moves the hunt group to Delivering at the first
/// kill. It ends when no admitted animal stands any more. A restart forgets it; the next plan admits again.
/// A null reference stands for no admission: callers use <c>admission?.Apply(channels) ?? channels</c>.
/// </summary>
public sealed class HuntAdmission
{
    private readonly HashSet<string> _admitted = new(StringComparer.Ordinal);
    private HuntAssessment? _committed;
    private HuntAssessment? _offered;

    /// <summary>
    /// The formation channel to offer: the one the plan opened, while any of its prey stands (an executing hunt is not
    /// re-compared, so the animals it startles cannot stale its estimate and call the squad off), else the plan's own choice.
    /// </summary>
    public SupplyCandidate? Formation(SupplyCandidate? chosen, HuntPlan plan, IReadOnlyList<AcquisitionSource> sources)
    {
        if (_committed is { Roster: { } roster } held)
        {
            var standing = roster.Prey.Where(id => sources.Any(s => s.Id == id && s.Hunt && s.Food)).ToArray();
            if (standing.Length > 0)
            {
                _offered = null;
                return HuntPlanningRules.HuntRosterChannel(held, new HuntRoster(roster.Gunners, EquatableArray.From(standing)), sources);
            }
            _committed = null;
        }
        _offered = plan.Selection?.Selected is { Roster: not null } selected && chosen is not null ? selected : null;
        return chosen;
    }

    /// <summary>Records the prey the plan opened; call it with HuntRequest of each plan.</summary>
    public void Admit(IEnumerable<Domain.PawnId> prey)
    {
        var opened = prey.ToArray();
        foreach (var id in opened)
        {
            _admitted.Add(id.Value);
        }
        if (_offered is { Roster: { } roster } && opened.Any(id => roster.Prey.Contains(id.Value)))
        {
            _committed = _offered;
        }
        _offered = null;
    }

    /// <summary>Marks the formation channels that hold admitted prey Designated; lone channels and channels of other prey are returned as they are.</summary>
    public IReadOnlyList<SupplyCandidate> Apply(IReadOnlyList<SupplyCandidate> channels)
    {
        if (_admitted.Count == 0)
        {
            return channels;
        }
        var standing = false;
        var @out = new List<SupplyCandidate>(channels);
        for (var i = 0; i < @out.Count; i++)
        {
            var c = @out[i];
            if (c.Kind != CandidateKind.Hunt || c.Mode() != HuntMode.Formation)
            {
                continue;
            }
            foreach (var id in c.Prey)
            {
                if (_admitted.Contains(id))
                {
                    standing = true;
                    @out[i] = c with { State = CandidateRules.FoodState(Known(false), Known(true)) };
                    break;
                }
            }
        }
        if (!standing)
        {
            _admitted.Clear();
            _committed = null;
        }
        return @out;
    }
}
