package simimport

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/bridge/stateval"
	"github.com/davidarcher/RimGovernor/go/internal/combatsim"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// Weapon stat names and the game's constants the effective weapon values are
// read with.
const (
	statCooldown        = "RangedWeapon_Cooldown"
	statDamageMult      = "RangedWeapon_DamageMultiplier"
	statPenetrationMult = "RangedWeapon_ArmorPenetrationMultiplier"
	shootVerbClass      = "Verse.Verb_Shoot"
	meleeVerbClass      = "RimWorld.Verb_MeleeAttack"
	// penetrationPerDamage is ProjectileProperties.GetArmorPenetration's
	// fallback for a projectile that states none, per point of damage.
	penetrationPerDamage = 0.015
	// projectileSpeedUnit divides ProjectileProperties.speed into
	// SpeedTilesPerTick.
	projectileSpeedUnit = 100
)

// damage is the ranged damage def every weapon in the Spec deals. The injury
// thresholds and the armor list depend on it, so a Spec mixing damage kinds is
// unsupported (A25).
type damage struct {
	def, armorCategory, hediff string
	// armorStat is the armor category's armorRatingStat, "" for damage that no
	// armor rates.
	armorStat string
	// harmAllLayers is the damage def's harmAllLayersUntilOutside and
	// overkillLow/High its overkillPctToDestroyPart.
	harmAllLayers             bool
	overkillLow, overkillHigh float64
}

// weapon is one pawn's weapon resolved.
type weapon struct {
	ranged *combatsim.WeaponDef
	// meleeDPS is the catalog DPS of a melee weapon the sim cannot fire.
	meleeDPS float64
	melee    bool
}

// weapons resolves every pawn's weapon and the Spec's one damage def.
func (im *importer) weapons(spec Spec) ([]weapon, error) {
	out := make([]weapon, len(spec.Pawns))
	for i, p := range spec.Pawns {
		if p.Weapon == "" {
			continue
		}
		facts, err := im.cat.WeaponOf(p.Weapon)
		if err != nil {
			return nil, unsupported("pawn %q weapon %q: %v", p.ID, p.Weapon, err)
		}
		switch {
		case facts.Ranged:
			if facts.Explosive || facts.Launcher || facts.OneUse || facts.EMP || facts.Incendiary {
				return nil, unsupported("pawn %q weapon %q is not ordinary ranged fire", p.ID, p.Weapon)
			}
			w, err := im.rangedWeapon(string(p.ID), p.Weapon, p.WeaponStuff)
			if err != nil {
				return nil, err
			}
			out[i].ranged = w
		case facts.Melee:
			out[i].melee, out[i].meleeDPS = true, facts.DPS
			im.res.Unmodeled = append(im.res.Unmodeled, "pawn "+string(p.ID)+" wields melee weapon "+p.Weapon+": the sim has no melee, the pawn is imported unarmed")
		default:
			return nil, unsupported("pawn %q weapon %q is neither ranged nor melee", p.ID, p.Weapon)
		}
	}
	if im.dmg == nil {
		return nil, unsupported("no pawn wields an ordinary ranged weapon: there is no damage to derive injury thresholds from")
	}
	return out, nil
}

// rangedWeapon reads one weapon's effective values: the verb's range, warmup
// and burst from its row, the cooldown, accuracy bands and damage multipliers
// from the weapon's stats (normal quality), the projectile's damage and speed
// from its row (A27).
func (im *importer) rangedWeapon(pawn, def, stuff string) (*combatsim.WeaponDef, error) {
	row := im.cat.ThingDef(def)
	if row == nil {
		return nil, unsupported("catalog has no weapon def %q", def)
	}
	var verb *d.VerbProperties
	for _, entry := range row.GetVerbs() {
		melee, err := im.cat.ClassIsA(entry.GetValue().GetVerbClass(), meleeVerbClass)
		if err != nil {
			return nil, unsupported("weapon %q verb class: %v", def, err)
		}
		if !melee {
			verb = entry.GetValue()
			break
		}
	}
	if verb == nil {
		return nil, unsupported("weapon %q has no ranged verb", def)
	}
	if shoot, err := im.cat.ClassIsA(verb.GetVerbClass(), shootVerbClass); err != nil || !shoot {
		return nil, unsupported("weapon %q verb %q is not Verb_Shoot", def, verb.GetVerbClass())
	}
	if verb.GetRangeStat() != "" {
		return nil, unsupported("weapon %q verb range comes from pawn stat %q", def, verb.GetRangeStat())
	}
	projectile := im.cat.ThingDef(verb.GetDefaultProjectile())
	props := projectile.GetProjectile()
	if props == nil {
		return nil, unsupported("weapon %q has no projectile def %q", def, verb.GetDefaultProjectile())
	}
	dd := bridge.DefRow[*d.DamageDef](im.cat, props.GetDamageDef())
	if dd == nil {
		return nil, unsupported("catalog has no damage def %q", props.GetDamageDef())
	}
	if err := im.settleDamage(props.GetDamageDef(), dd); err != nil {
		return nil, err
	}

	stat := func(name string) (float64, error) {
		v, err := im.ev.Value(name, stateval.ThingSubject(def, stuff))
		if err != nil {
			return 0, unsupported("weapon %q stat %s: %v", def, name, err)
		}
		return float64(v), nil
	}
	w := &combatsim.WeaponDef{
		Name: def, Range: float64(verb.GetRange()), MinRange: float64(verb.GetMinRange()),
		WarmupSeconds: float64(verb.GetWarmupTime()), BurstShotCount: int(verb.GetBurstShotCount()),
		TicksBetweenBurstShots: int(verb.GetTicksBetweenBurstShots()),
		ProjectileSpeed:        float64(props.GetSpeed()) / projectileSpeedUnit,
		ForcedMissRadius:       float64(verb.GetForcedMissRadius()),
	}
	if rc := im.weatherCap; rc >= 0 {
		w.Range = math.Min(w.Range, float64(rc))
	}
	var err error
	for _, band := range []struct {
		dst  *float64
		stat string
	}{
		{&w.CooldownSeconds, statCooldown},
		{&w.AccuracyTouch, "AccuracyTouch"}, {&w.AccuracyShort, "AccuracyShort"},
		{&w.AccuracyMedium, "AccuracyMedium"}, {&w.AccuracyLong, "AccuracyLong"},
	} {
		if *band.dst, err = stat(band.stat); err != nil {
			return nil, err
		}
	}
	// ProjectileProperties.GetDamageAmount: the stated amount or the damage
	// def's default, times the weapon's damage multiplier, rounded.
	amount := float64(props.GetDamageAmountBase())
	if props.GetDamageAmountBase() == -1 {
		amount = float64(dd.GetDefaultDamage())
	}
	mult, err := stat(statDamageMult)
	if err != nil {
		return nil, err
	}
	w.DamageAmount = int(math.RoundToEven(amount * mult))
	// ProjectileProperties.GetArmorPenetration.
	if dd.GetArmorCategory() != "" {
		ap := float64(props.GetArmorPenetrationBase())
		if props.GetDamageAmountBase() == -1 && props.GetArmorPenetrationBase() < 0 {
			ap = float64(dd.GetDefaultArmorPenetration())
		}
		if ap < 0 {
			ap = amount * penetrationPerDamage
		}
		apMult, err := stat(statPenetrationMult)
		if err != nil {
			return nil, err
		}
		w.ArmorPenetration = ap * apMult
	}
	src, proj := "ThingDef "+def, "ThingDef "+verb.GetDefaultProjectile()
	for _, t := range [][2]string{
		{"Weapon.Range", src + " verb range, WeatherDef maxRangeCap"}, {"Weapon.MinRange", src + " verb minRange"},
		{"Weapon.WarmupSeconds", src + " verb warmupTime"}, {"Weapon.CooldownSeconds", "stat " + statCooldown},
		{"Weapon.BurstShotCount", src + " verb burstShotCount"},
		{"Weapon.TicksBetweenBurstShots", src + " verb ticksBetweenBurstShots"},
		{"Weapon.AccuracyTouch", "stat AccuracyTouch"}, {"Weapon.AccuracyShort", "stat AccuracyShort"},
		{"Weapon.AccuracyMedium", "stat AccuracyMedium"}, {"Weapon.AccuracyLong", "stat AccuracyLong"},
		{"Weapon.ProjectileSpeed", proj + " projectile speed"},
		{"Weapon.DamageAmount", proj + " damageAmountBase or DamageDef " + props.GetDamageDef() + " defaultDamage, stat " + statDamageMult},
		{"Weapon.ArmorPenetration", proj + " armorPenetrationBase or DamageDef " + props.GetDamageDef() + " defaultArmorPenetration, stat " + statPenetrationMult},
		{"Weapon.ForcedMissRadius", src + " verb forcedMissRadius"},
	} {
		im.trace(pawn, t[0], t[1])
	}
	return w, nil
}

// settleDamage records the Spec's damage def and reads the pain it causes, or
// fails when a second, different damage def appears.
func (im *importer) settleDamage(name string, dd *d.DamageDef) error {
	if im.dmg != nil {
		if im.dmg.def != name {
			return unsupported("weapons deal two damage defs (%q and %q): thresholds and armor are derived for one", im.dmg.def, name)
		}
		return nil
	}
	if dd.GetHediff() == "" {
		return unsupported("damage def %q applies no hediff", name)
	}
	hediff := bridge.DefRow[*d.HediffDef](im.cat, dd.GetHediff())
	if hediff == nil || hediff.GetInjuryProps() == nil {
		return unsupported("catalog has no injury hediff %q for damage def %q", dd.GetHediff(), name)
	}
	if !(hediff.GetInjuryProps().GetPainPerSeverity() > 0) {
		return unsupported("hediff %q causes no pain per severity", dd.GetHediff())
	}
	overkill := dd.GetOverkillPctToDestroyPart()
	im.dmg = &damage{
		def: name, armorCategory: dd.GetArmorCategory(), hediff: dd.GetHediff(), harmAllLayers: dd.GetHarmAllLayersUntilOutside(),
		overkillLow: float64(overkill.GetMin()), overkillHigh: float64(overkill.GetMax()),
	}
	if dd.GetArmorCategory() != "" {
		cat := bridge.DefRow[*d.DamageArmorCategoryDef](im.cat, dd.GetArmorCategory())
		if cat == nil || cat.GetArmorRatingStat() == "" {
			return unsupported("catalog has no armor category %q for damage def %q", dd.GetArmorCategory(), name)
		}
		im.dmg.armorStat = cat.GetArmorRatingStat()
	}
	return nil
}
