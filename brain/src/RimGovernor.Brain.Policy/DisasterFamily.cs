namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the disaster family reads (#3304): the sheltering moves the hazards call for, the review's disaster history,
/// and the recovery census the service step selects from.
/// </summary>
public sealed record DisasterFacts
{
    /// <summary>The allowed-area moves <see cref="ShelterRules.PlanSheltering"/> wants now.</summary>
    public EquatableArray<AllowedAreaChange> Changes { get; init; } = [];

    /// <summary>The review's disaster history; null when none was observed, or none is open after a restore.</summary>
    public DisasterHistory? Disaster { get; init; }

    /// <summary>Whether the environmental conditions were read: a missing history is met only then.</summary>
    public bool ConditionsKnown { get; init; }

    /// <summary>The recovery census: area restrictions, workers and the buildings the services restore.</summary>
    public RecoveryPlanning Planning { get; init; } = new();

    /// <summary>The colonists' work rows: the area step's live pawns and the service step's running jobs.</summary>
    public Fact<EquatableArray<WorkPawn>> Workers { get; init; }

    /// <summary>
    /// The family's facts from one reading of the colony and the review's <paramref name="history"/>; a shelter
    /// trigger not known clear raises <see cref="RequiredFactException"/>.
    /// </summary>
    public static DisasterFacts From(RoundsFacts f, DisasterHistory? history)
    {
        var (_, known) = ShelterRules.ShelterTriggerOf(f);
        if (!known)
        {
            throw new RequiredFactException("sheltering triggers");
        }
        return new DisasterFacts
        {
            Changes = EquatableArray.From(ShelterRules.PlanSheltering(f)),
            Disaster = history,
            ConditionsKnown = f.DisasterConditions.IsKnown,
            Planning = new RecoveryPlanning { Safety = f.RecoverySafety, Workers = f.RecoveryWorkers, Buildings = f.RecoveryBuildings },
        };
    }
}

/// <summary>
/// The disaster family (#3304). It replaced the disaster recovery incident Concern, its inspection and planner:
/// <list type="bullet">
/// <item><c>disaster/sheltering</c> (SeriousHarm): no colonist or animal waits on an allowed-area move a hazard calls
/// for (toxic fallout, a cold snap or heat wave outside the comfort range, hostiles on the map, the danger window's
/// haulers), and none is left restricted once the trigger is known clear. It runs in the immediate pass; each move is
/// its own Method (<see cref="AreaMethod"/>), and an area-only assignment claims only that pawn's allowed area, so it
/// is admitted beside open service work.</item>
/// <item><c>disaster/services</c> (Wellbeing): the services a disaster took down (power, fuel, broken-down and
/// damaged buildings) are restored. It waits on sheltering, and its Methods are the selection's repair, breakdown and
/// refuel services, named by their candidate's hash.</item>
/// </list>
/// </summary>
public sealed class DisasterFamily : IFamily<DisasterFacts>
{
    public const string FamilyId = "disaster";

    public static readonly NeedId Sheltering = NeedId.Of(FamilyId, "sheltering");

    public static readonly NeedId Services = NeedId.Of(FamilyId, "services");

    public string Id => FamilyId;

    public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Pawns, FactFamily.Emergency, FactFamily.Colony];

    public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [FactFamily.Pawns, FactFamily.Emergency, FactFamily.Colony];

    public IReadOnlyList<Need> Derive(DisasterFacts facts) => [.. DeriveSeriousHarm(facts), ServicesNeed(facts.Disaster, facts.ConditionsKnown)];

    /// <summary>Sheltering, the SeriousHarm Need.</summary>
    public IReadOnlyList<Need> DeriveSeriousHarm(DisasterFacts facts) => [ShelteringNeed(facts.Changes)];

    /// <summary>Nothing pure: both steps fence and commit the read they act on.</summary>
    public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, DisasterFacts facts) => [];

    /// <summary>Unmet while any area move is owed, met otherwise.</summary>
    public static Need ShelteringNeed(IReadOnlyList<AllowedAreaChange> owed)
    {
        return new Need
        {
            Id = Sheltering,
            Tier = NeedTier.SeriousHarm,
            Satisfaction = owed.Count > 0 ? Satisfaction.Unmet : Satisfaction.Met,
            Deficit = Fact.Known((double)owed.Count),
        };
    }

    /// <summary>
    /// The disaster history's finding (<see cref="RecoveryRules.RecoveryNeed"/>): a missing history is met when the
    /// conditions were read; unread conditions or an unclear finding raise <see cref="RequiredFactException"/>. It waits
    /// on sheltering.
    /// </summary>
    public static Need ServicesNeed(DisasterHistory? history, bool conditionsKnown)
    {
        if (history is null && !conditionsKnown)
        {
            throw new RequiredFactException("disaster conditions");
        }
        var finding = RecoveryRules.RecoveryNeed(history);
        if (finding == Finding.Unclear)
        {
            throw new RequiredFactException("disaster service finding");
        }
        var met = finding == Finding.Met;
        return new Need
        {
            Id = Services,
            Tier = NeedTier.Wellbeing,
            Preconditions = [Sheltering],
            Satisfaction = met ? Satisfaction.Met : Satisfaction.Unmet,
            Deficit = Fact.Known(met ? 0.0 : 1.0),
        };
    }

    /// <summary>
    /// A sheltering move's Method, named by its target: <c>area-</c>, the pawn or animal, and the area it is moved
    /// into (<c>unrestricted</c> for none). A repeat of an open move continues it.
    /// </summary>
    public static MethodId AreaMethod(AllowedAreaChange change) =>
        new("area-" + change.Pawn.Value + "-" + (change.Area.Length == 0 ? "unrestricted" : change.Area));
}
