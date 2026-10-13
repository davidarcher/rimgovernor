namespace RimGovernor.Brain.Policy;

/// <summary>
/// The formation hunts the plan opened, in memory: their prey are admitted when the plan opens them
/// (HuntRequest, which raises the ActiveCombat hunt origin), and a formation channel of admitted prey is
/// Designated from then, so the delivery credit commits it and moves the hunt group to Delivering at the first
/// kill. It ends when no admitted animal stands any more. A restart forgets it; the next plan admits again.
/// Go's nil receiver is a null reference here: callers use <c>admission?.Apply(channels) ?? channels</c>.
/// </summary>
public sealed class HuntAdmission
{
    private readonly HashSet<string> _admitted = new(StringComparer.Ordinal);

    /// <summary>Records the prey the plan opened; call it with HuntRequest of each plan.</summary>
    public void Admit(IEnumerable<Domain.PawnId> prey)
    {
        foreach (var id in prey)
        {
            _admitted.Add(id.Value);
        }
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
                    @out[i] = c with { State = PolicyFunctions.FoodState(Known(false), Known(true)) };
                    break;
                }
            }
        }
        if (!standing)
        {
            _admitted.Clear();
        }
        return @out;
    }
}
