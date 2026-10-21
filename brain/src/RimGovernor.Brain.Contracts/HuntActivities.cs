using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Contracts.Wire.Observations;

/// <summary>
/// The game state the analytic hunt estimate is built from: every colonist who could join a hunt and every huntable
/// animal, each with the game's own combat inputs (<see cref="HuntActorRead.Gun"/>, <see cref="HuntActorRead.Body"/>,
/// <see cref="HuntActorRead.Melee"/>, <see cref="HuntActorRead.Revenge"/>), and the weather's accuracy factor. It carries
/// no chosen roster, no target and no estimate; policy proposes rosters and estimates them from this read.
/// </summary>
public sealed record HuntActivityRead
{
    public bool Known { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<HuntActorRead>))]
    public EquatableArray<HuntActorRead> Hunters { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<HuntActorRead>))]
    public EquatableArray<HuntActorRead> Prey { get; init; }
    /// <summary>The current weather's shot accuracy multiplier (WeatherManager.CurWeatherAccuracyMultiplier).</summary>
    public Fact<double> WeatherAccuracy { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Findings { get; init; }
}

/// <summary>A pawn of the hunt read: Pawn is the game's unique load id, Kind the race (prey) or pawn kind (hunter).</summary>
public sealed record HuntActorRead(string Pawn, string Kind, Cell Location, double Age)
{
    public string Xenotype { get; init; } = "";
    public HuntGearRead? Weapon { get; init; }
    /// <summary>The weapon's longest attack range in cells, unknown when it has no ranged attack verb.</summary>
    public Fact<double> WeaponRange { get; init; }
    /// <summary>Hunters: that ranged attack verb's shot as the game resolves it; null without one.</summary>
    public HuntGunRead? Gun { get; init; }
    /// <summary>The pawn's body as damage meets it; null when native cannot state it (no health tracker).</summary>
    public HuntBodyRead? Body { get; init; }
    /// <summary>Prey: its melee attack averaged over its verbs as the game's MeleeDPS stat does; null when it has no melee verb.</summary>
    public HuntMeleeRead? Melee { get; init; }
    /// <summary>Prey: per hunter, the chance one damage instance from him turns it manhunter, and its pack's chance to join.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<HuntRevengeRead>))]
    public EquatableArray<HuntRevengeRead> Revenge { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<HuntGearRead>))]
    public EquatableArray<HuntGearRead> Apparel { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<HuntSkillRead>))]
    public EquatableArray<HuntSkillRead> Skills { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<HuntTraitRead>))]
    public EquatableArray<HuntTraitRead> Traits { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<HuntHediffRead>))]
    public EquatableArray<HuntHediffRead> Health { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> MissingParts { get; init; }
    /// <summary>Hunters: able to take a hunt order now (free, standing, not mentally broken, hunting not disabled).</summary>
    public bool Available { get; init; }
    /// <summary>Hunters: hauling work is active for the pawn, so it may retrieve a corpse as native hauling work.</summary>
    public bool Hauls { get; init; }
    /// <summary>
    /// Prey: the cell its corpse would be hauled to, the nearest cell of the highest-priority player store that accepts
    /// the corpse (native's own store choice); null when no store accepts it, which leaves every return trip unknown.
    /// </summary>
    public Cell? CorpseStore { get; init; }
    /// <summary>Downed or in a mental state: such a prey is not proposed; such a rostered gunner releases the formation.</summary>
    public bool Downed { get; init; }
    public bool Mental { get; init; }
    /// <summary>Prey: the game's herd of this animal group, empty when it is alone.</summary>
    public string Herd { get; init; } = "";
    /// <summary>Prey meat if it is killed and butchered by a colonist, unknown when native has no stat for it.</summary>
    public Fact<double> Meat { get; init; }
    /// <summary>Prey: nutrition one unit of its meat gives when eaten.</summary>
    public Fact<double> MeatNutrition { get; init; }
}

/// <summary>A worn or held item as the release judges it: its def, stuff and quality (not its wear).</summary>
public sealed record HuntGearRead(string Definition, string Stuff, string Quality);
public sealed record HuntSkillRead(string Skill, int Level);
public sealed record HuntTraitRead(string Definition, int Degree);
/// <summary>A hediff by definition and body part; its severity reaches the estimate through <see cref="HuntBodyRead"/>.</summary>
public sealed record HuntHediffRead(string Definition, string Part);

/// <summary>
/// A ranged shot as ShotReport.HitReportFor and Verb resolve it. Accuracy is the shooter's per-cell ShootingAccuracyPawn
/// and Factor* his ShootingAccuracyFactor_Touch/Short/Medium/Long stats; Weapon* the weapon's hit chance factor at the
/// band edges (3, 12, 25 and 40 cells). GoesWild is verbProps.canGoWild (without it the shooter's factor is 1),
/// IgnoresWeather a unique weapon's IgnoreAccuracyMaluses. WarmupTicks already carries AimingDelayFactor; CooldownTicks
/// is AdjustedCooldownTicks, Burst the burst shot count, BurstGapTicks the ticks between burst shots. Damage and
/// ArmorPenetration are the projectile's per hit, Sharp whether its damage meets sharp armour, PainPerDamage its
/// injury's pain per severity.
/// </summary>
public sealed record HuntGunRead(double Accuracy, double FactorTouch, double FactorShort, double FactorMedium, double FactorLong,
    double WeaponTouch, double WeaponShort, double WeaponMedium, double WeaponLong, bool GoesWild, bool IgnoresWeather,
    int WarmupTicks, int CooldownTicks, int Burst, int BurstGapTicks, double Damage, double ArmorPenetration, bool Sharp, double PainPerDamage);

/// <summary>
/// A body as damage meets it. BodySize is the shot's target size, HealthScale the race's health scale, PainShock the
/// PainShockThreshold stat (IgnoresPainShock: the pawn kind never goes down from pain), Pain the current pain total and
/// Injury the summed severity of current injuries. ArmorSharp and ArmorBlunt are the overall armour the gear tab shows
/// (body-part coverage of natural and worn armour). TicksPerCell is TicksPerMoveCardinal, Dodge the MeleeDodgeChance stat.
/// </summary>
public sealed record HuntBodyRead(double BodySize, double HealthScale, double PainShock, bool IgnoresPainShock, double Pain, double Injury,
    double ArmorSharp, double ArmorBlunt, double TicksPerCell, double Dodge);

/// <summary>
/// A melee attack averaged over the pawn's available melee verbs by selection weight (the MeleeDPS stat's average): its
/// damage, cooldown ticks and armour penetration, the share of sharp damage, and its injuries' pain per severity.
/// HitChance is the MeleeHitChance stat.
/// </summary>
public sealed record HuntMeleeRead(double Damage, double CooldownTicks, double HitChance, double ArmorPenetration, double SharpShare, double PainPerDamage);

/// <summary>
/// One hunter's revenge odds against the prey: Chance is PawnUtility.GetManhunterOnDamageChance for a damage instance he
/// deals (race, difficulty, scaria, his hunting stealth and role); PackChance the chance its packmates within 24 cells
/// join once it turns, 0 when the storyteller allows no big threats.
/// </summary>
public sealed record HuntRevengeRead(string Hunter, double Chance, double PackChance);
