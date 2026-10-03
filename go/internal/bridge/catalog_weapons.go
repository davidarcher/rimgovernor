package bridge

import (
	"math"
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// Classes and names the weapon rows are read against (#1723).
const (
	meleeVerbClass  = "RimWorld.Verb_MeleeAttack"
	shootVerbClass  = "Verse.Verb_Shoot"
	oneUseVerbClass = "RimWorld.Verb_ShootOneUse"
	bluntArmorCat   = "Blunt"
	// categoryWeapons is the thing category a stockpile calls a weapon (what
	// a def sits within, parents included).
	categoryWeapons = "Weapons"
)

// WeaponOf is what the def rows say of weapon def name: its ranged verb's
// range, its projectile's explosion, damage and fire, whether every melee
// tool hits blunt, and whether it sits in the Weapons thing category. "" is no weapon and is the zero WeaponDef; a def
// without a row, a verb class, a projectile or a damage def row is a
// contract error.
func (catalog *DefinitionCatalog) WeaponOf(name string) (policy.WeaponDef, error) {
	var out policy.WeaponDef
	if name == "" {
		return out, nil
	}
	row, err := catalog.thingRow(name)
	if err != nil {
		return out, err
	}
	within, err := catalog.categoriesWithin(name, row)
	if err != nil {
		return out, err
	}
	out.ByTrade = within[categoryWeapons]
	ranged := false
	for _, entry := range row.GetVerbs() {
		verb := entry.GetValue()
		melee, err := catalog.ClassIsA(verb.GetVerbClass(), meleeVerbClass)
		if err != nil {
			return out, err
		}
		if melee || ranged {
			continue
		}
		ranged, out.Ranged = true, true
		out.Range = float64(verb.GetRange())
		out.Reach = out.Range
		if out.OneUse, err = catalog.ClassIsA(verb.GetVerbClass(), oneUseVerbClass); err != nil {
			return out, err
		}
		shoot, err := catalog.ClassIsA(verb.GetVerbClass(), shootVerbClass)
		if err != nil {
			return out, err
		}
		if verb.GetDefaultProjectile() == "" {
			continue
		}
		projectile, err := catalog.thingRow(verb.GetDefaultProjectile())
		if err != nil {
			return out, err
		}
		properties := projectile.GetProjectile()
		if properties == nil {
			return out, contract("catalog def %s is the projectile of %s but has no projectile properties", projectile.GetDefName(), name)
		}
		if properties.GetDamageDef() == "" {
			// A projectile that states no damage def (the turret and drone
			// packs' verbs) has no damage to read.
			continue
		}
		damage := DefRow[*d.DamageDef](catalog, properties.GetDamageDef())
		if damage == nil {
			return out, contract("catalog has no damage def row for %s, the damage of %s", properties.GetDamageDef(), name)
		}
		out.EMP = damage.GetCauseStun() && damage.GetExternalViolenceForMechanoids()
		out.Incendiary = properties.GetAi_IsIncendiary()
		out.Explosive = verb.GetForcedMissRadius() > 0 && properties.GetExplosionRadius() > 0
		out.Launcher = out.Explosive && shoot
		if out.Explosive && !out.OneUse && (damage.GetHarmsHealth() || damage.GetCauseStun()) {
			out.Blast = float64(properties.GetExplosionRadius())
		}
		if err := catalog.rangedThroughput(row, verb, properties, damage, &out); err != nil {
			return out, err
		}
	}
	if !ranged && len(row.GetTools()) > 0 {
		out.Melee = true
		out.Blunt = true
		for _, entry := range row.GetTools() {
			for _, capacity := range entry.GetValue().GetCapacities() {
				blunt, err := catalog.bluntCapacity(capacity)
				if err != nil {
					return out, err
				}
				out.Blunt = out.Blunt && blunt
			}
		}
		if err := catalog.meleeThroughput(row, &out); err != nil {
			return out, err
		}
	}
	return out, nil
}

// Stat names the weapon throughput reads off a weapon's statBases.
const (
	statRangedCooldown   = "RangedWeapon_Cooldown"
	statRangedDamageMult = "RangedWeapon_DamageMultiplier"
	statRangedPenMult    = "RangedWeapon_ArmorPenetrationMultiplier"
	statMeleeCooldown    = "MeleeWeapon_CooldownMultiplier"
	statMeleeDamageMult  = "MeleeWeapon_DamageMultiplier"
	statAccuracyShort    = "AccuracyShort"
	statAccuracyMedium   = "AccuracyMedium"
	statAccuracyLong     = "AccuracyLong"
	// ticksPerSecond is Verse.GenTicks.TicksPerRealSecond, which
	// TicksToSeconds divides by.
	ticksPerSecond = 60
	// penetrationPerDamage is the armor penetration an unstated (negative)
	// tool or projectile penetration falls back to, per point of damage
	// (VerbProperties.AdjustedArmorPenetration, ProjectileProperties).
	penetrationPerDamage = 0.015
)

// statBase is the game's GetStatValueAbstract(stat) of a weapon def with no
// stuff: the def's own statBases entry, else the stat def's default.
func (catalog *DefinitionCatalog) statBase(row *d.ThingDef, stat string) (float64, error) {
	if value, ok := statBaseEntry(row, stat); ok {
		return value, nil
	}
	def := DefRow[*d.StatDef](catalog, stat)
	if def == nil {
		return 0, contract("catalog has no %s stat def, the stat of weapon %s", stat, row.GetDefName())
	}
	return float64(def.GetDefaultBaseValue()), nil
}

// statBaseEntry is the def's own statBases entry for stat.
func statBaseEntry(row *d.ThingDef, stat string) (float64, bool) {
	for _, m := range row.GetStatBases() {
		if m.GetValue().GetStat() == stat {
			return float64(m.GetValue().GetValue()), true
		}
	}
	return 0, false
}

// rangedThroughput fills the damage per second, armor penetration, forced
// miss and precision of a ranged weapon from its verb, projectile, damage
// def and stat rows.
//
// The DPS is NOT a game formula: the game states a ranged weapon's damage and
// cycle separately. It is projectile damage times shots per burst over the
// verb's full cycle time (warmup + RangedWeapon_Cooldown + the gaps between
// burst shots), the same cycle VerbProperties.AdjustedFullCycleTime gives a
// melee verb. Damage and penetration follow ProjectileProperties
// (GetDamageAmount, GetArmorPenetration) without a wielder.
func (catalog *DefinitionCatalog) rangedThroughput(row *d.ThingDef, verb *d.VerbProperties, projectile *d.ProjectileProperties, damage *d.DamageDef, out *policy.WeaponDef) error {
	out.ForcedMiss = verb.GetForcedMissRadius() > 0
	// Precision is an accuracy curve that does not fall off before medium
	// range: the weapon states all three bands and its medium or long band
	// is at least its short one.
	short, hasShort := statBaseEntry(row, statAccuracyShort)
	medium, hasMedium := statBaseEntry(row, statAccuracyMedium)
	long, hasLong := statBaseEntry(row, statAccuracyLong)
	out.Precision = hasShort && hasMedium && hasLong && max(medium, long) >= short
	amount := float64(projectile.GetDamageAmountBase())
	if projectile.GetDamageAmountBase() == -1 {
		amount = float64(damage.GetDefaultDamage())
	}
	multiplier, err := catalog.statBase(row, statRangedDamageMult)
	if err != nil {
		return err
	}
	amount = math.Round(amount * multiplier)
	out.AP = 0
	if damage.GetArmorCategory() != "" {
		penetration := float64(projectile.GetArmorPenetrationBase())
		if projectile.GetDamageAmountBase() == -1 && projectile.GetArmorPenetrationBase() < 0 {
			penetration = float64(damage.GetDefaultArmorPenetration())
		}
		if penetration < 0 {
			penetration = amount * penetrationPerDamage
		}
		if penetration, err = catalog.scaled(row, statRangedPenMult, penetration); err != nil {
			return err
		}
		out.AP = penetration
	}
	cooldown, err := catalog.statBase(row, statRangedCooldown)
	if err != nil {
		return err
	}
	burst := max(1, float64(verb.GetBurstShotCount()))
	cycle := float64(verb.GetWarmupTime()) + cooldown + (burst-1)*float64(verb.GetTicksBetweenBurstShots())/ticksPerSecond
	if cycle > 0 {
		out.DPS = amount * burst / cycle
	}
	return nil
}

// scaled is value times the weapon's abstract stat.
func (catalog *DefinitionCatalog) scaled(row *d.ThingDef, stat string, value float64) (float64, error) {
	factor, err := catalog.statBase(row, stat)
	return value * factor, err
}

// meleeThroughput fills a melee weapon's damage per second and armor
// penetration. Each tool, through each maneuver of its capacities, is one
// melee verb whose DPS is the game's VerbUtility.DPS (damage * (1 +
// armor penetration) * accuracyTouch / full cycle time, the cycle being the
// maneuver's warmup plus the tool's cooldown and the burst gaps). The weapon's
// figure averages its verbs by the game's own selection weight
// (VerbProperties.AdjustedMeleeSelectionWeight: damage squared times the
// verb's commonality times the tool's chanceFactor), the mix a wielder
// swings. No stuff is assumed: damage is the tool's power, as the game's
// stuffless abstract value is.
func (catalog *DefinitionCatalog) meleeThroughput(row *d.ThingDef, out *policy.WeaponDef) error {
	cooldownFactor, err := catalog.statBase(row, statMeleeCooldown)
	if err != nil {
		return err
	}
	var weight, dps, penetration float64
	for _, entry := range row.GetTools() {
		tool := entry.GetValue()
		power := float64(tool.GetPower())
		ap := float64(tool.GetArmorPenetration())
		if ap < 0 {
			ap = power * penetrationPerDamage
		} else if ap, err = catalog.scaled(row, statMeleeDamageMult, ap); err != nil {
			return err
		}
		for _, capacity := range tool.GetCapacities() {
			maneuvers, err := catalog.maneuvers(capacity)
			if err != nil {
				return err
			}
			for _, maneuver := range maneuvers {
				verb := maneuver.GetVerb()
				out.Reach = max(out.Reach, float64(verb.GetRange()))
				burst := max(1, float64(verb.GetBurstShotCount()))
				cycle := float64(verb.GetWarmupTime()) + float64(tool.GetCooldownTime())*cooldownFactor + (burst-1)*float64(verb.GetTicksBetweenBurstShots())/ticksPerSecond
				if cycle <= 0 {
					return contract("melee verb %s of weapon %s has a zero cycle time", maneuver.GetDefName(), row.GetDefName())
				}
				w := power * power * float64(verb.GetCommonality()) * float64(tool.GetChanceFactor())
				weight += w
				dps += w * power * (1 + ap) * float64(verb.GetAccuracyTouch()) / cycle
				penetration += w * ap
			}
		}
	}
	if weight > 0 {
		out.DPS, out.AP = dps/weight, penetration/weight
	}
	return nil
}

// maneuvers is every maneuver def of tool capacity: the melee verbs a tool
// of that capacity has (Tool.Maneuvers).
func (catalog *DefinitionCatalog) maneuvers(capacity string) ([]*d.ManeuverDef, error) {
	var out []*d.ManeuverDef
	for _, row := range catalog.Defs[(&d.ManeuverDef{}).ProtoReflect().Descriptor().FullName()] {
		if maneuver, ok := row.(*d.ManeuverDef); ok && maneuver.GetRequiredCapacity() == capacity {
			out = append(out, maneuver)
		}
	}
	if len(out) == 0 {
		return nil, contract("catalog has no maneuver for tool capacity %s", capacity)
	}
	slices.SortFunc(out, func(a, b *d.ManeuverDef) int { return strings.Compare(a.GetDefName(), b.GetDefName()) })
	return out, nil
}

// bluntCapacity is whether every maneuver of tool capacity deals damage of
// the blunt armor category.
func (catalog *DefinitionCatalog) bluntCapacity(capacity string) (bool, error) {
	if catalog == nil {
		return false, contract("no definition catalog")
	}
	found, blunt := false, true
	for _, row := range catalog.Defs[(&d.ManeuverDef{}).ProtoReflect().Descriptor().FullName()] {
		maneuver, ok := row.(*d.ManeuverDef)
		if !ok || maneuver.GetRequiredCapacity() != capacity {
			continue
		}
		found = true
		damage := DefRow[*d.DamageDef](catalog, maneuver.GetVerb().GetMeleeDamageDef())
		if damage == nil {
			return false, contract("catalog has no damage def row for %s, the damage of maneuver %s", maneuver.GetVerb().GetMeleeDamageDef(), maneuver.GetDefName())
		}
		blunt = blunt && damage.GetArmorCategory() == bluntArmorCat
	}
	if !found {
		return false, contract("catalog has no maneuver for tool capacity %s", capacity)
	}
	return blunt, nil
}

// PrimaryWeapon is the def facts of the primary weapon a pawn's equipment
// names. A gear reference carries no def name, so the weapon resolves through
// the frame's things table; known is false while the equipment does not say
// (no armed flag, no primary id), the primary is not among the equipped
// items, or the table does not hold its row yet. An unarmed pawn is known
// with the zero WeaponDef.
func (catalog *DefinitionCatalog) PrimaryWeapon(equipment *o.PawnEquipment, things Things) (weapon policy.WeaponDef, known bool, err error) {
	if equipment == nil || equipment.Armed == nil {
		return weapon, false, nil
	}
	if !equipment.GetArmed() {
		return weapon, true, nil
	}
	if equipment.PrimaryId == nil {
		return weapon, false, nil
	}
	for _, item := range equipment.Equipped {
		if item.GetThing().GetId() != equipment.GetPrimaryId() {
			continue
		}
		row, ok := things.Row(item.GetThing())
		if !ok || row.GetThing().GetDefName() == "" {
			return weapon, false, nil
		}
		weapon, err = catalog.WeaponOf(row.GetThing().GetDefName())
		return weapon, err == nil, err
	}
	return weapon, false, nil
}
