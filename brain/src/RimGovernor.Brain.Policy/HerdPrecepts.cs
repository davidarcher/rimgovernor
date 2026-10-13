namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    // The herd's precept facts come from the shared precept rule: each animal action is bound to the
    // HistoryEventDef the game raises for it (HistoryEventDefOf in Assembly-CSharp), and ActionStance answers what
    // the colony ideoligion makes of it.
    //
    // Selling an animal raises no history event in the game (only SoldSlave, SoldPrisoner and SoldOrgan exist),
    // so no precept bears on a sale and the herd plan sells without consulting the ideoligion.

    /// <summary>Raised by any slaughter of an animal.</summary>
    private const string EventSlaughteredAnimal = "SlaughteredAnimal";

    /// <summary>Additionally raised by a venerated race's slaughter.</summary>
    private const string EventSlaughteredVeneratedAnimal = "SlaughteredVeneratedAnimal";

    private const string EventAteMeat = "AteMeat";
    private const string EventAteVeneratedAnimalMeat = "AteVeneratedAnimalMeat";

    /// <summary>
    /// Go <c>ApplyHerdPrecepts</c>: sets each animal's SlaughterBarred and EatingBarred from the ideoligion.
    /// ideologyActive is whether the load has Ideology defs: a game without Ideology has no precepts and bars
    /// nothing; with Ideology, an unread ideoligion or veneration leaves both unknown (the herd plan then holds
    /// removal, sale and slaughter). A race is barred when any action it raises is not allowed or approved:
    /// penalised, forbidden or unknown.
    /// </summary>
    public static Fact<IReadOnlyList<UpkeepAnimal>> ApplyHerdPrecepts(Fact<IReadOnlyList<UpkeepAnimal>> animals, Fact<Ideoligion> ideology, bool ideologyActive)
    {
        if (!animals.TryGetValue(out var rows))
        {
            return animals;
        }
        var @out = new List<UpkeepAnimal>(rows.Count);
        foreach (var a in rows)
        {
            if (!ideologyActive)
            {
                @out.Add(a with { Herd = a.Herd with { SlaughterBarred = Known(false), EatingBarred = Known(false) } });
                continue;
            }
            @out.Add(
                a with
                {
                    Herd = a.Herd with
                    {
                        SlaughterBarred = PreceptBars(ideology, a.Herd.Venerated, EventSlaughteredAnimal, EventSlaughteredVeneratedAnimal),
                        EatingBarred = PreceptBars(ideology, a.Herd.Venerated, EventAteMeat, EventAteVeneratedAnimalMeat),
                    },
                }
            );
        }
        return Known<IReadOnlyList<UpkeepAnimal>>(@out);
    }

    /// <summary>Whether the colony's precepts bar the action: base for any animal, plus venerated for a venerated race. Unknown when the ideoligion or the veneration is unread.</summary>
    private static Fact<bool> PreceptBars(Fact<Ideoligion> ideology, Fact<bool> venerated, string baseEvent, string veneratedEvent)
    {
        if (!venerated.TryGetValue(out var v))
        {
            return Unknown<bool>();
        }
        var events = v ? new[] { baseEvent, veneratedEvent } : new[] { baseEvent };
        var barred = false;
        foreach (var ev in events)
        {
            switch (ActionStance(ideology, new PreceptAction { HistoryEvent = ev }, new PreceptSubject()).Stance)
            {
                case PreceptStance.Allowed or PreceptStance.Approved:
                    break;
                case PreceptStance.Penalised or PreceptStance.Forbidden:
                    barred = true;
                    break;
                default:
                    return Unknown<bool>();
            }
        }
        return Known(barred);
    }
}
