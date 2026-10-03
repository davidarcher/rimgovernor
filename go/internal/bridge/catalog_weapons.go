package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// Classes and names the weapon rows are read against (#1723).
const (
	meleeVerbClass  = "RimWorld.Verb_MeleeAttack"
	shootVerbClass  = "Verse.Verb_Shoot"
	oneUseVerbClass = "RimWorld.Verb_ShootOneUse"
	bluntArmorCat   = "Blunt"
)

// WeaponOf is what the def rows say of weapon def name: its ranged verb's
// range, its projectile's explosion, damage and fire, and whether every
// melee tool hits blunt. "" is no weapon and is the zero WeaponDef; a def
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
	}
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
