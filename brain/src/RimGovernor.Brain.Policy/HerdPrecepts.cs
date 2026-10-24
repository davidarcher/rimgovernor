namespace RimGovernor.Brain.Policy;

public static class HerdPreceptsRules
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
    /// Sets each animal's SlaughterBarred and EatingBarred from the ideoligion. A game without Ideology has no
    /// precepts and bars nothing. A race is barred when any action it raises is penalised or forbidden.
    /// </summary>
    public static EquatableArray<UpkeepAnimal> ApplyHerdPrecepts(EquatableArray<UpkeepAnimal> animals, IdeologyMode ideology)
    {
        var @out = new List<UpkeepAnimal>(animals.Count);
        foreach (var a in animals)
        {
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
        return EquatableArray.From(@out);
    }

    /// <summary>Whether the colony's precepts bar the action: base for any animal, plus venerated for a venerated race.</summary>
    private static bool PreceptBars(IdeologyMode ideology, bool venerated, string baseEvent, string veneratedEvent)
    {
        var events = venerated ? new[] { baseEvent, veneratedEvent } : new[] { baseEvent };
        foreach (var ev in events)
        {
            var stance = ideology.ActionStance(new PreceptAction { HistoryEvent = ev }, new PreceptSubject()).Stance;
            if (stance is PreceptStance.Penalised or PreceptStance.Forbidden)
            {
                return true;
            }
        }
        return false;
    }
}
