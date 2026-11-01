namespace RimGovernor.Brain.Policy;

// The bridging provision a transition removal waits on (task 8.3 of redefine-habitat-requirements, RG-HABITAT-011): a building a
// chosen Store, Remove or Relocate takes off its cell keeps its place while what it provides is not observed served by a
// replacement on fresh facts. Four provisions are read: an occupied bed's sleepers, a live power net's consumers, a usable pen's
// herd, and the defense's standing tiers. Nothing is remembered: each review reads the dependency again, so a replacement lost
// before the old service is removed reopens it (ServicingChoiceRules.Choose gates the removal and binds nothing), and one lost
// after is a plain deficit for its owning family.

public static class TransitionBridgingRules
{
    /// <summary>The unserved dependants the reads name, before the dependant's id.</summary>
    public const string Rest = "rest:", Power = "power:", Pen = "pen:", Defense = "defense:";

    /// <summary>The construction census, unread: a pen or defense dependency cannot be told.</summary>
    public const string ConstructionCensus = "construction census";

    /// <summary>
    /// What a removal of the standing building (its id and definition) would leave without an observed serving replacement, from
    /// the review's facts (TransitionFacts.Unserved): the first of <see cref="RestUnserved"/>, <see cref="PowerUnserved"/>,
    /// <see cref="PenUnserved"/> and <see cref="DefenseUnserved"/>; null when nothing it provides is left unserved.
    /// </summary>
    public static Func<string, string, string?> UnservedOf(ReviewFacts f)
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
        return (target, definition) =>
            RestUnserved(f.Sleeping, target)
            ?? PowerUnserved(f.EssentialInputs.Power, target)
            ?? (pens.Contains(definition) ? herds.Value.Unread ?? PenUnserved(herds.Value.Herds, target) : null)
            ?? DefenseUnserved(f.LayoutPlan, f.EssentialInputs.Power, standing.Value, target, definition);
    }

    /// <summary>
    /// An occupied bed's sleeper left without rest (<c>rest:&lt;pawn&gt;</c>): each owner of <paramref name="target"/> needs another
    /// bed of the same use (crib or not, never a medical bed) that LegalBed gives the occupant class the old bed holds, that the
    /// owner can reach, and that the owner is assigned to or has a free slot in. Null for a bed no one owns, or no bed at all.
    /// </summary>
    public static string? RestUnserved(SleepingObservation sleeping, string target)
    {
        if (sleeping.Beds.FirstOrDefault(b => b.Id == target) is not { Owners.Count: > 0 } old || old.Medical)
        {
            return null;
        }
        var who = !old.Humanlike ? RimGovernor.Brain.GameFacts.Observations.BedOccupant.Animal
            : old.Prisoners ? RimGovernor.Brain.GameFacts.Observations.BedOccupant.Prisoner
            : old.Slaves ? RimGovernor.Brain.GameFacts.Observations.BedOccupant.Slave
            : RimGovernor.Brain.GameFacts.Observations.BedOccupant.Colonist;
        foreach (var owner in old.Owners.OrderBy(o => o.Value, StringComparer.Ordinal))
        {
            var served = sleeping.Beds.Any(b => b.Id != old.Id && !b.Medical && b.Crib == old.Crib
                && SleepingRules.LegalBed(b.Humanlike, b.Prisoners, b.Slaves, who) && b.AccessibleTo.Contains(owner)
                && (b.Owners.Contains(owner) || b.Owners.Count < b.Slots));
            if (!served)
            {
                return Rest + owner.Value;
            }
        }
        return null;
    }

    /// <summary>
    /// A live net's consumer left unpowered (<c>power:&lt;consumer&gt;</c>): a producer whose net's other generation no longer meets
    /// the net's draw without its output, or a battery on a net whose generation does not meet its draw, while the net powers a
    /// consumer. A net the per-net summary does not list names the net (<c>power:network:&lt;id&gt;</c>). Null for anything else.
    /// </summary>
    public static string? PowerUnserved(PowerTopology power, string target)
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
        var left = producer ? net.GenerationW - site.OutputW : net.GenerationW;
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
