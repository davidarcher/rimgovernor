using System.Globalization;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the prisoners family reads (#3262): the prisoner census with the colony facts a prisoner's use is judged by,
/// the food runway a release waits on, and the custody census of every observed humanlike. Each is a complete
/// native read; an unknown one raises <see cref="RequiredFactException"/> for its Need.
/// </summary>
public sealed record PrisonersFacts
{
    public EquatableArray<PrisonerFacts> Prisoners { get; init; } = [];

    public PrisonerColony Colony { get; init; } = new();

    public FoodRunway? FoodDays { get; init; }

    public EquatableArray<CustodyFacts> Custody { get; init; } = [];

    /// <summary>The facts a review already holds; a review without the population census raises <see cref="RequiredFactException"/>.</summary>
    public static PrisonersFacts From(RoundsFacts f) => f.Population.TryGetValue(out var census)
        ? new() { Prisoners = census.Prisoners, Colony = census.Colony, FoodDays = f.FoodDays, Custody = census.Custody }
        : throw new RequiredFactException("population census");
}

/// <summary>
/// The prisoners family (#3262): the prisoner half of the old population Concern; the
/// joiners and anomaly families are the rest (#3302).
/// <list type="bullet">
/// <item><c>prisoners/recruit</c> (Development): every living prisoner has the interaction the colony has a use for
/// (recruit, convert, enslave or release, <see cref="PopulationRules.PrisonerUse"/>). Each Method sets one
/// prisoner's interaction, named <c>prisoner-&lt;mode&gt;-&lt;pawn&gt;</c>, one per prisoner owed a change; the runtime also stages the jail (ring,
/// bed, prisoner flag) while the Need is unmet.</item>
/// <item><c>prisoners/custody</c> (Wellbeing): no downed humanlike is owed a capture or rescue
/// (<see cref="PopulationRules.CustodyEligible"/>). Both are held: a rescue belongs to the patient's medical/critical Need
/// owner and a capture needs shared forecast support.</item>
/// </list>
/// The two Needs are independent; neither waits on the other.
/// </summary>
public sealed class PrisonersFamily(PrisonerPolicy policy, bool recruit = true, bool custody = true) : IFamily<PrisonersFacts>
{
    public const string FamilyId = "prisoners";

    public static readonly NeedId Recruit = NeedId.Of(FamilyId, "recruit");

    public static readonly NeedId Custody = NeedId.Of(FamilyId, "custody");

    /// <summary>The stable refusal subject of a downed guest or refugee to rescue.</summary>
    public const string RescueHeld = "dependent_rescue_incident_owner";

    /// <summary>The stable refusal subject of a downed hostile to capture.</summary>
    public const string CaptureHeld = "unsupported_forecast_population_capture";

    public string Id => FamilyId;

    public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Pawns, FactFamily.Colony, FactFamily.Rooms];

    public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [];

    public IReadOnlyList<Need> Derive(PrisonersFacts facts)
    {
        var needs = new List<Need>(2);
        if (custody)
        {
            needs.Add(CustodyNeed(facts.Custody));
        }
        if (recruit)
        {
            needs.Add(RecruitNeed(facts, policy));
        }
        return needs;
    }

    public IReadOnlyList<Need> DeriveSeriousHarm(PrisonersFacts facts) => [];

    public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, PrisonersFacts facts)
    {
        var output = new List<NeedMethod>();
        foreach (var need in ready)
        {
            if (need.Id == Recruit)
            {
                // One Method per prisoner owed a change, lowest pawn first: each claims only its prisoner, so they
                // run side by side.
                foreach (var row in facts.Prisoners.OrderBy(r => r.Pawn))
                {
                    if (PopulationRules.PrisonerUse(row, facts.Colony, facts.FoodDays, policy) is not { } mode)
                    {
                        continue;
                    }
                    var interaction = new PrisonerInteraction(row.Pawn, mode);
                    output.Add(new NeedMethod(Recruit, InteractionMethod(row.Pawn, mode), "",
                        id => new PlanSpec(id, new PlanRevision(1), [DomainAction.NewPrisonerInteractionAction(new ActionId(id.Value + "-0"), interaction)])));
                }
            }
            else if (need.Id == Custody && PopulationRules.SelectCustodyMethod(facts.Custody) is { } held)
            {
                output.Add(held.Decision == CustodyDecision.Rescue
                    ? NeedMethod.Held(Custody, RescueHeld, "Critical eligible dependent rescue belongs to the patient's medical/critical Need; other custody transport requires shared forecast support.")
                    : NeedMethod.Held(Custody, CaptureHeld, "Population acquisition requires shared participant and custody outcome alternatives."));
            }
        }
        return output;
    }

    /// <summary>The MethodId of setting <paramref name="pawn"/>'s interaction to <paramref name="mode"/>: the same change continues its open Method.</summary>
    public static MethodId InteractionMethod(PawnId pawn, PrisonerInteractionMode mode) =>
        new("prisoner-" + mode.ToString().ToLowerInvariant() + "-" + pawn.Value);

    private static Need RecruitNeed(PrisonersFacts facts, PrisonerPolicy policy)
    {
        var owed = facts.Prisoners.Count(row => PopulationRules.PrisonerUse(row, facts.Colony, facts.FoodDays, policy) is not null);
        return new Need
        {
            Id = Recruit,
            Tier = NeedTier.Development,
            Satisfaction = owed > 0 ? Satisfaction.Unmet : Satisfaction.Met,
            Deficit = (double)owed,
        };
    }

    private static Need CustodyNeed(IReadOnlyList<CustodyFacts> census)
    {
        var candidates = census.Count(row => PopulationRules.CustodyEligible(row) is not null);
        return new Need
        {
            Id = Custody,
            Tier = NeedTier.Wellbeing,
            Satisfaction = candidates > 0 ? Satisfaction.Unmet : Satisfaction.Met,
            Deficit = (double)candidates,
        };
    }
}
