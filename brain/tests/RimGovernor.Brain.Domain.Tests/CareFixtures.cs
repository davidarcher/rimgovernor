using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Hand-built medical reads (#3788): a living patient, a whole-body hediff and an operation one doctor performs.</summary>
internal static class CareFixtures
{
    /// <summary>A living colonist on Best care, nothing life-threatening, no surgery queued.</summary>
    public static CarePawn Patient(string id, IEnumerable<Hediff>? conditions = null, IEnumerable<CareOperation>? operations = null) => new()
    {
        Id = new PawnId(id),
        Care = MedicalCare.Best,
        LifeThreatening = false,
        Dead = false,
        NeedsRest = false,
        BadConditions = false,
        QueuedSurgeries = 0,
        Conditions = EquatableArray.From(conditions ?? []),
        Operations = EquatableArray.From(operations ?? []),
    };

    /// <summary>A visible, untended, whole-body hediff.</summary>
    public static Hediff Condition(string def) => new()
    {
        DefName = def,
        Part = null,
        Severity = 0,
        Visible = true,
        Tend = new HediffTend.Untended(),
        Immunizable = null,
    };

    /// <summary>An operation on a part, neither a violation nor lethal, at the given chance (95% with one unnamed doctor by default).</summary>
    public static CareOperation Operation(SurgeryKind kind, string recipe, int part, string body, bool stocked, string item = "", double tier = 0, SurgeryChance? chance = null, int doctors = 1) => new()
    {
        Item = new Resource(item),
        Tier = tier,
        Operation = new SurgeryOperation
        {
            Recipe = recipe,
            Part = new BodyPartRef { Index = part, DefName = body },
            Kind = kind,
            Chance = chance ?? new SurgeryChance.Rated(0.95, 0.95, new Dictionary<string, double>()),
            EligibleDoctors = doctors,
            IngredientsOnMap = stocked,
            Violation = false,
            Lethal = false,
            Yield = null,
            AddedPartHediff = null,
            MedicineMarketValue = 0,
            MedicineCareLimited = false,
        },
    };
}
