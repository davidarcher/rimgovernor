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

    /// <summary>The colonists' work profiles (<see cref="PawnRules.Profiles"/>): who can tame, train and master.</summary>
    public Fact<EquatableArray<PawnProfile>> Handlers { get; init; }

    /// <summary>The pen-bound animals not yet contained, released or slaughtered (<see cref="AnimalUpkeepRules.ReviewAnimalUpkeep"/>).</summary>
    public Fact<EquatableArray<PawnId>> Uncontained { get; init; }

    /// <summary>The next pen step (<see cref="HerdRoomsRules.NextPenStep"/>); a no-op step when nothing is owed.</summary>
    public Fact<HerdStep> Pen { get; init; }

    /// <summary>The next hospital ward step (<see cref="HerdRoomsRules.NextWardStep"/>); a no-op step when nothing is owed.</summary>
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
            needs.Add(AnimalsFamilyRules.HerdNeedOf(facts));
        }
        if (containment)
        {
            needs.Add(AnimalsFamilyRules.ContainmentNeedOf(facts));
            needs.Add(AnimalsFamilyRules.WardNeedOf(facts));
        }
        if (herd)
        {
            needs.Add(AnimalsFamilyRules.SterilizeNeedOf(facts, containment));
        }
        return needs;
    }

    public IReadOnlyList<Need> DeriveSeriousHarm(AnimalsFacts facts) => [];

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
                var choice = AnimalsFamilyRules.HerdChoice(facts);
                if (choice is HusbandryChoice.Unread)
                {
                    output.Add(NeedMethod.Held(Herd, HusbandryCensus, "An animal's husbandry facts are unread."));
                }
                else if (choice is HusbandryChoice.Write write)
                {
                    output.Add(Write(Herd, write));
                }
            }
            else if (need.Id == Sterilize)
            {
                var choice = HerdSterilizeRules.SterilizeChoice(facts.Animals, facts.Herd, facts.WardRoom);
                output.Add(choice is not HusbandryChoice.Write sterilize
                    ? NeedMethod.Held(Sterilize, SterilizeWaiting, "The hospital ward is not ready, or an animal's sterilize bill is still queued.")
                    : Write(Sterilize, sterilize));
            }
        }
        return output;
    }

    /// <summary>The MethodId of a husbandry write: the method and the animal, and a training's trainable.</summary>
    private static MethodId HusbandryMethodId(HusbandryChoice.Write choice) =>
        new(choice.Method == HusbandryMethod.Train
            ? $"{Wire.Name(choice.Method)}-{choice.Animal}-{choice.Argument}"
            : $"{Wire.Name(choice.Method)}-{choice.Animal}");

    private static NeedMethod Write(NeedId need, HusbandryChoice.Write choice)
    {
        var husbandry = new Husbandry(choice.Animal, choice.Method, choice.Argument);
        return new NeedMethod(need, HusbandryMethodId(choice), "", id =>
            new PlanSpec(id, new PlanRevision(1), [DomainAction.NewHusbandryAction(new ActionId($"{id.Value}-0"), husbandry)]));
    }
}

public static class AnimalsFamilyRules
{
    /// <summary>
    /// The herd's one husbandry write (husbandry_method's single write per cycle): a removal the herd plan owes,
    /// then training and taming, the food plan's slaughter and tame, then mastering. Sterilizing is the sterilize
    /// Need's. Unknown when the removal reads an unknown row.
    /// </summary>
    public static HusbandryChoice HerdChoice(AnimalsFacts f)
    {
        var choice = HerdRemovalRules.ReconcileHerdRemoval(f.Animals, f.Herd, f.FoodPlan);
        if (choice is HusbandryChoice.NoDeficit)
        {
            choice = HusbandryUpkeepRules.SelectHusbandryMethod(f.Animals, f.Wild, f.FeedShort, f.Herd, f.Handlers);
        }
        if (choice is HusbandryChoice.NoDeficit)
        {
            choice = AnimalFoodRules.FoodSlaughterChoice(f.FoodPlan, f.Animals, f.Herd);
        }
        if (choice is HusbandryChoice.NoDeficit)
        {
            choice = AnimalAcquireRules.FoodTameChoice(f.FoodPlan, f.Wild, f.FeedShort, f.Handlers);
        }
        if (choice is HusbandryChoice.NoDeficit)
        {
            choice = HerdMasterRules.HerdMasterChoice(f.Animals, f.Herd, f.Handlers);
        }
        return choice;
    }

    /// <summary>
    /// The herd Need, a plain threshold (no always-held flag): unmet while the herd plan owes training, taming or
    /// removal (AnimalHerdDeficit, ReconcileHerdRemoval), the food plan a slaughter or a tame, or an animal a master;
    /// met otherwise. With nothing owed, an unread herd deficit or a choice chain (<see cref="HerdChoice"/>) that reads
    /// an unread row raises <see cref="RequiredFactException"/>.
    /// </summary>
    public static Need HerdNeedOf(AnimalsFacts f)
    {
        var deficit = HusbandryUpkeepRules.AnimalHerdDeficit(f.Animals, f.Wild, f.FeedShort, f.Herd);
        var removal = HerdRemovalRules.ReconcileHerdRemoval(f.Animals, f.Herd, f.FoodPlan);
        var slaughter = AnimalFoodRules.FoodSlaughterChoice(f.FoodPlan, f.Animals, f.Herd) is HusbandryChoice.Write { Method: HusbandryMethod.Slaughter };
        var tame = AnimalAcquireRules.FoodTameChoice(f.FoodPlan, f.Wild, f.FeedShort, f.Handlers) is HusbandryChoice.Write { Method: HusbandryMethod.Tame };
        var master = HerdMasterRules.HerdMasterChoice(f.Animals, f.Herd, f.Handlers) is HusbandryChoice.Write;
        var owed = RoundsRules.Positive(deficit) || removal is HusbandryChoice.Write || slaughter || tame || master;
        if (!owed && !deficit.IsKnown)
        {
            throw new RequiredFactException("herd deficit");
        }
        if (!owed && HerdChoice(f) is HusbandryChoice.Unread)
        {
            throw new RequiredFactException("husbandry census");
        }
        return new Need
        {
            Id = AnimalsFamily.Herd,
            Tier = NeedTier.Development,
            Satisfaction = owed ? Satisfaction.Unmet : Satisfaction.Met,
            Deficit = Known(owed ? 1.0 : 0.0),
        };
    }

    /// <summary>
    /// The containment Need, a plain threshold (no latch): unmet while a pen-bound animal is uncontained or a pen
    /// step is owed; met otherwise (a colony with no planned pen or no animals has a no-op pen step). With nothing
    /// owed, either one unread raises <see cref="RequiredFactException"/>. Its deficit is the uncontained animals,
    /// plus one for an owed pen.
    /// </summary>
    public static Need ContainmentNeedOf(AnimalsFacts f)
    {
        var known = f.Uncontained.TryGetValue(out var uncontained);
        var penKnown = f.Pen.TryGetValue(out var pen);
        var penOwed = penKnown && pen!.Owed();
        var count = known ? uncontained!.Count : 0;
        var owed = count > 0 || penOwed;
        if (!owed && !known)
        {
            throw new RequiredFactException("uncontained animals");
        }
        if (!owed && !penKnown)
        {
            throw new RequiredFactException("pen step");
        }
        return new Need
        {
            Id = AnimalsFamily.Containment,
            Tier = NeedTier.Wellbeing,
            Satisfaction = owed ? Satisfaction.Unmet : Satisfaction.Met,
            Deficit = Known(count + (penOwed ? 1.0 : 0.0)),
        };
    }

    /// <summary>The ward Need: unmet while a ward step is owed (an animal bed to build or flag medical); an unread step raises <see cref="RequiredFactException"/>.</summary>
    public static Need WardNeedOf(AnimalsFacts f)
    {
        if (!f.Ward.TryGetValue(out var step))
        {
            throw new RequiredFactException("ward step");
        }
        var owed = step.Owed();
        return new Need
        {
            Id = AnimalsFamily.Ward,
            Tier = NeedTier.Wellbeing,
            Satisfaction = owed ? Satisfaction.Unmet : Satisfaction.Met,
            Deficit = Known(owed ? 1.0 : 0.0),
        };
    }

    /// <summary>
    /// The sterilize Need, waiting on the ward when the family derives it: unmet while an animal the herd plan wants
    /// sterilized is fertile or an animal sits in the hospital area; met otherwise (a map with no hospital area holds
    /// no animal in it). An unread census raises <see cref="RequiredFactException"/>. Its deficit is the animals wanted.
    /// </summary>
    public static Need SterilizeNeedOf(AnimalsFacts f, bool ward = true)
    {
        if (!f.Animals.TryGetValue(out var animals))
        {
            throw new RequiredFactException("animal census");
        }
        var wanted = HerdSterilizeRules.SterilizeWanted(animals, f.Herd).Count;
        var area = f.WardRoom.Area;
        var inWard = area.Length != 0 && animals.Any(a => a.AllowedArea == area);
        var owed = wanted > 0 || inWard;
        return new Need
        {
            Id = AnimalsFamily.Sterilize,
            Tier = NeedTier.Development,
            Satisfaction = owed ? Satisfaction.Unmet : Satisfaction.Met,
            Deficit = Known((double)wanted),
            Preconditions = ward ? [AnimalsFamily.Ward] : [],
        };
    }
}
