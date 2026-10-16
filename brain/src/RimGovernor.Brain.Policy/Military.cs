using System.Globalization;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The next pest hunt the census supports (#3259): the selected pest rows, at most the hunting budget. The runtime
/// selects them (<see cref="PolicyFunctions.SelectPestAcquisition"/>) and names the Method by the rows, so the same
/// selection continues its open Method.
/// </summary>
/// <param name="Method">The hunt's MethodId, <c>pest-hunt-&lt;hash of the rows&gt;</c>.</param>
/// <param name="Rows">The pest rows, one hunt each.</param>
public sealed record PestHunt(MethodId Method, EquatableArray<AcquisitionSource> Rows);

/// <summary>An undispatched pest hunt action of the Need's own whose animal left the wild-animal census.</summary>
public readonly record struct StaleHunt(PlanId Plan, ActionId Action);

/// <summary>
/// What the military family reads (#3259). Defense: the hostiles standing, the armed and unarmed fighters and the
/// colonist count the foothold sizes for. Pests: the pest census, the Need's own undispatched hunts whose animal
/// left the census, the next hunt and whether an outdoor hazard holds hunting. Training: the assigned combat
/// capability review. A section the run does not read stays unknown.
/// </summary>
public sealed record MilitaryFacts
{
    public Fact<long> Hostiles { get; init; }

    public Fact<long> Armed { get; init; }

    /// <summary>Living, conscious colonists able to fight who hold no weapon.</summary>
    public Fact<long> Unarmed { get; init; }

    /// <summary>The colonist count the foothold sizes for (<see cref="PolicyFunctions.FootholdCount"/>).</summary>
    public Fact<long> Foothold { get; init; }

    /// <summary>The pests in the wild-animal census (<see cref="PolicyFunctions.PestCensus"/>).</summary>
    public Fact<int> Pests { get; init; }

    /// <summary>The Need's undispatched hunts whose animal died or left the map: the work step cancels them.</summary>
    public EquatableArray<StaleHunt> StaleHunts { get; init; }

    /// <summary>The next pest hunt, or null when every pest is hunted, no hunter can, or the census is unread.</summary>
    public PestHunt? Hunt { get; init; }

    /// <summary>No pest hunt was selected for want of a hunter.</summary>
    public bool HuntersMissing { get; init; }

    /// <summary>The acquisition census could not be read for a selection.</summary>
    public bool HuntsUnread { get; init; }

    /// <summary>An outdoor hazard holds chop, hunt and forage like it holds remote work.</summary>
    public bool HuntingHeld { get; init; }

    public Fact<TrainingReview> Training { get; init; }

    /// <summary>The defense and training facts a review already holds; the pest section stays unread.</summary>
    public static MilitaryFacts From(RoundsFacts f) => new()
    {
        Hostiles = f.Hostiles,
        Armed = f.Armed,
        Unarmed = f.Unarmed,
        Foothold = PolicyFunctions.FootholdCount(f),
        Training = PolicyFunctions.ReviewTraining(f),
    };
}

/// <summary>
/// The military family (#3259): basic defense, pest clearing and combat training. It replaced the
/// EnsureBasicDefense, ClearPests and MaintainTraining Concerns, their inspections and the equip, pest acquisition
/// and training planners.
/// <list type="bullet">
/// <item><c>military/defense</c> (Wellbeing): no hostile stands, the foothold's armed fighters are armed, and no
/// fighting-capable colonist stands unarmed. Arming is not yet a supported forecast outcome, so an unmet Need is
/// held as <c>unsupported_forecast_equipment</c>.</item>
/// <item><c>military/pests</c> (Wellbeing): no pest in the wild-animal census and no stale hunt of the Need's own.
/// Its Method is the next pest hunt; hunts of other pests run side by side and a second hunt of one animal is
/// refused by its claim.</item>
/// <item><c>military/training</c> (Development): no assigned combat-capability holder is forecast below its
/// requirement. Its Methods are the range and ring rooms the runtime's work step builds.</item>
/// </list>
/// </summary>
public sealed class MilitaryFamily(bool defense = true, bool pests = true, bool training = true) : IFamily<MilitaryFacts>
{
    public const string FamilyId = "military";

    public static readonly NeedId Defense = NeedId.Of(FamilyId, "defense");

    public static readonly NeedId Pests = NeedId.Of(FamilyId, "pests");

    public static readonly NeedId Training = NeedId.Of(FamilyId, "training");

    /// <summary>The stable refusal subject of an unmet defense Need.</summary>
    public const string UnsupportedEquipment = "unsupported_forecast_equipment";

    public string Id => FamilyId;

    public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Colony, FactFamily.Pawns, FactFamily.Rooms, FactFamily.Emergency, FactFamily.World];

    public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [];

    public IReadOnlyList<Need> Derive(MilitaryFacts facts)
    {
        var needs = new List<Need>(3);
        if (defense)
        {
            needs.Add(DefenseNeed(facts));
        }
        if (pests)
        {
            needs.Add(PestsNeed(facts));
        }
        if (training)
        {
            needs.Add(TrainingNeed(facts.Training));
        }
        return needs;
    }

    /// <summary>Defense is held; pests proposes its next hunt unless hunting is held; training builds in the work step.</summary>
    public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, MilitaryFacts facts)
    {
        var output = new List<NeedMethod>();
        foreach (var need in ready)
        {
            if (need.Id == Defense)
            {
                output.Add(NeedMethod.Held(Defense, UnsupportedEquipment,
                    "Observed equipment demand requires shared forecast worker, timing and outcome alternatives."));
            }
            else if (need.Id == Pests && !facts.HuntingHeld && facts.Hunt is { Rows.Count: > 0 } hunt)
            {
                output.Add(new NeedMethod(Pests, hunt.Method, "pest " + hunt.Rows[0].Definition, id => HuntPlan(id, hunt)));
            }
        }
        return output;
    }

    /// <summary>
    /// The basic-defense predicate: the foothold's armed fighters (<see cref="PolicyFunctions.ArmedTarget"/>) with
    /// no hostile standing, and no fighting-capable colonist left unarmed; unknown when the facts do not say.
    /// </summary>
    public static Fact<bool> DefenseMet(MilitaryFacts f)
    {
        var armed = f.Foothold.TryGetValue(out var n) ? Measured(f.Armed, v => v >= Math.Min(2, n)) : Unknown<bool>();
        return PolicyFunctions.BasicDefenseRecovered(AllFacts(armed, Measured(f.Hostiles, v => v == 0)), f.Unarmed);
    }

    private static Need DefenseNeed(MilitaryFacts f)
    {
        var met = DefenseMet(f);
        var evidence = EquatableArray.From<NeedEvidence>([new("hostiles", Text(f.Hostiles)), new("armed", Text(f.Armed)), new("unarmed", Text(f.Unarmed))]);
        if (!met.TryGetValue(out var ok))
        {
            return new Need { Id = Defense, Tier = NeedTier.Wellbeing, Evidence = evidence };
        }
        var deficit = ok ? Known(0.0) : Unknown<double>();
        if (!ok && f.Foothold.TryGetValue(out var n) && f.Armed.TryGetValue(out var stock) && n > 0)
        {
            // The armed shortfall's share of the target; with the target armed, the unarmed share of the fighters.
            var target = PolicyFunctions.ArmedTarget(n);
            var share = Math.Max(0, (double)(target - stock) / target);
            if (f.Unarmed.TryGetValue(out var unarmed) && unarmed > 0 && share == 0)
            {
                share = (double)unarmed / (unarmed + stock);
            }
            deficit = Known(share);
        }
        return new Need { Id = Defense, Tier = NeedTier.Wellbeing, Satisfaction = ok ? Satisfaction.Met : Satisfaction.Unmet, Deficit = deficit, Evidence = evidence };
    }

    private static Need PestsNeed(MilitaryFacts f)
    {
        if (!f.Pests.TryGetValue(out var count))
        {
            return new Need { Id = Pests, Tier = NeedTier.Wellbeing, Evidence = [new("pests", default)] };
        }
        var owed = count + f.StaleHunts.Count;
        return new Need
        {
            Id = Pests,
            Tier = NeedTier.Wellbeing,
            Satisfaction = owed == 0 ? Satisfaction.Met : Satisfaction.Unmet,
            Deficit = Known((double)owed),
            Evidence = [new("pests", Count(count)), new("stale_hunts", Count(f.StaleHunts.Count))],
        };
    }

    private static Need TrainingNeed(Fact<TrainingReview> review)
    {
        if (!review.TryGetValue(out var r))
        {
            return new Need { Id = Training, Tier = NeedTier.Development, Evidence = [new("below_requirement", default)] };
        }
        return new Need
        {
            Id = Training,
            Tier = NeedTier.Development,
            Satisfaction = r.Below == 0 ? Satisfaction.Met : Satisfaction.Unmet,
            Deficit = Known(r.Below == 0 || r.Capable == 0 ? 0.0 : (double)r.Below / r.Capable),
            Evidence = [new("below_requirement", Count(r.Below)), new("capable", Count(r.Capable))],
        };
    }

    private static PlanSpec HuntPlan(PlanId id, PestHunt hunt) =>
        new(id, new PlanRevision(1), hunt.Rows.Select((row, i) =>
            DomainAction.NewAcquisitionAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id.Value}-{i}")), new Acquisition(row.Id, row.Resource, row.Cell))));

    private static Fact<string> Count(long n) => Known(n.ToString(CultureInfo.InvariantCulture));

    private static Fact<string> Text(Fact<long> v) => v.TryGetValue(out var n) ? Count(n) : default;
}
