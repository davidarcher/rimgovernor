using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Policy;

// Prisoner medicine: a harvest target holds herbal care at most (the care cap, MedicalCareChanges). A harvest
// or part recovery that only the care limit blocks (better medicine stocked, no herbal) is refused with a
// reason naming the limit, and asks the economy/resource Need for herbal medicine through ResourceNeeds (healroot
// growing, trade buys).

public static partial class PrisonerMedicineRules
{
    /// <summary>
    /// The herbal stock a care-limited prisoner surgery asks for: vanilla's harvest and removal recipes take
    /// one medicine, and a second covers the tend after a failure.
    /// </summary>
    public const long PrisonerSurgeryHerbal = 2;

    private readonly record struct CareOp(PawnId Pawn, string Recipe, int Part);

    /// <summary>
    /// The organ harvest, else the part recovery, that SelectOrganHarvest or SelectPartRecovery would choose
    /// were every care-limited operation stocked, when the chosen operation is care-limited. Null when nothing
    /// is chosen or the choice is not blocked by care.
    /// </summary>
    public static OrganHarvest? CareLimitedHarvest(
        PrisonerCensus census,
        IReadOnlyList<OrganNeed> needs,
        IReadOnlyList<OrganNeed> parts,
        IReadOnlyDictionary<PawnId, bool>? inFlight,
        RecipeFacts facts)
    {
        var relaxed = new List<PrisonerFacts>(census.Prisoners.Count);
        var limited = new HashSet<CareOp>();
        foreach (var row in census.Prisoners)
        {
            var copied = new List<CareOperation>(row.Operations);
            for (var j = 0; j < copied.Count; j++)
            {
                var op = copied[j].Operation;
                if (op.MedicineCareLimited)
                {
                    copied[j] = copied[j] with { Operation = op with { IngredientsOnMap = true } };
                    limited.Add(new CareOp(row.Pawn, op.Recipe, SurgeryRules.PartOf(op)));
                }
            }
            relaxed.Add(row with { Operations = EquatableArray.From(copied) });
        }
        if (limited.Count == 0)
        {
            return null;
        }
        var relaxedFacts = census with { Prisoners = EquatableArray.From(relaxed) };
        var h = OrganHarvestRules.SelectOrganHarvest(relaxedFacts, needs, inFlight) ?? OrganHarvestRules.SelectPartRecovery(relaxedFacts, parts, inFlight, facts);
        return h is not null && limited.Contains(new CareOp((PawnId)h.Prisoner, h.Recipe, h.Part)) ? h : null;
    }

    /// <summary>
    /// Adds PrisonerSurgeryHerbal herbal medicine to needs while a sale harvest or stock recovery (the ones
    /// that keep the medical/surgery Need unmet) is blocked only by the prisoner care limit.
    /// </summary>
    public static IReadOnlyDictionary<Resource, long> PrisonerHerbalNeeds(IReadOnlyDictionary<Resource, long> needs, RoundsFacts f, Fact<bool> silverShort)
    {
        var stock = new Dictionary<Resource, long>();
        foreach (var row in f.Resources)
        {
            stock[row.Resource] = stock.GetValueOrDefault(row.Resource) + row.Count;
        }
        var sale = OrganHarvestRules.OrganNeeds(Unknown<EquatableArray<CarePawn>>(), [], RoundsRules.Positive(silverShort), stock, f.Recipes);
        if (CareLimitedHarvest(f.Population, sale, [], null, f.Recipes) is null)
        {
            return needs;
        }
        Resource herbal;
        try
        {
            herbal = f.Items.MedicineAt(0);
        }
        catch (DomainException)
        {
            return needs;
        }
        return ResourceRules.ResourceConcernTargets(needs, new Dictionary<Resource, long> { [herbal] = PrisonerMedicineRules.PrisonerSurgeryHerbal });
    }
}
