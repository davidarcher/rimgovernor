#nullable enable
using System.Collections.Generic;
using System.Text.Json;

namespace RimGovernor.Fixtures.Contracts;

// The combat lab's fixture replies as the sweep reads them (#3410): test/lab_start's wipe and test/lab_stage's read,
// reseed and shots actions (scripts/fixtures/DebugStartFixture.cs). A record carries only what the sweep reads; the
// bundle keeps the raw read and shot replies as the native wrote them. The properties are settable, not init: the
// source generator assigns every init property, a field the reply leaves out as null, and an empty ledger or pod-less
// read leaves fields out.

/// <summary><c>test/lab_start</c> with a colonist count: the lab wiped to that crew around its centre.</summary>
public sealed record CombatLabWipe
{
    public bool Success { get; set; }

    public StageCell Center { get; set; }
}

/// <summary><c>test/lab_stage</c> action read or tick: the pawns on the map and the damage ledger since the staging.</summary>
public sealed record LabFightRead
{
    public bool Success { get; set; }

    public int Tick { get; set; }

    public IReadOnlyList<LabFightPawn> Pawns { get; set; } = [];

    /// <summary>Every hit since the staging, oldest first.</summary>
    public IReadOnlyList<LabDamageRow> Damage { get; set; } = [];

    /// <summary>The ledger rows dropped for size.</summary>
    public int DamageDropped { get; set; }

    /// <summary>The staged pawns' sides by load id: the sweep stamps it on the first read of a bundle.</summary>
    public IReadOnlyDictionary<string, string>? Staged { get; set; }

    /// <summary>The staged colonists' roles by load id (gunner, melee or noncombatant): stamped with <see cref="Staged"/>.</summary>
    public IReadOnlyDictionary<string, string>? Roles { get; set; }

    /// <summary>The staged hostiles' summed combat power: stamped with <see cref="Staged"/>.</summary>
    public double? RaidPoints { get; set; }

    /// <summary>The staged colonists' and turrets' defense capacity in raid-point units: stamped with <see cref="Staged"/>.</summary>
    public double? DefenseCapacity { get; set; }
}

/// <summary>One pawn of a lab read.</summary>
public sealed record LabFightPawn
{
    public string Id { get; set; } = "";

    public bool Downed { get; set; }

    public bool Dead { get; set; }

    public bool Fleeing { get; set; }
}

/// <summary>One damage ledger row: Downed and Dead say the hit left the victim down or dead.</summary>
public sealed record LabDamageRow
{
    public int Tick { get; set; }

    public string Victim { get; set; } = "";

    public string VictimSide { get; set; } = "";

    public string Instigator { get; set; } = "";

    public string InstigatorSide { get; set; } = "";

    public double Dealt { get; set; }

    public bool Downed { get; set; }

    public bool Dead { get; set; }
}

/// <summary><c>test/lab_stage</c> action reseed: the seed the RNG took and its next draws, read without consuming them.</summary>
public sealed record LabReseedRead
{
    public bool Success { get; set; }

    public int Seed { get; set; }

    public IReadOnlyList<int> Draws { get; set; } = [];
}

/// <summary><c>test/lab_stage</c> action shots: the shot tap's rows, each kept as written, and the rows dropped for size.</summary>
public sealed record LabShotsRead
{
    public int ShotsDropped { get; set; }

    public IReadOnlyList<JsonElement> Shots { get; set; } = [];
}
