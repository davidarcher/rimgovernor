using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Simulation.Combat;

// Port of Go internal/combatsim: a deterministic combat simulator, a mechanics-faithful port of the game's
// grid rules (line of sight and lean, cover, per-cell movement cost) and of ordinary ranged resolution (hit
// chance, warmup, burst and cooldown, projectile flight and interception, armor, an injury approximation)
// behind a Reset/Step seam. Adapter and CombatRunner let the unchanged policy DecideCombat drive a fight:
// they build its CombatView, StopEvent and GeometryReply from sim state and apply its orders. EnemyAI drives
// the hostile ranged raiders: target scoring and choice, positioning, and the raid's flee triggers, all
// drawn from the enemy stream. Every simplification is listed in approximations.md.
//
// Time is the game tick. Reset(scenario, seed) builds the world and the simulator-owned random streams
// (physical outcomes and enemy choice); Step applies orders and advances Scenario.StepTicks ticks, returning
// the new state and the events of those ticks. The simulator never reads or clones native RNG state, and
// nothing it holds is persisted. A scenario missing required state fails Reset with
// UnsupportedScenarioException; no value is guessed.
//
// Definition values (speeds, cover and path costs) arrive in the Scenario, read from the native catalog by
// the caller; this package holds none. Rules ported from the game name their source and are versioned by
// RulesVersion.AssemblyCSharpSha256.
//
// Not modeled yet, by design: snow, sand and fire path costs, terrain-tag speed factors,
// downed/crawling/restrained/carrying movement, door closing and door-open delays, flying. Pawns share cells
// and only a standing hostile blocks a walker (A35); the rest of PatherTick is a port.

/// <summary>
/// The scenario cannot be represented faithfully: required state is missing or outside what the core models
/// (Go <c>ErrUnsupported</c>). The simulator never guesses a value for it.
/// </summary>
public sealed class UnsupportedScenarioException(string reason) : DomainException("combatsim: unsupported scenario: " + reason);

/// <summary>An order named a pawn the scenario does not hold (Go <c>unknownPawn</c>).</summary>
public sealed class UnknownPawnException(PawnId pawn) : DomainException($"combatsim: order for unknown pawn \"{pawn}\"");

/// <summary>A job's LocomotionUrgency; the default is Jog, the game's default.</summary>
public enum Urgency : byte
{
    Jog,
    Sprint,
    Walk,
    Amble,
}

/// <summary>A pawn at Reset.</summary>
public sealed record PawnSpec
{
    public PawnId Id { get; init; }

    public Cell Pos { get; init; }

    /// <summary>The pawn's MoveSpeed stat in cells per second. Unknown marks the scenario unsupported.</summary>
    public Fact<double> MoveSpeed { get; init; }

    /// <summary><see cref="Sides.Colonist"/> or <see cref="Sides.Hostile"/>; required when Combat is set.</summary>
    public string Side { get; init; } = "";

    /// <summary>Makes the pawn a combatant: it can be shot and, with a Weapon, shoot. Null marks a non-combatant that shots pass through and orders cannot target.</summary>
    public CombatSpec? Combat { get; init; }
}

/// <summary>The simulator input. Reset copies it; the simulator never mutates it.</summary>
public sealed record Scenario
{
    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>
    /// The palettes; the id grids index them. A TerrainIds value is an index into Terrain; a ThingIds value
    /// k &gt; 0 is Things[k-1] and 0 is no thing. Each cell holds at most one thing, the one that dominates
    /// cover and sight.
    /// </summary>
    public IReadOnlyList<TerrainDef> Terrain { get; init; } = [];

    public IReadOnlyList<ThingDef> Things { get; init; } = [];

    /// <summary>Width*Height, row-major by z; null means all Terrain[0].</summary>
    public IReadOnlyList<byte>? TerrainIds { get; init; }

    /// <summary>Width*Height; null means no things.</summary>
    public IReadOnlyList<byte>? ThingIds { get; init; }

    /// <summary>Width*Height; null means no roofs.</summary>
    public IReadOnlyList<bool>? Roofed { get; init; }

    /// <summary>Door cells that start open.</summary>
    public IReadOnlyList<Cell> OpenDoors { get; init; } = [];

    /// <summary>The weather's move speed multiplier outside roofs. Unknown marks the scenario unsupported.</summary>
    public Fact<double> WeatherMoveSpeed { get; init; }

    /// <summary>
    /// The weather's shooting accuracy multiplier (CurWeatherAccuracyMultiplier), applied unless both shooter
    /// and target are roofed. Required once any pawn has Combat set; unknown marks the scenario unsupported.
    /// </summary>
    public Fact<double> WeatherAccuracy { get; init; }

    /// <summary>The difficulty's friendlyFireChanceFactor; required like <see cref="WeatherAccuracy"/>.</summary>
    public Fact<double> FriendlyFireChanceFactor { get; init; }

    /// <summary>The game ticks one Step advances; it must be positive.</summary>
    public int StepTicks { get; init; }

    public IReadOnlyList<PawnSpec> Pawns { get; init; } = [];
}

/// <summary>Selects an Order.</summary>
public enum OrderKind : byte
{
    /// <summary>Walks the pawn to To.</summary>
    Move,

    /// <summary>Halts the pawn where it stands.</summary>
    Stop,

    /// <summary>Makes the pawn shoot Target until it is down, dead or the pawn is given another order. It stands and waits while Target is out of range or sight.</summary>
    Attack,

    /// <summary>
    /// Takes the pawn off the map (the enemy AI orders it on its exit cell). The pawn stops being a
    /// combatant: shots pass through it and it is no longer a target.
    /// </summary>
    Leave,
}

/// <summary>One pawn command applied at the start of a Step.</summary>
public sealed record Order
{
    public PawnId Pawn { get; init; }

    public OrderKind Kind { get; init; }

    public Cell To { get; init; }

    public Urgency Urgency { get; init; }

    /// <summary>The pawn an Attack shoots at.</summary>
    public PawnId Target { get; init; }

    /// <summary>
    /// Makes an Attack one burst: the pawn drops its target when the burst's cooldown ends, as a verb started
    /// with Pawn.TryStartAttack does, so whoever chooses targets chooses again.
    /// </summary>
    public bool Once { get; init; }

    /// <summary>
    /// Makes an Attack JobDriver_Wait.CheckForAutoAttack's Pawn.TryStartAttack: the verb starts the moment the
    /// order lands (the tick it is applied at), so its warmup counts down from the next tick. An attack
    /// without Auto is JobDriver_AttackStatic's: the order starts the job and the job's first tick starts the
    /// verb, one tick later.
    /// </summary>
    public bool Auto { get; init; }
}

/// <summary>Names what happened.</summary>
public enum EventKind : byte
{
    /// <summary>A path was found; ETA is the tick the pawn arrives.</summary>
    MoveStarted = 1,

    /// <summary>The pawn entered Cell.</summary>
    Moved,

    /// <summary>The pawn reached its destination Cell.</summary>
    Arrived,

    /// <summary>Another pawn stands on the pawn's next Cell; it waits.</summary>
    Blocked,

    /// <summary>No path to Cell (not standable, out of bounds or unreachable).</summary>
    PathFailed,

    /// <summary>The pawn approached the closed door at Cell.</summary>
    DoorOpened,

    /// <summary>A stop order halted the pawn at Cell.</summary>
    Stopped,

    /// <summary>Pawn fired at Target; Outcome says where the shot goes.</summary>
    Shot,

    /// <summary>A projectile of Pawn hit Target (the victim, which may not be the pawn aimed at) for Amount damage after armor.</summary>
    Hit,

    /// <summary>Pawn is down; Target is the instigator of the hit that did it.</summary>
    Downed,

    /// <summary>Pawn is dead; Target is the instigator of the hit that did it.</summary>
    Died,

    /// <summary>Pawn's attack order ended because Target is down or dead.</summary>
    AttackEnded,

    /// <summary>Pawn left the map (Leave); Cell is where it stood.</summary>
    LeftMap,
}

/// <summary>What a fired shot is already decided to do.</summary>
public enum ShotOutcome : byte
{
    /// <summary>Aimed at the target; it hits it unless a pawn intercepts it (a Hit follows when the projectile lands).</summary>
    Flying = 1,

    /// <summary>Missed wide, toward a cell around the target. The projectile flies on and may strike a thing or a pawn there (A7).</summary>
    Wild,

    /// <summary>Sent into the cover next to the target. It strikes the cover unless something on the way stops it first (A7).</summary>
    Cover,
}

/// <summary>One thing that happened during a Step.</summary>
public readonly record struct SimEvent
{
    public int Tick { get; init; }

    public EventKind Kind { get; init; }

    public PawnId Pawn { get; init; }

    public Cell Cell { get; init; }

    /// <summary>Set on MoveStarted: the arrival tick, and on a Shot: the tick its projectile lands or is destroyed.</summary>
    public int ETA { get; init; }

    /// <summary>Set by the combat events.</summary>
    public PawnId Target { get; init; }

    /// <summary>Set by the combat events.</summary>
    public double Amount { get; init; }

    /// <summary>Set by the combat events.</summary>
    public ShotOutcome Outcome { get; init; }

    /// <summary>Set on a Shot whose projectile ends in a non-pawn thing (cover or a wall): it hits no pawn.</summary>
    public bool StopsInThing { get; init; }

    /// <summary>Set on a Shot: the cell the projectile is sent to (the target's cell for an aimed shot, the cover or wild cell otherwise).</summary>
    public Cell Dest { get; init; }

    /// <summary>Set on a Shot: the cell the target stood in.</summary>
    public Cell TargetCell { get; init; }

    /// <summary>On a Hit, the victim's summary health after the hit.</summary>
    public double Health { get; init; }

    /// <summary>On a Hit, the victim's summary health before the hit (the serious-injury stop's crossing tests, A22).</summary>
    public double HealthBefore { get; init; }

    /// <summary>On a Hit, the victim's total bleed rate after the hit.</summary>
    public double Bleed { get; init; }

    /// <summary>On a Hit, the victim's total bleed rate before the hit.</summary>
    public double BleedBefore { get; init; }
}

/// <summary>A pawn's observable state.</summary>
public readonly record struct PawnState(
    PawnId Id,
    Cell Pos,
    bool Moving,
    Cell Dest,
    double Damage,
    bool Downed,
    bool Dead);

/// <summary>
/// The world after a Step. Pawns is in scenario order. Both the state and the event list Step returns alias
/// buffers reused by the next Step; copy what must outlive it.
/// </summary>
public sealed record SimState(int Tick, IReadOnlyList<PawnState> Pawns);

/// <summary>
/// The simulator-owned random streams, seeded from the Reset seed. Physical draws decide physical outcomes
/// (hits, damage); Enemy draws decide enemy choice. They are separate so a change in enemy behavior does not
/// shift physical draws.
/// </summary>
public sealed record Streams(GoRandom Physical, GoRandom Enemy, GoRandom Phase, GoRandom Colonist)
{
    // Stream selectors for the PCG second word: ASCII "physical", "enemy", "phase" and "colonist".
    internal const ulong PhysicalStream = 0x706879736963616c;
    internal const ulong EnemyStream = 0x656e656d79;
    internal const ulong PhaseStream = 0x7068617365;
    internal const ulong ColonistStream = 0x636f6c6f6e697374;
}

/// <summary>Fills of a thing def (FillCategory).</summary>
public enum Fill : byte
{
    None,
    Partial,
    Full,
}

/// <summary>A thing def's Traversability.</summary>
public enum Passability : byte
{
    /// <summary>Things that can be stood on (loose items).</summary>
    Standable,

    /// <summary>Things that can be walked through but not stopped on (doors, sandbags).</summary>
    PassThroughOnly,

    /// <summary>Things that cannot be entered (walls, fences).</summary>
    Impassable,
}

/// <summary>
/// The definition values the core reads from the native catalog; the package holds no def values of its own.
/// A cell holds at most one thing: the one that dominates cover and sight (see <see cref="Scenario.Things"/>).
/// </summary>
public sealed record ThingDef
{
    public string Name { get; init; } = "";

    public Fill Fillage { get; init; }

    public double FillPercent { get; init; }

    public Passability Passability { get; init; }

    /// <summary>ThingDef.pathCost.</summary>
    public int PathCost { get; init; }

    /// <summary>Marks a Building_Door: it blocks sight while closed and a pawn approaching it opens it.</summary>
    public bool Door { get; init; }
}

/// <summary>Terrain values the core reads from the native catalog.</summary>
public sealed record TerrainDef
{
    public string Name { get; init; } = "";

    public int PathCost { get; init; }

    /// <summary>False for Traversability.Impassable terrain (deep water).</summary>
    public bool Passable { get; init; }
}

/// <summary>
/// A ranged weapon's verb as the game reads it for one pawn: the catalog values after the weapon's own stat
/// multipliers (range, warmup, cooldown and accuracy are the effective numbers, not the raw def fields). The
/// package holds no weapon values; a zero field that the rules need positive fails Reset with
/// <see cref="UnsupportedScenarioException"/>.
/// </summary>
public sealed record WeaponDef
{
    public string Name { get; init; } = "";

    /// <summary>Verb.EffectiveRange, in cells.</summary>
    public double Range { get; init; }

    /// <summary>VerbProperties.EffectiveMinRange, in cells.</summary>
    public double MinRange { get; init; }

    /// <summary>Verb.WarmupTime.</summary>
    public double WarmupSeconds { get; init; }

    /// <summary>The weapon's RangedWeapon_Cooldown stat (VerbProperties.AdjustedCooldown before the shooter's factor).</summary>
    public double CooldownSeconds { get; init; }

    /// <summary>Verb.BurstShotCount.</summary>
    public int BurstShotCount { get; init; }

    /// <summary>The verb's ticksBetweenBurstShots.</summary>
    public int TicksBetweenBurstShots { get; init; }

    // VerbProperties.AdjustedAccuracy per range band.
    public double AccuracyTouch { get; init; }

    public double AccuracyShort { get; init; }

    public double AccuracyMedium { get; init; }

    public double AccuracyLong { get; init; }

    /// <summary>The projectile's SpeedTilesPerTick.</summary>
    public double ProjectileSpeed { get; init; }

    /// <summary>Projectile.DamageAmount after the weapon's quality.</summary>
    public int DamageAmount { get; init; }

    /// <summary>The projectile's armor penetration after the weapon's quality.</summary>
    public double ArmorPenetration { get; init; }

    /// <summary>VerbProperties.ForcedMissRadius; above 0.5 (mortars and the like) the weapon is not ordinary ranged fire and is unsupported.</summary>
    public double ForcedMissRadius { get; init; }

    /// <summary>
    /// VerbProperties.ai_TargetHasRangedAttackScoreOffset, the bonus the enemy AI's target score gives a target
    /// that carries a ranged weapon. Zero for most weapons.
    /// </summary>
    public double TargetHasRangedAttackScoreOffset { get; init; }
}

/// <summary>The pawn stats ShotReport and the verb read from the shooter. Unknown marks the scenario unsupported.</summary>
public sealed record ShooterStats
{
    public Fact<double> ShootingAccuracyPawn { get; init; }

    /// <summary>ShootingAccuracyFactor_Touch.</summary>
    public Fact<double> AccuracyTouch { get; init; }

    /// <summary>ShootingAccuracyFactor_Short.</summary>
    public Fact<double> AccuracyShort { get; init; }

    /// <summary>ShootingAccuracyFactor_Medium.</summary>
    public Fact<double> AccuracyMedium { get; init; }

    /// <summary>ShootingAccuracyFactor_Long.</summary>
    public Fact<double> AccuracyLong { get; init; }

    public Fact<double> AimingDelayFactor { get; init; }

    public Fact<double> RangedCooldownFactor { get; init; }
}

/// <summary>What makes a pawn a combatant.</summary>
public sealed record CombatSpec
{
    /// <summary>Pawn.BodySize.</summary>
    public Fact<double> BodySize { get; init; }

    /// <summary>The race's CanBenefitFromCover flag.</summary>
    public bool CanBenefitFromCover { get; init; }

    /// <summary>The pawn's per-part injury model (A1, A39), which downs and kills it. The caller derives it from the pawn's body def; null is unsupported.</summary>
    public Body? Body { get; init; }

    /// <summary>
    /// The chance, in [0, 1], that the hit that makes the pawn downable kills it outright instead (A33). Zero,
    /// the default, is a pawn that is only ever downed (a colonist); for a hostile the caller derives what
    /// Pawn_HealthTracker.CheckForStateChange computes for it.
    /// </summary>
    public double DeathOnDownedChance { get; init; }

    /// <summary>
    /// The armor ratings against the projectile's damage category, in the order the game applies them: worn
    /// apparel from the last-worn layer in, then the pawn's own natural rating. Known(empty) is unarmored.
    /// </summary>
    public Fact<IReadOnlyList<double>> Armor { get; init; }

    /// <summary>Null for an unarmed pawn, which cannot be ordered to attack.</summary>
    public WeaponDef? Weapon { get; init; }

    public ShooterStats Shooter { get; init; } = new();
}

/// <summary>The hit report for one shooter and target, with every factor kept apart so tests (and later calibration) can see which moved.</summary>
public sealed record ShotReport
{
    public double Distance { get; init; }

    /// <summary>ShotReport.HitFactorFromShooter.</summary>
    public double FromShooter { get; init; }

    /// <summary>VerbProperties.GetHitChanceFactor.</summary>
    public double FromEquipment { get; init; }

    /// <summary>The weather accuracy (1 when both cells are roofed).</summary>
    public double FromWeather { get; init; }

    /// <summary>The clamped body size.</summary>
    public double FromTargetSize { get; init; }

    /// <summary>The lying-target posture factor.</summary>
    public double FromPosture { get; init; }

    /// <summary>The lying-target execution factor.</summary>
    public double FromExecution { get; init; }

    /// <summary>CoverUtility.CalculateOverallBlockChance.</summary>
    public double CoverBlock { get; init; }

    /// <summary>AimOnTargetChance_StandardTarget.</summary>
    public double AimStandard { get; init; }

    /// <summary>AimOnTargetChance_IgnoringPosture (the chance a shot is not wild).</summary>
    public double AimIgnoringPosture { get; init; }

    /// <summary>AimOnTargetChance.</summary>
    public double Aim { get; init; }

    /// <summary>TotalEstimatedHitChance.</summary>
    public double Total { get; init; }

    /// <summary>PassCoverChance: the chance a shot gets past cover.</summary>
    public double PassCover => 1 - CoverBlock;
}
