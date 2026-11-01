namespace RimGovernor.Brain.Policy;

// The bridging provision a transition removal waits on (task 8.3 of redefine-habitat-requirements, RG-HABITAT-011): a building a
// chosen Store, Remove or Relocate takes off its cell keeps its place while what it provides is not observed served by a
// replacement on fresh facts. Four provisions are read: an occupied bed's sleepers, a live power net's consumers, a usable pen's
// herd, and the defense's standing tiers. Nothing is remembered across reviews: each review reads the dependency again, so a
// replacement lost before the old service is removed reopens it (ServicingChoiceRules.Choose gates the removal and binds nothing),
// and one lost after is a plain deficit for its owning family. Within one review a replacement serves once: the bed slots and the
// net generation each admitted removal relies on are taken on the review's ledger (TransitionBridgingLedger), so two removals
// never pass against the same spare slot or the same remaining wattage.

/// <summary>
/// The replacements the removals admitted so far in one review rely on (ColonyReading.Bridging; one per review, shared by every
/// proposing path that reads the same colony): the buildings taken down, the free bed slots assigned to their stranded
/// sleepers, and the generation their nets lose. A removal is checked against what is left (<see cref="TransitionBridgingRules.UnservedOf"/>)
/// and, once admitted, takes its share (<see cref="TransitionBridgingRules.AdmitOf"/>); taking the same building twice takes nothing more.
/// </summary>
public sealed class TransitionBridgingLedger
{
    private readonly HashSet<string> removed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> slots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> generation = new(StringComparer.Ordinal);

    /// <summary>Whether an admitted removal takes <paramref name="target"/> down: it serves no one as a replacement.</summary>
    public bool Removed(string target) => removed.Contains(target);

    /// <summary>The free slots of <paramref name="bed"/> already assigned to sleepers of removed beds.</summary>
    public int SlotsTaken(string bed) => slots.GetValueOrDefault(bed);

    /// <summary>The generation <paramref name="network"/> loses with its admitted producer removals.</summary>
    public double GenerationTaken(string network) => generation.GetValueOrDefault(network);

    internal bool TakeRemoved(string target) => removed.Add(target);

    internal void TakeSlot(string bed) => slots[bed] = SlotsTaken(bed) + 1;

    internal void TakeGeneration(string network, double watts) => generation[network] = GenerationTaken(network) + watts;
}

public static class TransitionBridgingRules
{
    /// <summary>The unserved dependants the reads name, before the dependant's id.</summary>
    public const string Rest = "rest:", Power = "power:", Pen = "pen:", Defense = "defense:";

    /// <summary>The construction census, unread: a pen or defense dependency cannot be told.</summary>
    public const string ConstructionCensus = "construction census";

    /// <summary>
    /// What a removal of the standing building (its id and definition) would leave without an observed serving replacement, from
    /// the review's facts and what <paramref name="ledger"/> says earlier admitted removals already rely on (TransitionFacts.Unserved):
    /// the first of <see cref="RestUnserved"/>, <see cref="PowerUnserved"/>, <see cref="PenUnserved"/> and <see cref="DefenseUnserved"/>;
    /// null when nothing it provides is left unserved.
    /// </summary>
    public static Func<string, string, string?> UnservedOf(ReviewFacts f, TransitionBridgingLedger? ledger = null)
    {
        var herds = new Lazy<(IReadOnlyList<HerdArrangement> Herds, string? Unread)>(() =>
        {
            try
            {
                return (f.HerdArrangements(), null);
            }
            catch (RequiredFactException e)
            {
                return ([], Pen + e.Message);
            }
        });
        var standing = new Lazy<IReadOnlyDictionary<string, CurrentBuilding>?>(() =>
        {
            try
            {
                var byId = new Dictionary<string, CurrentBuilding>(StringComparer.Ordinal);
                foreach (var b in f.CurrentConstruction.Buildings)
                {
                    byId.TryAdd(b.Id, b);
                }
                return byId;
            }
            catch (RequiredFactException)
            {
                return null;
            }
        });
        var pens = new HashSet<string>([HerdPenRules.PenFenceDefinition, HerdPenRules.PenGateDefinition, HerdPenRules.PenMarkerDefinition, .. f.AnimalDoors], StringComparer.Ordinal);
        ledger ??= new();
        return (target, definition) =>
            RestUnserved(f.Sleeping, target, ledger)
            ?? PowerUnserved(f.EssentialInputs.Power, target, ledger)
            ?? (pens.Contains(definition) ? herds.Value.Unread ?? PenUnserved(herds.Value.Herds, target) : null)
            ?? DefenseUnserved(f.LayoutPlan, f.EssentialInputs.Power, standing.Value, target, definition);
    }

    /// <summary>
    /// What an admitted removal of the standing building takes on <paramref name="ledger"/> (TransitionFacts.Admit): the building
    /// itself, the free slot of the replacement bed each of its sleepers moves to (<see cref="RestUnserved"/>'s assignment), and the
    /// generation its net loses. The same building admitted again takes nothing more.
    /// </summary>
    public static Action<string, string> AdmitOf(ReviewFacts f, TransitionBridgingLedger ledger) => (target, _) =>
    {
        if (!ledger.TakeRemoved(target))
        {
            return;
        }
        RestOf(f.Sleeping, target, ledger, take: true);
        if (f.EssentialInputs.Power.Buildings.FirstOrDefault(b => b.Id == target) is { Network.Length: > 0 } site
            && site.Service.Power is RimGovernor.Brain.GameFacts.Observations.BuildingPower.Trader { OutputW: > 0 })
        {
            ledger.TakeGeneration(site.Network, site.OutputW);
        }
    };

    /// <summary>
    /// An occupied bed's sleeper left without rest (<c>rest:&lt;pawn&gt;</c>): each owner of <paramref name="target"/>, in id order,
    /// needs another bed of the same use (crib or not, never a medical bed) that LegalBed gives the occupant class the old bed
    /// holds, that the owner can reach, that no admitted removal takes down, and that the owner is assigned to or has a free slot
    /// in. A free slot serves one sleeper: the first owner it serves takes it, for the bed's other owners and, on
    /// <paramref name="ledger"/>, for every later removal. Null for a bed no one owns, or no bed at all.
    /// </summary>
    public static string? RestUnserved(SleepingObservation sleeping, string target, TransitionBridgingLedger? ledger = null) =>
        RestOf(sleeping, target, ledger ?? new(), take: false);

    private static string? RestOf(SleepingObservation sleeping, string target, TransitionBridgingLedger ledger, bool take)
    {
        if (sleeping.Beds.FirstOrDefault(b => b.Id == target) is not { Owners.Count: > 0 } old || old.Medical)
        {
            return null;
        }
        var who = !old.Humanlike ? RimGovernor.Brain.GameFacts.Observations.BedOccupant.Animal
            : old.Prisoners ? RimGovernor.Brain.GameFacts.Observations.BedOccupant.Prisoner
            : old.Slaves ? RimGovernor.Brain.GameFacts.Observations.BedOccupant.Slave
            : RimGovernor.Brain.GameFacts.Observations.BedOccupant.Colonist;
        var taken = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var owner in old.Owners.OrderBy(o => o.Value, StringComparer.Ordinal))
        {
            var candidates = sleeping.Beds.Where(b => b.Id != old.Id && !b.Medical && b.Crib == old.Crib && !ledger.Removed(b.Id)
                && SleepingRules.LegalBed(b.Humanlike, b.Prisoners, b.Slaves, who) && b.AccessibleTo.Contains(owner));
            if (candidates.Any(b => b.Owners.Contains(owner)))
            {
                continue;
            }
            var free = candidates.Where(b => b.Owners.Count + ledger.SlotsTaken(b.Id) + taken.GetValueOrDefault(b.Id) < b.Slots)
                .OrderBy(b => b.Id, StringComparer.Ordinal).FirstOrDefault();
            if (free is null)
            {
                return Rest + owner.Value;
            }
            taken[free.Id] = taken.GetValueOrDefault(free.Id) + 1;
        }
        if (take)
        {
            foreach (var (bed, count) in taken)
            {
                for (var i = 0; i < count; i++)
                {
                    ledger.TakeSlot(bed);
                }
            }
        }
        return null;
    }

    /// <summary>
    /// A live net's consumer left unpowered (<c>power:&lt;consumer&gt;</c>): a producer whose net's other generation, less what
    /// <paramref name="ledger"/>'s admitted producer removals already take, no longer meets the net's draw without its output, or a
    /// battery on a net whose remaining generation does not meet its draw, while the net powers a consumer. A net the per-net
    /// summary does not list names the net (<c>power:network:&lt;id&gt;</c>). Null for anything else.
    /// </summary>
    public static string? PowerUnserved(PowerTopology power, string target, TransitionBridgingLedger? ledger = null)
    {
        if (power.Buildings.FirstOrDefault(b => b.Id == target) is not { } site || site.Network.Length == 0)
        {
            return null;
        }
        var producer = site.Service.Power is RimGovernor.Brain.GameFacts.Observations.BuildingPower.Trader { OutputW: > 0 };
        var battery = site.Service.Power is RimGovernor.Brain.GameFacts.Observations.BuildingPower.Battery;
        if (!producer && !battery)
        {
            return null;
        }
        var consumer = power.Buildings.Where(b => b.Id != target && b.Network == site.Network
                && b.Service.Power is RimGovernor.Brain.GameFacts.Observations.BuildingPower.Trader { PowerOn: true, OutputW: < 0 })
            .Select(b => b.Id).Order(StringComparer.Ordinal).FirstOrDefault();
        if (consumer is null)
        {
            return null;
        }
        if (power.Networks.FirstOrDefault(n => n.Id == site.Network) is not { } net)
        {
            return Power + "network:" + site.Network;
        }
        var left = net.GenerationW - (ledger?.GenerationTaken(net.Id) ?? 0) - (producer ? site.OutputW : 0);
        return left + Tolerance < net.ConsumptionW ? Power + consumer : null;
    }

    /// <summary>
    /// The herd a pen piece pens that its pen does not serve (<c>pen:&lt;herd&gt;</c>), the HerdArrangementRules.Assess read of
    /// task 6.2: of the herds whose pieces (HerdArrangement.Pieces: the marker and observed boundary of a pen in the herd's
    /// planned pens or holding its animals) include <paramref name="target"/>, the first whose pen is unserved (no closed fence
    /// and door line around a valid marker), that has animals uncontained, or whose pen feed falls short. A piece that bounds no
    /// current herd's pen, or only served herds' pens, is not held on pens: an old pen stands until its own herd's new pen is
    /// closed and fed, never on another herd's deficit.
    /// </summary>
    public static string? PenUnserved(IReadOnlyList<HerdArrangement> herds, string target) =>
        herds.Where(h => h.Pieces.Contains(target) && (h.PenUnserved || h.Uncontained > 0 || h.FeedingDeficit > 0)).Select(h => h.Herd)
            .Order(StringComparer.Ordinal).FirstOrDefault() is { } herd ? Pen + herd : null;

    /// <summary>
    /// The defense a removal would take down before its replacement stands (<c>defense:&lt;tier&gt;</c>): a building that is
    /// one of a planned tier's pieces (its definition placed on one of its cells) is the chosen defense itself; a turret stays
    /// until every planned tier's pieces stand (<c>defense:&lt;def&gt;</c> while the plan places no tier at all). A retreat or
    /// temporary-cover winner's own positions are its tier's pieces, so standing cover outlives them. Unread construction
    /// census with a tier planned: <c>defense:construction census</c>.
    /// </summary>
    public static string? DefenseUnserved(LayoutPlan? plan, PowerTopology power, IReadOnlyDictionary<string, CurrentBuilding>? standing, string target, string definition)
    {
        var tiers = (plan?.Sites ?? []).Where(s => s.Tier is { Places.Count: > 0 }).Select(s => s.Tier!).ToList();
        var turret = power.Buildings.Any(b => b.Id == target && b.Turret is not null);
        if (tiers.Count == 0)
        {
            return turret ? Defense + definition : null;
        }
        if (standing is null)
        {
            return Defense + ConstructionCensus;
        }
        if (standing.GetValueOrDefault(target) is { } building)
        {
            var cells = building.Cells.ToHashSet();
            if (tiers.FirstOrDefault(t => t.Places.Any(p => p.Definition == definition && cells.Contains(p.Cell))) is { } own)
            {
                return Defense + own.Tier.Value;
            }
        }
        if (!turret)
        {
            return null;
        }
        var stood = new Dictionary<Cell, string>();
        foreach (var b in standing.Values)
        {
            foreach (var c in b.Cells)
            {
                stood.TryAdd(c, b.Observed.Building.DefName);
            }
        }
        return tiers.FirstOrDefault(t => t.Places.Any(p => stood.GetValueOrDefault(p.Cell) != p.Definition)) is { } unbuilt ? Defense + unbuilt.Tier.Value : null;
    }

    private const double Tolerance = 1e-9;
}
