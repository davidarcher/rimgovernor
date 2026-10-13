using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using RimGovernor.Contracts.Wire.Common;
using RimGovernor.Contracts.Wire.Mirror;
using RimGovernor.Contracts.Wire.Observations;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Observation;

/// <summary>
/// Go's <c>Combat</c> (bridge frames.go): the combat state of the newest frame, the emergency census with its combat
/// detail, the lines of fire, the standing rooms, doors and mortars, the frame's temperatures, and the load's
/// catalog with the mortar shells and the weapons' things read off it. The frame decoder (<c>DecodeCombat</c>) and
/// the stream read (<c>ReadCombat</c>) are wire plumbing and not ported.
/// </summary>
public sealed record Combat
{
    public WorldProgressionRead? World { get; init; }

    public ObservationContext? Context { get; init; }

    public IReadOnlyList<CombatPawn> Pawns { get; init; } = [];

    public IReadOnlyList<CombatEventRow> Events { get; init; } = [];

    public EmergencyObservation Emergency { get; init; } = new(null, new EmergencyFacts());

    /// <summary>The frame's pawn table cut to the emergency census: colonists, hostiles and predators with their combat detail.</summary>
    public Pawns Detail { get; init; }

    public IReadOnlyList<Bridge.Contracts.LineOfFire> Lines { get; init; } = [];

    /// <summary>The line-of-fire budget ran out, so a pair absent from <see cref="Lines"/> is unknown, not blocked (the shooter then walks in).</summary>
    public bool LinesSkipped { get; init; }

    /// <summary>The map's standing rectangular rooms.</summary>
    public IReadOnlyList<P.CombatRoom> Rooms { get; init; } = [];

    public Fact<EquatableArray<P.RoomDoor>> DoorStates { get; init; }

    /// <summary>The damaged player doors.</summary>
    public IReadOnlyList<CombatDoorRow> Doors { get; init; } = [];

    /// <summary>The unroofed player mortars.</summary>
    public IReadOnlyList<P.CombatMortar> Mortars { get; init; } = [];

    /// <summary>The colony facts outdoor temperature.</summary>
    public Fact<double> OutdoorTemperatureC { get; init; }

    /// <summary>The hottest live hive's temperature.</summary>
    public Fact<double> HiveTemperatureC { get; init; }

    public BundleSnapshot? Frame { get; init; }

    /// <summary>The load's definition catalog: the mech kinds, the weapons' def rows and the race rows resolve against it.</summary>
    public DefinitionCatalog? Catalog { get; init; }

    /// <summary>The load's mortar shells by kind, read off <see cref="Catalog"/>.</summary>
    public P.MortarShells Shells { get; init; } = new();

    /// <summary>The frame's things table cut to the census pawns' primary weapons: a gear reference carries no def name, so the weapons resolve through it.</summary>
    public Things Things { get; init; }
}
