namespace RimGovernor.Brain.Observation;

/// <summary>
/// The Odyssey colony section: active game conditions, hazardous terrain, lava emergences and underground
/// sites. An absent scalar is unknown, never zero; the whole section is unknown without Odyssey or when the
/// read failed.
/// </summary>
public sealed record OdysseyColony
{
    public IReadOnlyList<ActiveCondition> Conditions { get; init; } = [];

    public IReadOnlyList<HazardTerrain> HazardTerrain { get; init; } = [];

    public IReadOnlyList<LavaEmergence> LavaEmergences { get; init; } = [];

    public IReadOnlyList<UndergroundSite> Sites { get; init; } = [];
}

/// <summary>One GameCondition on the colony map. TicksLeft is unknown for a permanent condition.</summary>
public sealed record ActiveCondition
{
    public string ID { get; init; } = "";

    public string Definition { get; init; } = "";

    public string Class { get; init; } = "";

    public bool Permanent { get; init; }

    public Fact<int> TicksPassed { get; init; }

    public Fact<int> TicksLeft { get; init; }

    public Fact<string> CauserID { get; init; }
}

/// <summary>A terrain def whose own flags hurt or contaminate and the cells it covers.</summary>
public sealed record HazardTerrain
{
    public string Definition { get; init; } = "";

    public uint Cells { get; init; }

    public Fact<bool> Dangerous { get; init; }

    public Fact<int> BurnDamage { get; init; }

    public Fact<double> HeatPerTick { get; init; }

    public Fact<double> ToxicBuildupFactor { get; init; }
}

public sealed record LavaEmergence(string ID, Cell Position);

/// <summary>An ancient hatch's pocket map. Hackable positions are cells of the pocket map, not the colony map.</summary>
public sealed record UndergroundSite
{
    public string HatchID { get; init; } = "";

    public int PocketMapID { get; init; }

    public Fact<string> StockpileType { get; init; }

    public Fact<string> Layout { get; init; }

    public Fact<uint> ColonistsPresent { get; init; }

    public IReadOnlyList<UndergroundHackable> Hackables { get; init; } = [];
}

public sealed record UndergroundHackable
{
    public string ID { get; init; } = "";

    public string Definition { get; init; } = "";

    public Cell Position { get; init; }

    public Fact<double> ProgressPercent { get; init; }

    public Fact<double> Defence { get; init; }

    public Fact<bool> Hacked { get; init; }

    public Fact<bool> LockedOut { get; init; }

    public Fact<bool> Autohack { get; init; }
}

public static partial class ObservationPackage
{
    /// <summary>Projects a validated section; an absent or unavailable one is an unknown fact.</summary>
    internal static Fact<OdysseyColony> ColonyOdyssey(O.OdysseySection? section)
    {
        var f = section?.Outcome?.Observed;
        if (f is null)
        {
            return default;
        }
        var conditions = new List<ActiveCondition>();
        foreach (var x in f.Conditions)
        {
            conditions.Add(new ActiveCondition
            {
                ID = x.ConditionId ?? "",
                Definition = x.DefName ?? "",
                Class = x.ConditionClass ?? "",
                Permanent = x.Permanent.GetValueOrDefault(),
                TicksPassed = Optional(x.TicksPassed is not null, x.TicksPassed.GetValueOrDefault()),
                TicksLeft = Optional(x.TicksLeft is not null, x.TicksLeft.GetValueOrDefault()),
                CauserID = Optional(x.CauserId is not null, x.CauserId ?? ""),
            });
        }
        var hazards = new List<HazardTerrain>();
        foreach (var x in f.HazardTerrain)
        {
            hazards.Add(new HazardTerrain
            {
                Definition = x.DefName ?? "",
                Cells = x.Cells.GetValueOrDefault(),
                Dangerous = Optional(x.Dangerous is not null, x.Dangerous.GetValueOrDefault()),
                BurnDamage = Optional(x.BurnDamage is not null, x.BurnDamage.GetValueOrDefault()),
                HeatPerTick = Optional(x.HeatPerTick is not null, x.HeatPerTick.GetValueOrDefault()),
                ToxicBuildupFactor = Optional(x.ToxicBuildupFactor is not null, x.ToxicBuildupFactor.GetValueOrDefault()),
            });
        }
        var lava = new List<LavaEmergence>();
        foreach (var x in f.LavaEmergences)
        {
            lava.Add(new LavaEmergence(x.ThingId ?? "", CellOf(x.Position)));
        }
        var sites = new List<UndergroundSite>();
        foreach (var x in f.Sites)
        {
            var hackables = new List<UndergroundHackable>();
            foreach (var h in x.Hackables)
            {
                hackables.Add(new UndergroundHackable
                {
                    ID = h.ThingId ?? "",
                    Definition = h.DefName ?? "",
                    Position = CellOf(h.Position),
                    ProgressPercent = Optional(h.ProgressPercent is not null, h.ProgressPercent.GetValueOrDefault()),
                    Defence = Optional(h.Defence is not null, h.Defence.GetValueOrDefault()),
                    Hacked = Optional(h.Hacked is not null, h.Hacked.GetValueOrDefault()),
                    LockedOut = Optional(h.LockedOut is not null, h.LockedOut.GetValueOrDefault()),
                    Autohack = Optional(h.Autohack is not null, h.Autohack.GetValueOrDefault()),
                });
            }
            sites.Add(new UndergroundSite
            {
                HatchID = x.HatchId ?? "",
                PocketMapID = x.PocketMapId.GetValueOrDefault(),
                StockpileType = Optional(x.StockpileType is not null, x.StockpileType ?? ""),
                Layout = Optional(x.Layout is not null, x.Layout ?? ""),
                ColonistsPresent = Optional(x.ColonistsPresent is not null, x.ColonistsPresent.GetValueOrDefault()),
                Hackables = hackables,
            });
        }
        return Known(new OdysseyColony { Conditions = conditions, HazardTerrain = hazards, LavaEmergences = lava, Sites = sites });
    }
}
