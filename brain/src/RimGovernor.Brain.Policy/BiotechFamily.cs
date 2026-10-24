using System.Globalization;
using System.Text;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the biotech family reads (#3261). Inactive without the Biotech expansion (and in the immediate pass, which
/// reads nothing): the family derives no Needs. Active carries the colony's Biotech section (its gene buildings
/// answer whether a gene bank is owed, its wastepacks and uncovered cells are the pollution census), whether a mech
/// is owed (MechGestationOwed over the gestation read) and whether one more charger is owed (MechChargerOwed), the
/// packs an earlier haul of the pollution Need could not store and the polluted cells the planning window shows.
/// </summary>
public abstract record BiotechFacts
{
    private protected BiotechFacts() { }

    /// <summary>The Biotech expansion is not active, or nothing was read.</summary>
    public sealed record Inactive : BiotechFacts;

    /// <summary>The Biotech expansion is active.</summary>
    public sealed record Active : BiotechFacts
    {
        public required RimGovernor.Contracts.Wire.Observations.BiotechColonyFacts Colony { get; init; }

        /// <summary>Whether the mechanitors are owed one more mech; false when the mechs Need is not composed (it is then never derived).</summary>
        public required bool MechsOwed { get; init; }

        /// <summary>Whether the colony owes one more charger; false when the mech-charger Need is not composed (it is then never derived).</summary>
        public required bool MechChargerOwed { get; init; }

        /// <summary>The wastepacks whose haul by an unretired pollution Method ended unsuccessful (no stockpile took them): not ordered again.</summary>
        public EquatableArray<string> PollutionRefused { get; init; } = [];

        /// <summary>The polluted cells of the planning window (PollutedWindowCells).</summary>
        public EquatableArray<Cell> PollutedCells { get; init; } = [];

        /// <summary>Whether more genepacks lie loose than the standing gene banks have room for.</summary>
        public bool GeneBankOwed => GeneBankConcernRules.GeneBankOwed(Colony);

        /// <summary>The pollution census of the section.</summary>
        public PollutionFacts Pollution => PollutionRules.PollutionOf(Colony);
    }
}

/// <summary>The biotech Needs a composition derives; each toggle is the old planner flag.</summary>
public sealed record BiotechNeeds
{
    public bool Mechs { get; init; }

    public bool MechCharger { get; init; }

    public bool GeneBank { get; init; }

    public bool Pollution { get; init; }

    public bool Any => Mechs || MechCharger || GeneBank || Pollution;
}

/// <summary>
/// The biotech family (#3261). It replaced the MaintainMechs, EnsureMechCharger, MaintainGeneBank and
/// ManagePollution Concerns, their inspections and planners:
/// <list type="bullet">
/// <item><c>biotech/mech-charger</c> (Development): the colony owes no further mech charger (MechChargerOwed). Its
/// work step builds one, <c>mech-charger-&lt;def&gt;-&lt;chargers standing&gt;</c>.</item>
/// <item><c>biotech/mechs</c> (Development): no mech is owed inside the mechanitors' bandwidth (MechGestationOwed).
/// It waits on <c>biotech/mech-charger</c> when that Need is composed: chargers come before more mechs. Its remedy
/// is the gestation bill the work ledger places (<c>MechBillDeclarer</c>), filed under the Need.</item>
/// <item><c>biotech/gene-bank</c> (Development): the standing gene banks hold every loose genepack (GeneBankOwed).
/// Its work step builds one, <c>gene-bank-&lt;def&gt;-&lt;banks standing&gt;</c>.</item>
/// <item><c>biotech/pollution</c> (Wellbeing): no wastepack lies exposed or forbidden and no polluted cell is outside
/// the pollution-clear area (PollutionDeficit). Its pure Method, <c>pollution-&lt;hash of the work&gt;</c>, allows the
/// forbidden packs (admitted against the review's pollution cohort), sets the clear area and hauls the exposed packs.</item>
/// </list>
/// </summary>
public sealed class BiotechFamily(BiotechNeeds needs) : IFamily<BiotechFacts>
{
    public const string FamilyId = "biotech";

    public static readonly NeedId Mechs = NeedId.Of(FamilyId, "mechs");

    public static readonly NeedId MechCharger = NeedId.Of(FamilyId, "mech-charger");

    public static readonly NeedId GeneBank = NeedId.Of(FamilyId, "gene-bank");

    public static readonly NeedId Pollution = NeedId.Of(FamilyId, "pollution");

    private readonly BiotechNeeds _needs = needs ?? throw new ArgumentNullException(nameof(needs));

    public string Id => FamilyId;

    public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Colony, FactFamily.Pawns, FactFamily.World, FactFamily.Rooms, FactFamily.Emergency];

    public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [];

    public IReadOnlyList<Need> Derive(BiotechFacts facts)
    {
        if (facts is not BiotechFacts.Active active)
        {
            return [];
        }
        var output = MechNeeds(active);
        if (_needs.GeneBank)
        {
            output.Add(Owed(GeneBank, NeedTier.Development, active.GeneBankOwed));
        }
        if (_needs.Pollution)
        {
            output.Add(BiotechFamilyRules.PollutionNeedOf(active));
        }
        return output;
    }

    /// <summary>The mech-charger and mechs Needs, the ones composed, in derivation order.</summary>
    private List<Need> MechNeeds(BiotechFacts.Active facts)
    {
        var output = new List<Need>(4);
        if (_needs.MechCharger)
        {
            output.Add(Owed(MechCharger, NeedTier.Development, facts.MechChargerOwed));
        }
        if (_needs.Mechs)
        {
            output.Add(Owed(Mechs, NeedTier.Development, facts.MechsOwed) with
            {
                Preconditions = _needs.MechCharger ? [MechCharger] : [],
            });
        }
        return output;
    }

    public IReadOnlyList<Need> DeriveSeriousHarm(BiotechFacts facts) => [];

    public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, BiotechFacts facts)
    {
        var output = new List<NeedMethod>();
        if (facts is not BiotechFacts.Active active)
        {
            return output;
        }
        foreach (var need in ready)
        {
            if (need.Id == Pollution && PollutionMethod(active.Pollution, active) is { } method)
            {
                output.Add(method);
            }
        }
        return output;
    }

    /// <summary>
    /// The pollution Need's one Method for this read: the allows, the clear-area edit and the hauls SelectPollutionWork
    /// orders, named by a hash of that work so the same work continues its open Method. Null when nothing is to be ordered.
    /// </summary>
    internal static NeedMethod? PollutionMethod(PollutionFacts pollution, BiotechFacts.Active facts)
    {
        var refused = facts.PollutionRefused.ToDictionary(id => id, _ => true, StringComparer.Ordinal);
        var work = PollutionRules.SelectPollutionWork(pollution, refused, [.. facts.PollutedCells]);
        if (work.Empty)
        {
            return null;
        }
        var text = new StringBuilder();
        foreach (var w in work.Allow)
        {
            text.Append(CultureInfo.InvariantCulture, $"allow/{w.Id}\n");
        }
        foreach (var w in work.Haul)
        {
            text.Append(CultureInfo.InvariantCulture, $"haul/{w.Id}\n");
        }
        foreach (var c in work.Area)
        {
            text.Append(CultureInfo.InvariantCulture, $"cell/{c.X}/{c.Z}\n");
        }
        var method = new MethodId("pollution-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))[..16]));
        var reason = string.Create(CultureInfo.InvariantCulture, $"pollution: {work.Allow.Count} wastepacks to allow, {work.Area.Count} cells to the clear area, {work.Haul.Count} wastepacks to haul");
        return new NeedMethod(Pollution, method, reason, id =>
        {
            var actions = new List<DomainAction>();
            ActionId Next() => new(string.Create(CultureInfo.InvariantCulture, $"{id.Value}-{actions.Count}"));
            foreach (var w in work.Allow)
            {
                actions.Add(DomainAction.NewSupplyAllowAction(Next(), new SupplyAllow(w.Id, w.Definition, w.Cell)));
            }
            if (work.Area.Count > 0)
            {
                actions.Add(DomainAction.NewAreaAction(Next(), Area.NewPollutionClearArea(AreaOperation.SetCells, work.Area)));
            }
            foreach (var w in work.Haul)
            {
                actions.Add(DomainAction.NewWastepackHaulAction(Next(), new WastepackHaul(w.Id, w.Definition, w.Cell)));
            }
            return new PlanSpec(id, new PlanRevision(1), actions);
        });
    }

    /// <summary>Whether the composition derives the mech-charger Need (the mechs Need's precondition).</summary>
    public bool ComposesMechCharger => _needs.MechCharger;

    /// <summary>Whether the mechs Need is ready over the gestation read: composed, unmet, and its charger precondition met.</summary>
    public bool MechsReady(BiotechFacts.Active facts) =>
        NeedsRules.NeedStandings(FamilyId, MechNeeds(facts)).Any(s => s.Need.Id == Mechs && s.Readiness == NeedReadiness.Ready);

    private static Need Owed(NeedId id, NeedTier tier, bool owed) => FoodRules.WholeNeed(id, tier, !owed);
}

public static class BiotechFamilyRules
{
    /// <summary>The pollution Need: unmet while PollutionDeficit finds an exposed or forbidden pack or an uncovered polluted cell.</summary>
    public static Need PollutionNeedOf(BiotechFacts.Active facts) =>
        FoodRules.WholeNeed(BiotechFamily.Pollution, NeedTier.Wellbeing, !PollutionRules.PollutionDeficit(facts.Pollution));
}
