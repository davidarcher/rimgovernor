using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Policy;

// Organ harvest: MaintainSurgery harvests an organ from a prisoner the colony would not recruit
// (PrisonerFacts.HarvestEligible) only for a concrete need, and only when the gain outweighs the cost, both
// in silver:
//
//   - a colonist's failing or missing natural organ that no stocked part can replace (a surgery_part_short
//     want whose natural recipe installs a harvestable organ): the gain is the capacity the install gives
//     back, servedSurgery's weight x SilverPerCapacity; the harvested organ then feeds the existing restore
//     or replacement install.
//   - a silver runway deficit (SilverShort) with no harvestable organ already stocked: the gain is the
//     organ's native market value; the organ sells through the routine trade as surplus while the deficit
//     holds.
//
// The cost is the colony mood thoughts vanilla's OrganUse precept gives, SilverPerMoodPoint x magnitude x
// affected colonists, plus the prisoner's faction goodwill change x SilverPerGoodwillPoint. An Abhorrent (or
// unrecognized) precept refuses outright. Only a paired organ with both present is taken: never a heart,
// liver or last lung or kidney, and never a harvest native reports lethal.

public static partial class PolicyConstants
{
    // Silver prices of the cost model.
    //
    // SilverPerMoodPoint: one mood point on one colonist for a thought's whole duration (vanilla's harvest
    // thoughts last 8 days). Twenty silver makes a classic harvest in a five-colonist colony (-5 x 5) cost
    // 500, about half a kidney's 900: harvesting is worth it for a small colony, and with a faction's
    // goodwill hit on top it is refused from seven colonists (1050).
    //
    // SilverPerGoodwillPoint: vanilla's -70 harvest report costs 350 silver, the rough value of the trade
    // and raid-relief a faction's lost goodwill gives up.
    //
    // SilverPerCapacity: a full body part's capacity (partWeight 1, a kidney or lung) given back to a
    // colonist, priced above any organ's market value so a colonist's need outranks a sale.
    public const double SilverPerMoodPoint = 20.0;
    public const double SilverPerGoodwillPoint = 5.0;
    public const double SilverPerCapacity = 1500.0;

    // Core's KnowGuestOrganHarvested thought on every colonist (Thoughts_Memory_Misc.xml): the cost of a
    // harvest in a game without the Ideology DLC, which has no precepts to read. With Ideology the precepts'
    // own thoughts price it (HarvestMood).
    internal const int ClassicKnowHarvestMood = 5;

    // The history events a harvest raises: the organ taken, and the organ sold.
    internal const string HarvestedOrganEvent = "HarvestedOrgan";
    internal const string SoldOrganEvent = "SoldOrgan";

    // Artificial part recovery: added part hediffs never removed: worth nothing (a peg leg or wooden
    // hand/foot gives 1 wood, dentures nothing) or not removable at all (joywire, death acidifier,
    // mindscrew).
    internal static readonly HashSet<string> UnrecoveredParts = new(StringComparer.Ordinal)
    {
        "PegLeg", "WoodenHand", "WoodenFoot", "Denture", "Joywire", "DeathAcidifier", "Mindscrew",
    };

    // Body parts whose added part stays although no vital tag rides on it (RecipeFacts.VitalParts holds the
    // rest, a heart among them): a spine leaves the prisoner helpless.
    internal static readonly HashSet<string> KeptBodyParts = new(StringComparer.Ordinal) { "Spine" };
}

/// <summary>
/// One concrete need a harvest answers: a colonist's install (For set, Gain its capacity in silver) or a sale
/// (For empty; the gain is the harvested organ's market value).
/// </summary>
public sealed record OrganNeed(string Organ, PawnId For, double Gain);

/// <summary>
/// One prisoner removal to queue as a medical ProductionBillIntent: an organ harvest, or an added part
/// recovery (Organ names the part item). Violation sets acknowledge_violation. Step is a peg-leg cycling
/// step (default otherwise); Surgeon restricts a training step's bill to the doctor who needs the XP (empty
/// keeps vanilla's choice).
/// </summary>
public sealed record OrganHarvest(
    Domain.PawnId Prisoner,
    string Recipe,
    int Part,
    string Organ,
    PawnId For,
    double Gain,
    double Cost,
    bool Violation,
    PegCycleStep Step,
    Domain.PawnId Surgeon);

public static partial class PolicyFunctions
{
    /// <summary>
    /// The colony mood points a harvest costs: the precept rule's verdict on the HarvestedOrgan event, plus
    /// SoldOrgan when the organ is sold; the doer's cost once, the witnesses' on every colonist. Ok is false
    /// when a precept refuses (any chance of refusal) or the ideoligion is unread. A game without Ideology
    /// has no precepts: Core's thought applies.
    /// </summary>
    public static (double Mood, bool Ok) HarvestMood(PrisonerColony c, bool sale)
    {
        var n = (double)Math.Max(0, c.Colonists);
        if (!c.IdeologyActive)
        {
            return (PolicyConstants.ClassicKnowHarvestMood * n, true);
        }
        var events = new List<string> { PolicyConstants.HarvestedOrganEvent };
        if (sale)
        {
            events.Add(PolicyConstants.SoldOrganEvent);
        }
        double mood = 0;
        foreach (var evt in events)
        {
            var v = ActionStance(c.Ideology, new PreceptAction { HistoryEvent = evt }, new PreceptSubject());
            if (v.Stance == PreceptStance.Unknown || v.RefusalChance > 0)
            {
                return (0, false);
            }
            mood += v.DoerMoodCost + v.WitnessMoodCost * n;
        }
        return (mood, true);
    }

    /// <summary>
    /// The silver a harvest costs the colony: mood at SilverPerMoodPoint plus the goodwill change (&lt;= 0) at
    /// SilverPerGoodwillPoint. Ok is false when the precept refuses.
    /// </summary>
    public static (double Cost, bool Ok) HarvestCost(PrisonerColony c, int goodwill, bool sale)
    {
        var (mood, ok) = HarvestMood(c, sale);
        return (PolicyConstants.SilverPerMoodPoint * mood + PolicyConstants.SilverPerGoodwillPoint * Math.Max(0, -goodwill), ok);
    }

    /// <summary>
    /// Lists the colonists' organ needs, then one sale need per organ while silver is short and none of it is
    /// stocked. A colonist's need is a part-short want whose natural recipe installs a harvestable organ none
    /// of which is stocked.
    /// </summary>
    public static IReadOnlyList<OrganNeed> OrganNeeds(
        Fact<IReadOnlyList<CarePawn>> pawns,
        IReadOnlyList<SurgeryWant> wants,
        bool silverShort,
        IReadOnlyDictionary<Resource, long> stock,
        RecipeFacts facts)
    {
        pawns.TryGetValue(out var rows);
        var byId = new Dictionary<PawnId, CarePawn>();
        foreach (var row in rows ?? [])
        {
            byId[row.Id] = row;
        }
        var result = new List<OrganNeed>();
        foreach (var want in wants)
        {
            if (want.Reason != SurgeryWantReason.PartShort || !byId.TryGetValue(want.Pawn, out var pawn))
            {
                continue;
            }
            pawn.Operations.TryGetValue(out var ops);
            var best = new OrganNeed("", default, 0);
            foreach (var op in ops ?? [])
            {
                var pk = op.PartIndex.TryGetValue(out var part);
                op.PartDefName.TryGetValue(out var organ);
                organ ??= "";
                // The natural install of the organ is the recipe that consumes the organ itself.
                if (!pk || part != want.Part || !facts.HarvestOrgan(organ) || op.Item != new Resource(organ)
                    || stock.GetValueOrDefault(new Resource(organ)) > 0)
                {
                    continue;
                }
                var (weight, _) = ServedSurgery(pawn, op);
                if (weight * PolicyConstants.SilverPerCapacity > best.Gain)
                {
                    best = new OrganNeed(organ, pawn.Id, weight * PolicyConstants.SilverPerCapacity);
                }
            }
            if (best.Gain > 0)
            {
                result.Add(best);
            }
        }
        if (silverShort)
        {
            foreach (var organ in facts.HarvestOrgans)
            {
                if (stock.GetValueOrDefault(new Resource(organ)) == 0)
                {
                    result.Add(new OrganNeed(organ, default, 0));
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Picks at most one harvest: nothing while any prisoner has a queued bill or an open surgery action
    /// (inFlight), so one harvest lands before the next is weighed. Each eligible prisoner's harvest ops on a
    /// needed organ it has two of, not lethal, stocked, and within RestoreFailureCap for some eligible doctor,
    /// are priced; a colonist's need comes before a sale, then the best gain less cost, then prisoner id. A
    /// harvest whose cost reaches its gain, or whose precept refuses, is never chosen. Unknown facts refuse.
    /// </summary>
    public static OrganHarvest? SelectOrganHarvest(
        Fact<IReadOnlyList<PrisonerFacts>> prisoners,
        Fact<PrisonerColony> colony,
        IReadOnlyList<OrganNeed> needs,
        IReadOnlyDictionary<PawnId, bool>? inFlight)
    {
        if (!prisoners.TryGetValue(out var rows) || !colony.TryGetValue(out var c) || needs.Count == 0
            || SurgeryInFlight(rows, inFlight))
        {
            return null;
        }
        OrganHarvest? best = null;
        foreach (var row in rows)
        {
            if (!row.HarvestEligible(c).TryGetValue(out var eligible) || !eligible)
            {
                continue;
            }
            if (!row.HarvestGoodwill.TryGetValue(out var goodwill) || !row.Operations.TryGetValue(out var ops))
            {
                continue;
            }
            var pairs = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var op in ops)
            {
                op.PartDefName.TryGetValue(out var organ);
                if (op.Kind == SurgeryKind.Harvest)
                {
                    pairs[organ ?? ""] = pairs.GetValueOrDefault(organ ?? "") + 1;
                }
            }
            foreach (var op in ops)
            {
                op.PartDefName.TryGetValue(out var organ);
                organ ??= "";
                if (op.Kind != SurgeryKind.Harvest || pairs.GetValueOrDefault(organ) < 2 || !HarvestAcceptable(op))
                {
                    continue;
                }
                op.Recipe.TryGetValue(out var recipe);
                op.PartIndex.TryGetValue(out var part);
                foreach (var need in needs)
                {
                    if (need.Organ != organ)
                    {
                        continue;
                    }
                    var gain = need.Gain;
                    if (need.For.Value.Length == 0)
                    {
                        if (!op.YieldValue.TryGetValue(out var value) || !Finite(value))
                        {
                            continue;
                        }
                        gain = value;
                    }
                    var (cost, ok) = HarvestCost(c, goodwill, need.For.Value.Length == 0);
                    var h = new OrganHarvest(row.Pawn, recipe ?? "", part, organ, need.For, gain, cost, true, default, default);
                    if (ok && gain > cost && (best is null || BetterHarvest(h, best)))
                    {
                        best = h;
                    }
                }
            }
        }
        return best;
    }

    /// <summary>
    /// Some living prisoner has a queued bill (or an unknown bill stack) or an open surgery action, so no new
    /// cut is weighed.
    /// </summary>
    internal static bool SurgeryInFlight(IReadOnlyList<PrisonerFacts> rows, IReadOnlyDictionary<PawnId, bool>? inFlight)
    {
        foreach (var row in rows)
        {
            if (!row.QueuedSurgeries.TryGetValue(out var queued) || queued > 0
                || (inFlight?.GetValueOrDefault(new PawnId(row.Pawn.Value)) ?? false))
            {
                row.Dead.TryGetValue(out var dead);
                if (!dead)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>A colonist's need before a sale, then the earlier peg step, then gain less cost, then prisoner id.</summary>
    internal static bool BetterHarvest(OrganHarvest h, OrganHarvest best)
    {
        if ((h.For.Value.Length != 0) != (best.For.Value.Length != 0))
        {
            return h.For.Value.Length != 0;
        }
        if (h.Step != best.Step)
        {
            return h.Step < best.Step;
        }
        if (h.Gain - h.Cost != best.Gain - best.Cost)
        {
            return h.Gain - h.Cost > best.Gain - best.Cost;
        }
        return h.Prisoner < best.Prisoner;
    }

    /// <summary>
    /// A part harvest native reports non-lethal, with its ingredients on the map and some eligible doctor
    /// within RestoreFailureCap. The violation is acknowledged, not refused.
    /// </summary>
    internal static bool HarvestAcceptable(SurgeryOperation op)
    {
        var dk = op.EligibleDoctors.TryGetValue(out var doctors);
        var ck = op.SuccessChance.TryGetValue(out var chance);
        var lk = op.Lethal.TryGetValue(out var lethal);
        var sk = op.IngredientsOnMap.TryGetValue(out var stocked);
        var pk = op.PartIndex.TryGetValue(out _);
        var rk = op.Recipe.TryGetValue(out _);
        return dk && ck && lk && sk && pk && rk && doctors > 0 && stocked && !lethal && 1 - chance <= RestoreFailureCap + 1e-9;
    }

    /// <summary>
    /// Reports whether a sale harvest would be queued now: the silver runway is short and some eligible
    /// prisoner's organ clears its cost. DetectRounds holds MaintainSurgery open on it.
    /// </summary>
    public static bool SaleHarvestWanted(RoundsFacts f, Fact<bool> silverShort)
    {
        if (!Positive(silverShort))
        {
            return false;
        }
        var stock = new Dictionary<Resource, long>();
        if (f.Resources.TryGetValue(out var rows))
        {
            foreach (var row in rows)
            {
                stock[row.Resource] = stock.GetValueOrDefault(row.Resource) + row.Count;
            }
        }
        return SelectOrganHarvest(f.Prisoners, f.PrisonerColony, OrganNeeds(Unknown<IReadOnlyList<CarePawn>>(), [], true, stock, f.Recipes), null) is not null;
    }

    /// <summary>
    /// Adds each harvested organ in stock to the trade need's surplus while the silver runway is short: the
    /// sale path's organ sells through SelectTrade like any surplus, keeping none.
    /// </summary>
    public static Fact<TradeNeed> OrganSaleSurplus(
        ItemFacts items,
        Fact<TradeNeed> need,
        Fact<IReadOnlyList<Amount>> resources,
        Fact<long> colonists,
        RecipeFacts facts)
    {
        if (!need.TryGetValue(out var n) || !resources.TryGetValue(out var rows)
            || !Positive(SilverShort(items, need, SilverStock(items, resources), colonists)))
        {
            return need;
        }
        var stock = new Dictionary<Resource, long>();
        foreach (var row in rows)
        {
            stock[row.Resource] = stock.GetValueOrDefault(row.Resource) + row.Count;
        }
        var surplus = n.Surplus.ToList();
        var retained = n.Retained is null ? new Dictionary<Resource, long>() : new Dictionary<Resource, long>(n.Retained);
        foreach (var organ in facts.HarvestOrgans.OrderBy(o => o, StringComparer.Ordinal))
        {
            var count = stock.GetValueOrDefault(new Resource(organ));
            if (count > 0)
            {
                surplus.Add(new Amount(new Resource(organ), count));
                retained[new Resource(organ)] = 0;
            }
        }
        return Known(n with { Surplus = surplus, Retained = retained });
    }

    // Artificial part recovery: MaintainSurgery removes an added part (bionic, prosthetic, archotech) from a
    // HarvestEligible prisoner so the colony can install or sell it. The gain is a colonist's capacity the
    // part restores (a surgery_part_short want with an install recipe for it, servedSurgery's weight x
    // SilverPerCapacity) or else the part's market value in stock, where the part demand and the trade
    // surplus use it. The prisoner's own market value is not a cost: vanilla's price already counts the part,
    // and nothing sells prisoners. The cost is the medicine used, plus HarvestCost's mood and goodwill only
    // when native flags the removal a violation (then acknowledged). Shares the harvest's one-surgery-at-a-
    // time rule; SelectOrganHarvest's harvest goes first.

    /// <summary>Lists the colonists' part-short wants an added part could serve: one need per install option, Organ the part item.</summary>
    public static IReadOnlyList<OrganNeed> PartRecoveryNeeds(Fact<IReadOnlyList<CarePawn>> pawns, IReadOnlyList<SurgeryWant> wants, RecipeFacts facts)
    {
        pawns.TryGetValue(out var rows);
        var byId = new Dictionary<PawnId, CarePawn>();
        foreach (var row in rows ?? [])
        {
            byId[row.Id] = row;
        }
        var result = new List<OrganNeed>();
        foreach (var want in wants)
        {
            if (want.Reason != SurgeryWantReason.PartShort || !byId.TryGetValue(want.Pawn, out var pawn))
            {
                continue;
            }
            pawn.Operations.TryGetValue(out var ops);
            foreach (var op in ops ?? [])
            {
                var pk = op.PartIndex.TryGetValue(out var part);
                var item = op.Item;
                if (!pk || part != want.Part || item.Value.Length == 0 || !ValidResource(item) || facts.HarvestOrgan(item.Value))
                {
                    continue;
                }
                var (weight, _) = ServedSurgery(pawn, op);
                if (weight > 0)
                {
                    result.Add(new OrganNeed(item.Value, pawn.Id, weight * PolicyConstants.SilverPerCapacity));
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Picks at most one added part removal under the harvest's in-flight rule: a colonist's need first, then
    /// the best gain less cost, then prisoner id. A removal is skipped when its part is in unrecoveredParts,
    /// sits on a keptBodyParts part, is a leg on a prisoner already missing one, or fails HarvestAcceptable
    /// (lethal included).
    /// </summary>
    public static OrganHarvest? SelectPartRecovery(
        Fact<IReadOnlyList<PrisonerFacts>> prisoners,
        Fact<PrisonerColony> colony,
        IReadOnlyList<OrganNeed> needs,
        IReadOnlyDictionary<PawnId, bool>? inFlight,
        RecipeFacts facts)
    {
        if (!prisoners.TryGetValue(out var rows) || !colony.TryGetValue(out var c) || SurgeryInFlight(rows, inFlight))
        {
            return null;
        }
        OrganHarvest? best = null;
        foreach (var row in rows)
        {
            if (!row.HarvestEligible(c).TryGetValue(out var eligible) || !eligible)
            {
                continue;
            }
            if (!row.Operations.TryGetValue(out var ops) || !row.MissingParts.TryGetValue(out var missing))
            {
                continue;
            }
            var legless = false;
            foreach (var m in missing)
            {
                m.PartDefName.TryGetValue(out var name);
                if (name == "Leg")
                {
                    legless = true;
                }
            }
            foreach (var op in ops)
            {
                var hk = op.AddedPart.TryGetValue(out var hediff);
                var ik = op.YieldThing.TryGetValue(out var item);
                var vk = op.YieldValue.TryGetValue(out var value);
                op.PartDefName.TryGetValue(out var body);
                body ??= "";
                if (op.Kind != SurgeryKind.Amputate || !hk || !ik || !vk || !Finite(value) || PolicyConstants.UnrecoveredParts.Contains(hediff!)
                    || facts.KeptPart(body) || (body == "Leg" && legless) || !HarvestAcceptable(op))
                {
                    continue;
                }
                var (cost, ok) = PartRecoveryCost(op, row, c);
                if (!ok)
                {
                    continue;
                }
                op.Recipe.TryGetValue(out var recipe);
                op.PartIndex.TryGetValue(out var part);
                op.Violation.TryGetValue(out var violation);
                var h = new OrganHarvest(row.Pawn, recipe ?? "", part, item!, default, value, cost, violation, default, default);
                foreach (var need in needs)
                {
                    if (need.Organ == item && need.For.Value.Length != 0 && (h.For.Value.Length == 0 || need.Gain > h.Gain))
                    {
                        h = h with { For = need.For, Gain = need.Gain };
                    }
                }
                if (h.Gain > h.Cost && (best is null || BetterHarvest(h, best)))
                {
                    best = h;
                }
            }
        }
        return best;
    }

    /// <summary>
    /// The medicine's value plus, when native flags the removal a violation, HarvestCost's mood and goodwill.
    /// Ok is false when a needed fact is unknown or the precept refuses.
    /// </summary>
    internal static (double Cost, bool Ok) PartRecoveryCost(SurgeryOperation op, PrisonerFacts row, PrisonerColony c)
    {
        double cost = 0;
        if (op.MedicineValue.TryGetValue(out var medicine))
        {
            if (!Finite(medicine))
            {
                return (0, false);
            }
            cost = medicine;
        }
        if (!op.Violation.TryGetValue(out var violation))
        {
            return (0, false);
        }
        if (violation)
        {
            var gk = row.HarvestGoodwill.TryGetValue(out var goodwill);
            var (extra, ok) = HarvestCost(c, goodwill, false);
            if (!gk || !ok)
            {
                return (0, false);
            }
            cost += extra;
        }
        return (cost, true);
    }

    /// <summary>Reports whether a stock recovery would be queued now; DetectRounds holds MaintainSurgery open on it.</summary>
    public static bool PartRecoveryWanted(RoundsFacts f) =>
        SelectPartRecovery(f.Prisoners, f.PrisonerColony, [], null, f.Recipes) is not null;

    /// <summary>
    /// Keeps one stocked unit per open colonist restore or install: each want, each choice this review would
    /// queue and each bill already queued holds back its best recipe item still in the surplus, so a
    /// harvested organ or recovered part is not sold before its install is queued.
    /// </summary>
    public static Fact<TradeNeed> ReserveSurgeryStock(Fact<TradeNeed> need, Fact<IReadOnlyList<CarePawn>> pawns)
    {
        if (!need.TryGetValue(out var n) || n.Surplus.Count == 0)
        {
            return need;
        }
        var s = SelectSurgery(pawns, null, new SurgeryContext());
        var options = new List<IReadOnlyList<Resource>>(s.Wants.Count + s.Queue.Count);
        foreach (var want in s.Wants)
        {
            options.Add(want.Items);
        }
        foreach (var choice in s.Queue)
        {
            options.Add([choice.Item]);
        }
        if (pawns.TryGetValue(out var rows))
        {
            foreach (var pawn in rows)
            {
                pawn.Dead.TryGetValue(out var dead);
                if (dead)
                {
                    continue;
                }
                foreach (var item in pawn.QueuedItems)
                {
                    options.Add([item]); // queued bill
                }
            }
        }
        var surplus = n.Surplus.ToList();
        var retained = n.Retained is null ? new Dictionary<Resource, long>() : new Dictionary<Resource, long>(n.Retained);
        var reserved = false;
        foreach (var items in options)
        {
            foreach (var item in items)
            {
                if (!ValidResource(item))
                {
                    continue;
                }
                var taken = false;
                for (var i = 0; i < surplus.Count; i++)
                {
                    if (surplus[i].Resource == item && surplus[i].Count > 0)
                    {
                        surplus[i] = surplus[i] with { Count = surplus[i].Count - 1 };
                        retained[item] = retained.GetValueOrDefault(item) + 1;
                        reserved = true;
                        taken = true;
                        break;
                    }
                }
                if (taken)
                {
                    break;
                }
            }
        }
        if (!reserved)
        {
            return need;
        }
        return Known(n with { Surplus = surplus.Where(row => row.Count > 0).ToList(), Retained = retained });
    }
}
