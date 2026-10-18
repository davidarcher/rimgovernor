using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The formation hunt the plan opened, in memory: its prey are admitted when the plan opens them (HuntRequest, which
/// makes the military/combat Need unmet), and the formation channel of admitted prey is Designated from then, so the
/// delivery credit commits it and moves the hunt group to Delivering at the first kill. It is held, not re-compared,
/// until a release trigger (<see cref="Release"/>): then the next review compares again from fresh estimates, and nothing
/// extends the hunt without that comparison. A restart forgets it; the next plan admits again.
/// A null reference stands for no admission: callers use <c>admission?.Apply(channels) ?? channels</c>.
/// </summary>
public sealed class HuntAdmission
{
    private readonly HashSet<string> _admitted = new(StringComparer.Ordinal);
    private Held? _committed;
    private Held? _offered;

    /// <summary>A chosen formation and the hunt read it was chosen from: the scene its estimate still described then.</summary>
    private sealed record Held(HuntAssessment Assessment, HuntRoster Roster, HuntActivityRead Read);

    /// <summary>
    /// The formation channel to offer and why a held one was released (empty when none was): the one the plan opened
    /// while no release trigger holds, else the plan's own choice. <paramref name="read"/> is the current hunt read.
    /// </summary>
    public (SupplyCandidate? Channel, string Released) Formation(SupplyCandidate? chosen, HuntPlan plan, IReadOnlyList<AcquisitionSource> sources, HuntActivityRead? read)
    {
        var released = "";
        if (_committed is { } held)
        {
            released = Release(read, sources);
            if (released.Length == 0)
            {
                _offered = null;
                return (HuntPlanningRules.HuntRosterChannel(held.Assessment, held.Roster, sources), "");
            }
            _committed = null;
            _admitted.Clear();
        }
        _offered = plan.Selection?.Selected is { Roster: { } roster } selected && chosen is not null && read is not null ? new(selected, roster, read) : null;
        return (chosen, released);
    }

    /// <summary>
    /// Why the held formation is released now, or empty while it stands (or nothing is held). The triggers: a rostered
    /// gunner left the hunt read, is downed or in a mental state; an admitted animal is gone from the sources, downed,
    /// out of the observed window, or a new animal joined its group; or a gunner is no longer the pawn his estimate was
    /// made from (<see cref="HuntEncounterRequests.StillDescribesGunner"/>). An unknown hunt read decides nothing about
    /// the gunners: the prey rules still apply. A drafted gunner is not a trigger: the hunt's own draft and the player's
    /// read the same.
    /// </summary>
    public string Release(HuntActivityRead? read, IReadOnlyList<AcquisitionSource> sources)
    {
        if (_committed is not { } held)
        {
            return "";
        }
        foreach (var id in held.Roster.Prey)
        {
            var source = sources.FirstOrDefault(s => s.Id == id && s.Hunt && s.Food);
            if (source is null)
            {
                return "prey_gone:" + id;
            }
            if (source.Downed)
            {
                return "prey_downed:" + id;
            }
        }
        if (read is not { Known: true })
        {
            return "";
        }
        foreach (var id in held.Roster.Prey)
        {
            if (read.Prey.All(p => p.Pawn != id))
            {
                return "prey_unobserved:" + id;
            }
        }
        var prey = held.Roster.Prey.Select(id => read.Prey.First(p => p.Pawn == id)).ToArray();
        foreach (var animal in read.Prey)
        {
            if (!animal.Downed && !animal.Mental && !held.Roster.Prey.Contains(animal.Pawn) && prey.Any(p => HuntAlternatives.Grouped(animal, p)))
            {
                return "prey_joined:" + animal.Pawn;
            }
        }
        foreach (var id in held.Roster.Gunners)
        {
            var gunner = read.Hunters.FirstOrDefault(h => h.Pawn == id);
            if (gunner is null)
            {
                return "gunner_unobserved:" + id;
            }
            if (gunner.Downed)
            {
                return "gunner_downed:" + id;
            }
            if (gunner.Mental)
            {
                return "gunner_mental:" + id;
            }
            if (!HuntEncounterRequests.StillDescribesGunner(held.Read, read, id))
            {
                return "gunner_changed:" + id + ":" + HuntEncounterRequests.GunnerDifferences(held.Read, read, id);
            }
        }
        return "";
    }

    /// <summary>Records the prey the plan opened; call it with HuntRequest of each plan.</summary>
    public void Admit(IEnumerable<Domain.PawnId> prey)
    {
        var opened = prey.ToArray();
        foreach (var id in opened)
        {
            _admitted.Add(id.Value);
        }
        if (_offered is { } offered && opened.Any(id => offered.Roster.Prey.Contains(id.Value)))
        {
            _committed = offered;
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
        var @out = new List<SupplyCandidate>(channels);
        for (var i = 0; i < @out.Count; i++)
        {
            var c = @out[i];
            if (c.Kind == CandidateKind.Hunt && c.Mode() == HuntMode.Formation && c.Prey.Any(_admitted.Contains))
            {
                @out[i] = c with { State = CandidateRules.FoodState(Known(false), Known(true)) };
            }
        }
        return @out;
    }
}
