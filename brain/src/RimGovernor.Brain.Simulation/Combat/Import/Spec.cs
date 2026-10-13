using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using PawnId = RimGovernor.Brain.Domain.PawnId;

namespace RimGovernor.Brain.Simulation.Combat.Import;

// Builds a Fight from a plain description of a fight (a Spec) and the native definition catalog. It derives what
// the simulator deliberately holds no literals for: per-pawn injury thresholds, the flattened armor list,
// effective weapon values, shooter stats, body size, move speed, and per-cell thing and terrain defs. Every
// derived value is traced to the catalog def (or game constant) it was read from in ImportResult.Trace. A value
// the catalog cannot give, or that the Spec does not state, fails the import with UnsupportedScenarioException;
// nothing is guessed. The derivations and the departures from the game they carry are approximations A24-A31 in
// approximations.md.
//
// The Spec is an in-memory description, not a persisted or wire format. The namespace sits beside the simulator
// so the pure core references no catalog code; fixtures are converted to a Spec on the native acceptance side.
//
// Stat reads go through IStatSource (Domain). The pawn stat contexts here are the importer's own hypotheticals
// (a healthy adult with stated skills and traits, see FightImporter.BaselineAdult), stated as values and never
// read from a game pawn, so the production source is the game-thread stat service (#3080) and tests record the
// values in FakeStatSource.

/// <summary>Describes one fight.</summary>
public sealed record Spec
{
    /// <summary>Labels the Metrics row.</summary>
    public string Name { get; init; } = "";

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>The TerrainDef name of every cell.</summary>
    public string Terrain { get; init; } = "";

    /// <summary>The WeatherDef name in force; its move speed and accuracy multipliers and range cap come from the def.</summary>
    public string Weather { get; init; } = "";

    /// <summary>The difficulty's friendlyFireChanceFactor, which no catalog def holds. Unknown fails the import.</summary>
    public Fact<double> FriendlyFireChanceFactor { get; init; }

    /// <summary>What the death-on-downed chance of a hostile depends on besides the catalog (A33). Unknown fails the import of a Spec with a hostile.</summary>
    public Fact<DeathOnDowned> DeathOnDowned { get; init; }

    /// <summary>
    /// The difficulty's allowInstantKillChance, the chance a player-faction pawn's would-be instant kill goes
    /// through. The per-part model kills a colonist at any hit, so only 1 imports (A39). Unknown fails the
    /// import of a Spec with a colonist.
    /// </summary>
    public Fact<double> AllowInstantKillChance { get; init; }

    public IReadOnlyList<SpecThing> Things { get; init; } = [];

    public IReadOnlyList<SpecPawn> Pawns { get; init; } = [];

    /// <summary>The stored defense layout the policy reads, unknown without one.</summary>
    public Fact<CombatLayout> Layout { get; init; }

    /// <summary>The hostiles' lord; null leaves <see cref="Fight.Raid"/> unset.</summary>
    public SpecRaid? Raid { get; init; }

    /// <summary>The game ticks per Step.</summary>
    public int StepTicks { get; init; }

    /// <summary>Ends an unresolved fight.</summary>
    public int MaxTicks { get; init; }
}

/// <summary>
/// The live state Pawn_HealthTracker.CheckForStateChange reads for a non-colony humanlike: the storyteller's
/// population intent and two difficulty settings. No catalog def holds them.
/// </summary>
public sealed record DeathOnDowned
{
    /// <summary>StorytellerUtilityPopulation.PopulationIntent.</summary>
    public double PopulationIntent { get; init; }

    /// <summary>The difficulty's setting.</summary>
    public double EnemyDeathOnDownedChanceFactor { get; init; }

    /// <summary>The difficulty's setting; false selects the WaveringPrisoners curve.</summary>
    public bool UnwaveringPrisoners { get; init; }
}

/// <summary>
/// One structure or item standing on a cell. Stuff does not change any value the sim reads (fill percent, path
/// cost, passability), so it is not carried.
/// </summary>
public sealed record SpecThing(string Def, Cell Cell);

/// <summary>One worn apparel: its def and stuff (empty for none).</summary>
public sealed record Wear(string Def, string Stuff);

/// <summary>
/// One trait a pawn carries: its TraitDef and degree. Only its stat offsets and factors are read (the stats of
/// the trace); the front-line split and every other rule treat the pawn as traitless, so a caller lists only
/// traits that touch stats (a lab crew's SpeedOffset).
/// </summary>
public sealed record SpecTrait(string Def, int Degree);

/// <summary>
/// One combatant. Only healthy adult pawns of a humanlike race are described: their health, capacities and
/// needs are the catalog baseline, and a lab-staged pawn is exactly that. Traits are the one story detail
/// carried.
/// </summary>
public sealed record SpecPawn
{
    public PawnId Id { get; init; }

    public string Side { get; init; } = "";

    /// <summary>The PawnKindDef name; its race def gives body size, health scale and natural armor.</summary>
    public string Kind { get; init; } = "";

    public Cell Cell { get; init; }

    /// <summary>The wielded weapon def (empty for none). A weapon that is not an ordinary ranged weapon leaves the pawn unarmed and is listed in <see cref="ImportResult.Unmodeled"/>.</summary>
    public string Weapon { get; init; } = "";

    public string WeaponStuff { get; init; } = "";

    /// <summary>The worn apparel outermost first. Unknown fails the import: the sim must not assume a pawn is unarmored.</summary>
    public Fact<IReadOnlyList<Wear>> Worn { get; init; }

    /// <summary>The SkillRecord levels by SkillDef name. Null fails the import.</summary>
    public IReadOnlyDictionary<string, int>? Skills { get; init; }

    /// <summary>The pawn's story traits: MoveSpeed and the shooting stats read their offsets and factors.</summary>
    public IReadOnlyList<SpecTrait> Traits { get; init; } = [];

    /// <summary>The hostile's lord evidence; unknown reaches the policy as unknown.</summary>
    public Fact<string> LordJobClass { get; init; }

    public Fact<string> LordToilClass { get; init; }
}

/// <summary>
/// The hostiles' LordJob_AssaultColony as the sim needs it. Importing a Spec with a Raid sets
/// <see cref="Fight.Raid"/>, which drives the hostile ranged pawns; without one the caller drives them through
/// <see cref="Fight.Scripted"/>.
/// </summary>
public sealed record SpecRaid
{
    /// <summary>The lord's canTimeoutOrFlee: true for an ordinary raid, which gives up after AssaultTimeBeforeGiveUp or on enough colony damage. It needs Exit.</summary>
    public bool CanTimeoutOrFlee { get; init; }

    /// <summary>The cell fleeing raiders walk to and leave by. No catalog def or fixture holds it (the game picks a map-edge spot), so the caller states it.</summary>
    public Fact<Cell> Exit { get; init; }

    /// <summary>
    /// The lord's Trigger_FractionPawnsLost fraction (<see cref="EnemyConfig.AutoFleeFraction"/>): the game
    /// draws it from the faction's attackersDownPercentageRangeForAutoFlee seeded by the lord's load id, so
    /// the caller states it. Unknown: the raid never panics. Needs Exit.
    /// </summary>
    public Fact<double> AutoFleeFraction { get; init; }

    /// <summary>Each raider's staged target in a resumed checkpoint (<see cref="EnemyConfig.FirstTargets"/>, A44); empty: every raider draws.</summary>
    public IReadOnlyDictionary<PawnId, PawnId> FirstTargets { get; init; } = new Dictionary<PawnId, PawnId>();
}

/// <summary>Records where one derived value came from.</summary>
/// <param name="Subject">A pawn id, or "map".</param>
/// <param name="Field">The combatsim field or fact that was derived.</param>
/// <param name="Source">The catalog def and member, stat or game constant read.</param>
public sealed record ImportTrace(string Subject, string Field, string Source);

/// <summary>An imported fight.</summary>
public sealed record ImportResult
{
    /// <summary>Ready for <see cref="CombatRunner.RunFight"/>; a Spec without a Raid leaves the hostiles to the caller's <see cref="Fight.Scripted"/>.</summary>
    public Fight Fight { get; init; } = new();

    /// <summary>Every derived value with its source.</summary>
    public IReadOnlyList<ImportTrace> Trace { get; init; } = [];

    /// <summary>What the Spec holds that the sim leaves out (a melee weapon, say). The fight runs without it: read it before trusting a result.</summary>
    public IReadOnlyList<string> Unmodeled { get; init; } = [];
}
