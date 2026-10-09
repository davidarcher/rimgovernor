package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// MortarShells are the load's mortar shells by kind: every thing
// def with a projectileWhenLoaded (the game's ThingDef.IsShell) is classified
// by the damage def of that projectile, never by its name.
//
//   - EMP: the damage stuns and applies to mechanoids.
//   - Incendiary: the projectile is incendiary (ai_IsIncendiary) and the
//     damage harms health.
//   - HE: the damage is explosive, harms health and has an armor category,
//     and the blast stays inside safeRadius, the radius at which a shot
//     would reach our own: a shell that big (the antigrain warhead) is not
//     one the counter-battery fires.
//
// A shell that is none of those (smoke, firefoam, tox, deadlife) is skipped,
// and so is a kind more than one shell qualifies for: a kind is never
// guessed, and its def is "". A shell whose projectile or damage def has no
// row is a contract error.
func (catalog *DefinitionCatalog) MortarShells(safeRadius float64) (policy.MortarShells, error) {
	if catalog == nil {
		return policy.MortarShells{}, contract("no definition catalog")
	}
	var he, incendiary, emp []string
	for name, row := range catalog.ThingDefs {
		if row.GetProjectileWhenLoaded() == "" {
			continue
		}
		projectile, err := catalog.thingRow(row.GetProjectileWhenLoaded())
		if err != nil {
			return policy.MortarShells{}, err
		}
		properties := projectile.GetProjectile()
		if properties == nil {
			return policy.MortarShells{}, contract("catalog def %s is the projectile of shell %s but has no projectile properties", projectile.GetDefName(), name)
		}
		damage := DefRow[*d.DamageDef](catalog, properties.GetDamageDef())
		if damage == nil {
			return policy.MortarShells{}, contract("catalog has no damage def row for %s, the damage of shell %s", properties.GetDamageDef(), name)
		}
		switch {
		case damage.GetCauseStun() && damage.GetExternalViolenceForMechanoids():
			emp = append(emp, name)
		case damage.GetHarmsHealth() && properties.GetAi_IsIncendiary():
			incendiary = append(incendiary, name)
		case damage.GetIsExplosive() && damage.GetHarmsHealth() && damage.GetArmorCategory() != "" && float64(properties.GetExplosionRadius()) < safeRadius:
			he = append(he, name)
		}
	}
	only := func(defs []string) string {
		if len(defs) != 1 {
			return ""
		}
		return defs[0]
	}
	return policy.MortarShells{HE: only(he), Incendiary: only(incendiary), EMP: only(emp)}, nil
}
