#nullable enable
using System.Collections.Generic;

namespace RimGovernor.Fixtures.Contracts;

// The replies of the sparring cases' bespoke fixture ops (#3403): the ring the stage primitives cannot express (Melee
// levels with a Minor passion, training-only work, painstopper, a mid-bout event) and the per-tick recorders of the bout
// registry and of the watch jobs. A record carries only what a case reads or records as its evidence.

/// <summary><c>test/sparring_prepare</c>: the staged ring's eligible and shooting-chosen colonists (#2708).</summary>
public sealed record SparringPrepared
{
    public IReadOnlyList<string> Eligible { get; init; } = [];

    public IReadOnlyList<string> Shooter { get; init; } = [];
}

/// <summary>A bout as it first fought: per slot, the member, its team, its Melee level, its opponent and its marker.</summary>
public sealed record SparringFight
{
    public IReadOnlyList<string> Members { get; init; } = [];

    public IReadOnlyList<int> Teams { get; init; } = [];

    public IReadOnlyList<int> Melee { get; init; } = [];

    public IReadOnlyList<string> Opponents { get; init; } = [];

    public IReadOnlyList<string> Markers { get; init; } = [];
}

/// <summary>One bout the watcher saw: whether it has left the registry and how it first fought.</summary>
public sealed record SparringBout
{
    public int Id { get; init; }

    public bool Finished { get; init; }

    public SparringFight? Fighting { get; init; }
}

/// <summary>The raid scenario (#2711): the attacked fighter, and whether the raider is gone.</summary>
public sealed record SparringRaid
{
    public string Victim { get; init; } = "";

    public bool RaiderGone { get; init; }
}

/// <summary>What one tracked fighter's spar job left the tick it ended (#2709, #2710, #2731); Trail and GearDiff explain a failure.</summary>
public sealed record SparringPawn
{
    public string Id { get; init; } = "";

    public bool Ended { get; init; }

    public string Stop { get; init; } = "";

    public int Exchanges { get; init; }

    public bool Swapped { get; init; }

    public bool SwapSeen { get; init; }

    public bool SwapAtMarker { get; init; }

    public bool OriginalsHeld { get; init; }

    public bool Dead { get; init; }

    public bool Drafted { get; init; }

    public bool Restored { get; init; }

    public bool NoPractice { get; init; }

    public bool Originals { get; init; }

    public bool ThreatNull { get; init; }

    public bool Xp { get; init; }

    public bool TrainedWith { get; init; }

    public float JoyStart { get; init; }

    public float JoyEnd { get; init; }

    public float ToleranceStart { get; init; }

    public float ToleranceEnd { get; init; }

    public string Trail { get; init; } = "";

    public string GearDiff { get; init; } = "";
}

/// <summary><c>test/sparring_inspect</c>: the live bout registry and everything the per-tick watcher saw of the staged ring.</summary>
public sealed record SparringRead
{
    public int Active { get; init; }

    public int PeakActive { get; init; }

    public int DoubleBooked { get; init; }

    public IReadOnlyList<string> Seated { get; init; } = [];

    public string Drafted { get; init; } = "";

    public IReadOnlyList<SparringBout> Bouts { get; init; } = [];

    public string Injected { get; init; } = "";

    public string Hit { get; init; } = "";

    public int Abandoned { get; init; }

    public int AttackJobs { get; init; }

    public int Struck { get; init; }

    public SparringRaid? Raid { get; init; }

    public IReadOnlyList<SparringPawn> Pawns { get; init; } = [];
}

/// <summary><c>test/sparring_watch_prepare</c>: the staged spectators (#2732).</summary>
public sealed record SparringWatchPrepared
{
    public IReadOnlyList<string> Free { get; init; } = [];

    public string Drafted { get; init; } = "";

    public string Working { get; init; } = "";
}

/// <summary>One watch job the recorder saw; Ended is -1 while it runs.</summary>
public sealed record SparringWatchRow
{
    public string Id { get; init; } = "";

    public int Ended { get; init; }

    public float JoyStart { get; init; }

    public float JoyMax { get; init; }

    public bool CellOk { get; init; }

    public string CellNote { get; init; } = "";

    public float Dist { get; init; }

    public bool Fighter { get; init; }

    public int AtCellTicks { get; init; }

    public bool FightingAtEnd { get; init; }

    public bool Thought { get; init; }
}

/// <summary><c>test/sparring_watch_inspect</c>: everything the watch recorder saw of the staged spectators.</summary>
public sealed record SparringWatchRead
{
    public int Tick { get; init; }

    public IReadOnlyList<SparringWatchRow> Watchers { get; init; } = [];

    public int WatchJobs { get; init; }

    public int FighterWatchTicks { get; init; }
}
