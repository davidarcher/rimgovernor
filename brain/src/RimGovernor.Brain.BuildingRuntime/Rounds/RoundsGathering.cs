using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Rounder
{
    /// <summary>
    /// The party the mood census owes in <paramref name="reading"/>, the rooms/gatherings Need's evidence: the party
    /// thought's worth, the mood ledger and pawns, and the colony's calm. The AttendedParty memory is the cooldown.
    /// </summary>
    internal static Fact<GatheringPlan> GatheringPlanOf(RoundsReading reading, GenerationSnapshot snapshot)
    {
        var facts = reading.Projection.Facts;
        var thought = reading.Catalog?.ThoughtFacts(GatheringRules.PartyThought)
            ?? throw new RequiredFactException(GatheringRules.PartyThought + " thought facts");

        return GatheringRules.PlanGathering(new GatheringInput
        {
            Ledger = facts.MoodLedger,
            Pawns = facts.MoodPawns,
            Benefit = thought.WorstOffset,
            Calm = RoundsCalm(reading, snapshot),
            Census = facts.CurrentConstruction,
        });
    }
}
