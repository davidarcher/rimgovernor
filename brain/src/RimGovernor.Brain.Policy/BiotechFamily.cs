using System.Globalization;
using System.Text;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the biotech family reads (#3261): whether a mech is owed (MechGestationOwed over the gestation read),
/// whether one more charger or gene bank is owed, and the pollution census with the packs an earlier haul of the
/// pollution Need could not store and the polluted cells the planning window shows.
/// </summary>
public sealed record BiotechFacts
{
    public Fact<bool> MechsOwed { get; init; }

    public Fact<bool> MechChargerOwed { get; init; }

    public Fact<bool> GeneBankOwed { get; init; }

    public Fact<PollutionFacts> Pollution { get; init; }

    /// <summary>The wastepacks whose haul by an unretired pollution Method ended unsuccessful (no stockpile took them): not ordered again.</summary>
    public EquatableArray<string> PollutionRefused { get; init; }

    /// <summary>The polluted cells of the planning window (PollutedWindowCells).</summary>
    public EquatableArray<Cell> PollutedCells { get; init; }
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
/// <item><c>biotech/mech-charger</c> (Development): the colony owes no further mech charger (MechChargerNeed). Its
/// work step builds one, <c>mech-charger-&lt;def&gt;-&lt;chargers standing&gt;</c>.</item>
/// <item><c>biotech/mechs</c> (Development): no mech is owed inside the mechanitors' bandwidth (MechGestationOwed).
/// It waits on <c>biotech/mech-charger</c> when that Need is composed: chargers come before more mechs. Its remedy
/// is the gestation bill the work ledger places (<c>MechBillDeclarer</c>), filed under the Need.</item>
/// <item><c>biotech/gene-bank</c> (Development): the standing gene banks hold every loose genepack (GeneBankNeed).
/// Its work step builds one, <c>gene-bank-&lt;def&gt;-&lt;banks standing&gt;</c>.</item>
/// <item><c>biotech/pollution</c> (Wellbeing): no wastepack lies exposed or forbidden and no polluted cell is outside
/// the pollution-clear area (PollutionDeficit). Its pure Method, <c>pollution-&lt;hash of the work&gt;</c>, sets the
/// clear area and hauls the exposed packs. A forbidden pack's allow is not admitted for this Need, so the Need is
/// held as <see cref="UnsupportedWastepackAllow"/> while one is owed.</item>
/// </list>
/// </summary>
public sealed class BiotechFamily(BiotechNeeds needs) : IFamily<BiotechFacts>
{
    public const string FamilyId = "biotech";

    public static readonly NeedId Mechs = NeedId.Of(FamilyId, "mechs");

    public static readonly NeedId MechCharger = NeedId.Of(FamilyId, "mech-charger");

    public static readonly NeedId GeneBank = NeedId.Of(FamilyId, "gene-bank");

    public static readonly NeedId Pollution = NeedId.Of(FamilyId, "pollution");

    /// <summary>
    /// The hold of a pollution Need that owes a forbidden wastepack's allow. Supply allows are admitted only for the
    /// cohorts the review lists (food storage, event loot), so the old pollution planner's batches with one were always refused.
    /// </summary>
    public const string UnsupportedWastepackAllow = "unsupported_wastepack_allow";

    private readonly BiotechNeeds _needs = needs ?? throw new ArgumentNullException(nameof(needs));

    public string Id => FamilyId;

    public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Colony, FactFamily.Pawns, FactFamily.World, FactFamily.Rooms, FactFamily.Emergency];

    public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [];

    public IReadOnlyList<Need> Derive(BiotechFacts facts)
    {
        var output = new List<Need>(4);
        if (_needs.MechCharger)
        {
            output.Add(Owed(MechCharger, NeedTier.Development, "mech_charger_owed", facts.MechChargerOwed));
        }
        if (_needs.Mechs)
        {
            output.Add(Owed(Mechs, NeedTier.Development, "mech_owed", facts.MechsOwed) with
            {
                Preconditions = _needs.MechCharger ? [MechCharger] : [],
            });
        }
        if (_needs.GeneBank)
        {
            output.Add(Owed(GeneBank, NeedTier.Development, "gene_bank_owed", facts.GeneBankOwed));
        }
        if (_needs.Pollution)
        {
            output.Add(PolicyFunctions.PollutionNeedOf(facts));
        }
        return output;
    }

    public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, BiotechFacts facts)
    {
        var output = new List<NeedMethod>();
        foreach (var need in ready)
        {
            if (need.Id == Pollution && facts.Pollution.TryGetValue(out var pollution) && PollutionMethod(pollution, facts) is { } method)
            {
                output.Add(method);
            }
        }
        return output;
    }

    /// <summary>
    /// The pollution Need's one Method for this read: the clear-area edit and the hauls SelectPollutionWork orders,
    /// named by a hash of that work so the same work continues its open Method. Null when nothing is to be ordered;
    /// a hold while a forbidden pack is owed its allow.
    /// </summary>
    internal static NeedMethod? PollutionMethod(PollutionFacts pollution, BiotechFacts facts)
    {
        var refused = facts.PollutionRefused.ToDictionary(id => id, _ => true, StringComparer.Ordinal);
        var work = PollutionRules.SelectPollutionWork(pollution, refused, [.. facts.PollutedCells]);
        if (work.Allow.Count > 0)
        {
            return NeedMethod.Held(Pollution, UnsupportedWastepackAllow, "A forbidden wastepack's allow is not admitted for the pollution Need.");
        }
        if (work.Empty)
        {
            return null;
        }
        var text = new StringBuilder();
        foreach (var w in work.Haul)
        {
            text.Append(CultureInfo.InvariantCulture, $"haul/{w.Id}\n");
        }
        foreach (var c in work.Area)
        {
            text.Append(CultureInfo.InvariantCulture, $"cell/{c.X}/{c.Z}\n");
        }
        var method = new MethodId("pollution-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))[..16]));
        var reason = string.Create(CultureInfo.InvariantCulture, $"pollution: {work.Area.Count} cells to the clear area, {work.Haul.Count} wastepacks to haul");
        return new NeedMethod(Pollution, method, reason, id =>
        {
            var actions = new List<DomainAction>();
            ActionId Next() => new(string.Create(CultureInfo.InvariantCulture, $"{id.Value}-{actions.Count}"));
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

    /// <summary>Whether the mechs Need is ready this read: derived, unmet, and its charger precondition met.</summary>
    public bool MechsReady(BiotechFacts facts) =>
        NeedsRules.NeedStandings(FamilyId, Derive(facts)).Any(s => s.Need.Id == Mechs && s.Readiness == NeedReadiness.Ready);

    private static Need Owed(NeedId id, NeedTier tier, string fact, Fact<bool> owed) => new()
    {
        Id = id,
        Tier = tier,
        Satisfaction = !owed.TryGetValue(out var o) ? Satisfaction.Unknown : o ? Satisfaction.Unmet : Satisfaction.Met,
        Deficit = owed.TryGetValue(out var d) ? Known(d ? 1.0 : 0.0) : Unknown<double>(),
        Evidence = [new(fact, owed.TryGetValue(out var e) ? Known(e ? "yes" : "no") : Unknown<string>())],
    };
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// The pollution Need: unmet while PollutionDeficit finds an exposed or forbidden pack or an uncovered polluted
    /// cell, met once every verdict is known and none is, unknown otherwise (no Biotech read, or an unread verdict).
    /// </summary>
    public static Need PollutionNeedOf(BiotechFacts facts)
    {
        var deficit = PollutionRules.PollutionDeficit(facts.Pollution);
        var known = deficit.TryGetValue(out var owed);
        var uncovered = facts.Pollution.TryGetValue(out var f) && f.UncoveredCells.TryGetValue(out var cells) ? Known(cells.ToString(CultureInfo.InvariantCulture)) : Unknown<string>();
        return new Need
        {
            Id = BiotechFamily.Pollution,
            Tier = NeedTier.Wellbeing,
            Satisfaction = !known ? Satisfaction.Unknown : owed ? Satisfaction.Unmet : Satisfaction.Met,
            Deficit = known ? Known(owed ? 1.0 : 0.0) : Unknown<double>(),
            Evidence =
            [
                new("wastepacks", facts.Pollution.TryGetValue(out var p) ? Known(p.Wastepacks.Count.ToString(CultureInfo.InvariantCulture)) : Unknown<string>()),
                new("uncovered_cells", uncovered),
            ],
        };
    }
}
