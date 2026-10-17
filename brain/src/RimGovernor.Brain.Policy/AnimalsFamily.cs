using System.Globalization;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the animals family reads (#3260): the herd census and the factionless census it tames from, the herd plan,
/// the feed runway's shortfall, the food plan, the colonists' handling profiles, the pen-bound animals not yet
/// contained, the next pen and ward steps of the herd rooms and the hospital ward's readiness and area. A step is
/// unknown while any planning fact behind it is unread.
/// </summary>
public sealed record AnimalsFacts
{
    public Fact<EquatableArray<UpkeepAnimal>> Animals { get; init; }

    public Fact<EquatableArray<UpkeepAnimal>> Wild { get; init; }

    public HerdPolicy Herd { get; init; } = new();

    public Fact<bool> FeedShort { get; init; }

    public Fact<FoodPlan> FoodPlan { get; init; }

    /// <summary>The colonists' work profiles (<see cref="PolicyFunctions.Profiles"/>): who can tame, train and master.</summary>
    public Fact<EquatableArray<PawnProfile>> Handlers { get; init; }

    /// <summary>The pen-bound animals not yet contained, released or slaughtered (<see cref="PolicyFunctions.ReviewAnimalUpkeep"/>).</summary>
    public Fact<EquatableArray<PawnId>> Uncontained { get; init; }

    /// <summary>The next pen step (<see cref="PolicyFunctions.NextPenStep"/>); a no-op step when nothing is owed.</summary>
    public Fact<HerdStep> Pen { get; init; }

    /// <summary>The next hospital ward step (<see cref="PolicyFunctions.NextWardStep"/>); a no-op step when nothing is owed.</summary>
    public Fact<HerdStep> Ward { get; init; }

    /// <summary>Whether the ward stands with a medical animal bed, and the hospital's allowed area.</summary>
    public AnimalWard WardRoom { get; init; } = new();
}

/// <summary>
/// The animals family (#3260). It replaced the MaintainHerd and MaintainAnimalContainment Concerns, their
/// inspections and planners:
/// <list type="bullet">
/// <item><c>animals/herd</c> (Development): the herd plan holds. Unmet while an animal is owed training, a race is
/// under its floor with a tameable wild animal, over its cap with a removable animal, or the food plan asks for a
/// slaughter or a tame, or an animal is owed a master. Its Method is the one husbandry write the choice chain
/// picks (removal, training and taming, the food plan's slaughter and tame, then mastering), named
/// <c>&lt;method&gt;-&lt;animal&gt;[-&lt;trainable&gt;]</c>; the animal is its claim.</item>
/// <item><c>animals/containment</c> (Wellbeing): every pen-bound animal is penned and every planned pen matches the
/// plan. The pens are built by the family's work step through the shared building planner; once they stand the
/// Need waits on native delivery.</item>
/// <item><c>animals/ward</c> (Wellbeing): the hospital holds the animal beds the herd needs, each flagged medical.
/// Built by the work step.</item>
/// <item><c>animals/sterilize</c> (Development, waits on <c>animals/ward</c>): no animal the herd plan wants
/// sterilized is fertile and no animal sits in the ward's area without cause. Its Method lets one animal into
/// the hospital, queues its bill or lets it out.</item>
/// </list>
/// </summary>
public sealed class AnimalsFamily(bool herd = true, bool containment = true) : IFamily<AnimalsFacts>
{
    public const string FamilyId = "animals";

    public static readonly NeedId Herd = NeedId.Of(FamilyId, "herd");

    public static readonly NeedId Containment = NeedId.Of(FamilyId, "containment");

    public static readonly NeedId Ward = NeedId.Of(FamilyId, "ward");

    public static readonly NeedId Sterilize = NeedId.Of(FamilyId, "sterilize");

    /// <summary>The hold of a ready herd Need whose choice reads an unknown census row.</summary>
    public const string HusbandryCensus = "husbandry_census";

    /// <summary>The hold of a ready sterilize Need while the ward is not ready or an animal's bill is still queued.</summary>
    public const string SterilizeWaiting = "sterilize_ward";

    public string Id => FamilyId;

    public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Pawns, FactFamily.Colony, FactFamily.Rooms];

    public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [];

    /// <summary>The herd and sterilize Needs with the husbandry toggle, the containment and ward Needs with the containment toggle.</summary>
    public IReadOnlyList<Need> Derive(AnimalsFacts facts)
    {
        var needs = new List<Need>(4);
        if (herd)
        {
            needs.Add(PolicyFunctions.HerdNeedOf(facts));
        }
        if (containment)
        {
            needs.Add(PolicyFunctions.ContainmentNeedOf(facts));
            needs.Add(PolicyFunctions.WardNeedOf(facts));
        }
        if (herd)
        {
            needs.Add(PolicyFunctions.SterilizeNeedOf(facts, containment));
        }
        return needs;
    }

    /// <summary>
    /// The herd's one husbandry write and the sterilize step. Containment and the ward build in the work step, so
    /// they propose nothing here.
    /// </summary>
    public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, AnimalsFacts facts)
    {
        var output = new List<NeedMethod>();
        foreach (var need in ready)
        {
            if (need.Id == Herd)
            {
                var choice = PolicyFunctions.HerdChoice(facts);
                if (choice.Reason == HusbandryPlanReason.Unknown)
                {
                    output.Add(NeedMethod.Held(Herd, HusbandryCensus, "An animal's husbandry facts are unread."));
                }
                else if (choice.Method is not null)
                {
                    output.Add(Write(Herd, choice));
                }
            }
            else if (need.Id == Sterilize)
            {
                var choice = PolicyFunctions.SterilizeChoice(facts.Animals, facts.Herd, facts.WardRoom);
                output.Add(choice.Method is null
                    ? NeedMethod.Held(Sterilize, SterilizeWaiting, "The hospital ward is not ready, or an animal's sterilize bill is still queued.")
                    : Write(Sterilize, choice));
            }
        }
        return output;
    }

    /// <summary>The MethodId of a husbandry write: the method and the animal, and a training's trainable.</summary>
    public static MethodId HusbandryMethodId(HusbandryChoice choice) =>
        new(choice.Method == HusbandryMethod.Train
            ? $"{Wire.Name(choice.Method!.Value)}-{choice.Animal}-{choice.TrainableDef}"
            : $"{Wire.Name(choice.Method!.Value)}-{choice.Animal}");

    private static NeedMethod Write(NeedId need, HusbandryChoice choice)
    {
        var argument = choice.Argument.Length != 0 ? choice.Argument : choice.TrainableDef;
        var husbandry = new Husbandry(choice.Animal, choice.Method!.Value, argument);
        return new NeedMethod(need, HusbandryMethodId(choice), "", id =>
            new PlanSpec(id, new PlanRevision(1), [DomainAction.NewHusbandryAction(new ActionId($"{id.Value}-0"), husbandry)]));
    }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// The herd's one husbandry write (husbandry_method's single write per cycle): a removal the herd plan owes,
    /// then training and taming, the food plan's slaughter and tame, then mastering. Sterilizing is the sterilize
    /// Need's. Unknown when the removal reads an unknown row.
    /// </summary>
    public static HusbandryChoice HerdChoice(AnimalsFacts f)
    {
        var choice = ReconcileHerdRemoval(f.Animals, f.Herd, f.FoodPlan);
        if (choice.Reason == HusbandryPlanReason.NoDeficit)
        {
            choice = SelectHusbandryMethod(f.Animals, f.Wild, f.FeedShort, f.Herd, f.Handlers);
        }
        if (choice.Reason == HusbandryPlanReason.NoDeficit)
        {
            choice = FoodSlaughterChoice(f.FoodPlan, f.Animals, f.Herd);
        }
        if (choice.Reason == HusbandryPlanReason.NoDeficit)
        {
            choice = FoodTameChoice(f.FoodPlan, f.Wild, f.FeedShort, f.Handlers);
        }
        if (choice.Reason == HusbandryPlanReason.NoDeficit)
        {
            choice = HerdMasterChoice(f.Animals, f.Herd, f.Handlers);
        }
        return choice;
    }

    /// <summary>
    /// The herd Need, a plain threshold (no always-held flag): unmet while the herd plan owes training, taming or
    /// removal (AnimalHerdDeficit, ReconcileHerdRemoval), the food plan a slaughter or a tame, or an animal a master;
    /// otherwise unknown while the herd deficit is unread or the choice chain (<see cref="HerdChoice"/>) reads an
    /// unknown row; met otherwise.
    /// </summary>
    public static Need HerdNeedOf(AnimalsFacts f)
    {
        var deficit = AnimalHerdDeficit(f.Animals, f.Wild, f.FeedShort, f.Herd);
        var removal = ReconcileHerdRemoval(f.Animals, f.Herd, f.FoodPlan);
        var slaughter = FoodSlaughterChoice(f.FoodPlan, f.Animals, f.Herd).Method == HusbandryMethod.Slaughter;
        var tame = FoodTameChoice(f.FoodPlan, f.Wild, f.FeedShort, f.Handlers).Method == HusbandryMethod.Tame;
        var master = HerdMasterChoice(f.Animals, f.Herd, f.Handlers).Method is not null;
        var owed = RoundsRules.Positive(deficit) || removal.Method is not null || slaughter || tame || master;
        var satisfaction = owed ? Satisfaction.Unmet
            : deficit.IsKnown && HerdChoice(f).Reason != HusbandryPlanReason.Unknown ? Satisfaction.Met
            : Satisfaction.Unknown;
        return new Need
        {
            Id = AnimalsFamily.Herd,
            Tier = NeedTier.Development,
            Satisfaction = satisfaction,
            Deficit = satisfaction == Satisfaction.Unknown ? Unknown<double>() : Known(owed ? 1.0 : 0.0),
            Evidence =
            [
                new("herd_deficit", deficit.TryGetValue(out var d) ? Known(d ? "owed" : "none") : Unknown<string>()),
                new("herd_removal", removal.Reason == HusbandryPlanReason.Unknown ? Unknown<string>() : Known(removal.Method is { } m ? Wire.Name(m) : "none")),
                new("food_slaughter", Known(slaughter ? "owed" : "none")),
                new("food_tame", Known(tame ? "owed" : "none")),
                new("master", Known(master ? "owed" : "none")),
            ],
        };
    }

    /// <summary>
    /// The containment Need, a plain threshold (no latch): unmet while a pen-bound animal is uncontained or a pen
    /// step is owed; unknown while either is unread; met otherwise. Its deficit is the uncontained animals, plus
    /// one for an owed pen.
    /// </summary>
    public static Need ContainmentNeedOf(AnimalsFacts f)
    {
        var known = f.Uncontained.TryGetValue(out var uncontained);
        var penKnown = f.Pen.TryGetValue(out var pen);
        var penOwed = penKnown && pen!.Owed();
        var count = known ? uncontained!.Count : 0;
        var satisfaction = count > 0 || penOwed ? Satisfaction.Unmet : known && penKnown ? Satisfaction.Met : Satisfaction.Unknown;
        return new Need
        {
            Id = AnimalsFamily.Containment,
            Tier = NeedTier.Wellbeing,
            Satisfaction = satisfaction,
            Deficit = satisfaction == Satisfaction.Unknown ? Unknown<double>() : Known(count + (penOwed ? 1.0 : 0.0)),
            Evidence =
            [
                new("uncontained_animals", known ? Known(count.ToString(CultureInfo.InvariantCulture)) : Unknown<string>()),
                new("pen_step", penKnown ? Known(pen!.Owed() ? "owed" : "none") : Unknown<string>()),
            ],
        };
    }

    /// <summary>The ward Need: unmet while a ward step is owed (an animal bed to build or flag medical), unknown while the step is unread.</summary>
    public static Need WardNeedOf(AnimalsFacts f)
    {
        var known = f.Ward.TryGetValue(out var step);
        var owed = known && step!.Owed();
        var satisfaction = !known ? Satisfaction.Unknown : owed ? Satisfaction.Unmet : Satisfaction.Met;
        return new Need
        {
            Id = AnimalsFamily.Ward,
            Tier = NeedTier.Wellbeing,
            Satisfaction = satisfaction,
            Deficit = known ? Known(owed ? 1.0 : 0.0) : Unknown<double>(),
            Evidence =
            [
                new("ward_step", known ? Known(owed ? Wire.Name(step!.Kind) : "none") : Unknown<string>()),
                new("ward_ready", f.WardRoom.Ready.TryGetValue(out var ready) ? Known(ready ? "ready" : "not_ready") : Unknown<string>()),
            ],
        };
    }

    /// <summary>
    /// The sterilize Need, waiting on the ward when the family derives it: unmet while an animal the herd plan wants
    /// sterilized is fertile or an animal sits in the hospital area; unknown while the census, or with nothing
    /// wanted the hospital area, is unread; met otherwise. Its deficit is the animals wanted.
    /// </summary>
    public static Need SterilizeNeedOf(AnimalsFacts f, bool ward = true)
    {
        var need = new Need { Id = AnimalsFamily.Sterilize, Tier = NeedTier.Development, Preconditions = ward ? [AnimalsFamily.Ward] : [] };
        if (!f.Animals.TryGetValue(out var animals))
        {
            return need with { Evidence = [new("sterilize_wanted", Unknown<string>())] };
        }
        var wanted = SterilizeWanted(animals, f.Herd).Count;
        var areaKnown = f.WardRoom.Area.TryGetValue(out var area);
        var inWard = areaKnown && area!.Length != 0 && animals.Any(a => a.AllowedArea.TryGetValue(out var held) && held == area);
        var satisfaction = wanted > 0 || inWard ? Satisfaction.Unmet : areaKnown ? Satisfaction.Met : Satisfaction.Unknown;
        return need with
        {
            Satisfaction = satisfaction,
            Deficit = satisfaction == Satisfaction.Unknown ? Unknown<double>() : Known((double)wanted),
            Evidence =
            [
                new("sterilize_wanted", Known(wanted.ToString(CultureInfo.InvariantCulture))),
                new("in_ward", areaKnown ? Known(inWard ? "yes" : "no") : Unknown<string>()),
            ],
        };
    }
}
