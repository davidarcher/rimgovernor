using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Policy;

// Prisoner medicine: a harvest target holds herbal care at most (the care cap, MedicalCareChanges). A harvest
// or part recovery that only the care limit blocks (better medicine stocked, no herbal) is refused with a
// reason naming the limit, and asks MaintainResource for herbal medicine through ResourceNeeds (healroot
// growing, trade buys).

public static partial class PolicyConstants
{
    /// <summary>The care ceiling a harvest target is held at.</summary>
    public const string PrisonerMedicalCare = "HerbalOrWorse";

    /// <summary>
    /// The herbal stock a care-limited prisoner surgery asks for: vanilla's harvest and removal recipes take
    /// one medicine, and a second covers the tend after a failure.
    /// </summary>
    public const long PrisonerSurgeryHerbal = 2;
}

public static partial class PolicyFunctions
{
    private readonly record struct CareOp(PawnId Pawn, string Recipe, int Part);

    /// <summary>
    /// The organ harvest, else the part recovery, that SelectOrganHarvest or SelectPartRecovery would choose
    /// were every care-limited operation stocked, when the chosen operation is care-limited. Null when nothing
    /// is chosen or the choice is not blocked by care.
    /// </summary>
    public static OrganHarvest? CareLimitedHarvest(
        Fact<IReadOnlyList<PrisonerFacts>> prisoners,
        Fact<PrisonerColony> colony,
        IReadOnlyList<OrganNeed> needs,
        IReadOnlyList<OrganNeed> parts,
        IReadOnlyDictionary<PawnId, bool>? inFlight,
        RecipeFacts facts)
    {
        if (!prisoners.TryGetValue(out var rows))
        {
            return null;
        }
        var relaxed = new List<PrisonerFacts>(rows.Count);
        var limited = new HashSet<CareOp>();
        foreach (var row in rows)
        {
            if (!row.Operations.TryGetValue(out var ops))
            {
                relaxed.Add(row);
                continue;
            }
            var copied = new List<SurgeryOperation>(ops);
            for (var j = 0; j < copied.Count; j++)
            {
                var op = copied[j];
                if (Positive(op.CareLimited))
                {
                    copied[j] = op with { IngredientsOnMap = Known(true) };
                    op.Recipe.TryGetValue(out var recipe);
                    op.PartIndex.TryGetValue(out var part);
                    limited.Add(new CareOp(row.Pawn, recipe ?? "", part));
                }
            }
            relaxed.Add(row with { Operations = Known<IReadOnlyList<SurgeryOperation>>(copied) });
        }
        if (limited.Count == 0)
        {
            return null;
        }
        var relaxedFacts = Known<IReadOnlyList<PrisonerFacts>>(relaxed);
        var h = SelectOrganHarvest(relaxedFacts, colony, needs, inFlight) ?? SelectPartRecovery(relaxedFacts, colony, parts, inFlight, facts);
        return h is not null && limited.Contains(new CareOp(h.Prisoner, h.Recipe, h.Part)) ? h : null;
    }

    /// <summary>
    /// Adds PrisonerSurgeryHerbal herbal medicine to needs while a sale harvest or stock recovery (the ones
    /// that hold MaintainSurgery open) is blocked only by the prisoner care limit.
    /// </summary>
    public static IReadOnlyDictionary<Resource, long> PrisonerHerbalNeeds(IReadOnlyDictionary<Resource, long> needs, RoundsFacts f, Fact<bool> silverShort)
    {
        var stock = new Dictionary<Resource, long>();
        if (f.Resources.TryGetValue(out var rows))
        {
            foreach (var row in rows)
            {
                stock[row.Resource] = stock.GetValueOrDefault(row.Resource) + row.Count;
            }
        }
        var sale = OrganNeeds(Unknown<IReadOnlyList<CarePawn>>(), [], Positive(silverShort), stock, f.Recipes);
        if (CareLimitedHarvest(f.Prisoners, f.PrisonerColony, sale, [], null, f.Recipes) is null)
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
        return ResourceConcernTargets(needs, new Dictionary<Resource, long> { [herbal] = PolicyConstants.PrisonerSurgeryHerbal });
    }
}
