#nullable enable
using System.Collections.Generic;

namespace RimGovernor.Fixtures.Contracts;

// The replies of the Food, Farm and Husbandry cases' bespoke fixture ops (#3405): each prepares physical facts the stage
// primitives cannot express, or records what the native game did. A record carries only what a case reads.

/// <summary>
/// <c>test/husbandry_setup</c>: a single-handler enclosure with a near-term pregnant mother, a father, a cow, a dog short
/// of Obedience, an obedient unmastered guard, an allowed area and a factionless tameable muffalo outside.
/// </summary>
public sealed record HusbandrySetup
{
    public bool Success { get; init; }

    public string Mother { get; init; } = "";

    public string Father { get; init; } = "";

    public string Cow { get; init; } = "";

    public string Dog { get; init; } = "";

    public string Wild { get; init; } = "";

    public string Handler { get; init; } = "";

    public string Guard { get; init; } = "";

    public string Area { get; init; } = "";
}

/// <summary><c>test/ledger_observe</c>: the fixture's own tallies of Notify_Fished catches, rice yields and milk gathers.</summary>
public sealed record LedgerTally
{
    public double Catches { get; init; }

    public double RiceYield { get; init; }

    public int Gathers { get; init; }
}

/// <summary><c>test/ledger_hunt_observe</c>: the fixture's own tallies of deer corpses and the products butchered from them.</summary>
public sealed record HuntTally
{
    public int Tick { get; init; }

    public int Kills { get; init; }

    public int ButcherMeat { get; init; }

    public int ButcherLeather { get; init; }
}

/// <summary><c>test/ledger_hunt_butcher</c>: the hunted corpse, hauled out and back twice before its butcher bill.</summary>
public sealed record HuntButchered
{
    public bool Success { get; init; }

    public string Corpse { get; init; } = "";
}

/// <summary><c>test/food_reserve_prepare</c>: the pemmican units seeded short of the reserve target, and the colonists.</summary>
public sealed record ReservePrepared
{
    public bool Success { get; init; }

    public int Pemmican { get; init; }

    public int Colonists { get; init; }
}

/// <summary>
/// <c>test/food_reserve_probe</c>: the pemmican stacks, the other food, the food bills and the pemmican colonists ate since
/// the drain.
/// </summary>
public sealed record ReserveProbe
{
    public double ReserveEaten { get; init; }

    public IReadOnlyList<PemmicanPile> Pemmican { get; init; } = [];

    public int OtherFood { get; init; }

    public IReadOnlyList<FoodBill> Bills { get; init; } = [];
}

public sealed record PemmicanPile
{
    public int Units { get; init; }

    /// <summary>Held for the reserve.</summary>
    public bool Forbidden { get; init; }
}

public sealed record FoodBill
{
    public string Recipe { get; init; } = "";

    public int Target { get; init; }

    /// <summary>The BillRepeatModeDef name.</summary>
    public string Repeat { get; init; } = "";

    public bool Paused { get; init; }
}

/// <summary>
/// <c>test/food_starving_observe</c>: what the starving tribal colony did since <c>test/food_starving_prepare</c>: the
/// colonists' weapons, meat eaten, wild kills and each killer's job two ticks after its kill.
/// </summary>
public sealed record StarvingObserved
{
    public IReadOnlyList<StarvingColonist> Colonists { get; init; } = [];

    public double MeatEaten { get; init; }

    public int WildKills { get; init; }

    public IReadOnlyList<KillJob> KillJobs { get; init; } = [];
}

public sealed record StarvingColonist
{
    /// <summary>Wields a ranged weapon.</summary>
    public bool Ranged { get; init; }

    /// <summary>The JobDef the colonist is doing; empty when idle.</summary>
    public string Job { get; init; } = "";
}

public sealed record KillJob
{
    public string JobDef { get; init; } = "";

    public bool TargetLive { get; init; }

    public bool TargetDesignated { get; init; }
}

/// <summary><c>test/blight_prepare</c>: a 4x4 sown rice zone near the colonists with the first few plants blighted.</summary>
public sealed record BlightPrepared
{
    public bool Success { get; init; }

    public IReadOnlyList<BlightPreparedPlant> Plants { get; init; } = [];

    public IReadOnlyList<StageCell> ZoneCells { get; init; } = [];
}

/// <summary>A plant the fixture blighted.</summary>
public sealed record BlightPreparedPlant
{
    public string Id { get; init; } = "";
}

/// <summary><c>test/refrigeration_prepare</c>: an enclosed roofed room of warm meat with power and Cooler research; its interior, wall ring and meat stacks.</summary>
public sealed record RefrigerationPrepared
{
    public bool Success { get; init; }

    public RoomInterior Interior { get; init; } = new();

    public IReadOnlyList<StageCell> Walls { get; init; } = [];

    public IReadOnlyList<string> Meat { get; init; } = [];
}

/// <summary>The inclusive interior rectangle of a fixture room.</summary>
public sealed record RoomInterior
{
    public int MinX { get; init; }

    public int MinZ { get; init; }

    public int MaxX { get; init; }

    public int MaxZ { get; init; }
}

/// <summary><c>test/refrigeration_rot</c>: one thing's ambient temperature, or that it is gone.</summary>
public sealed record MeatProbe
{
    public bool Destroyed { get; init; }

    public double? TemperatureC { get; init; }
}
