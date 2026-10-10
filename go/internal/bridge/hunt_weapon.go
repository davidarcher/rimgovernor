package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// bulletProjectileClass is the exact class the game's Bullet projectile has;
// every other projectile class (explosive, beam, rocket) is Other.
const bulletProjectileClass = "RimWorld.Bullet"

// HuntWeapon is what the def rows say of a hunter's primary weapon (the rule
// the native hunt census used to state over ThingDef.verbs): whether the def is
// a ranged or melee weapon, and each verb's range, warmup, ai-weapon flag,
// default projectile class, explosion radius and damage def and worker. A def
// that is no weapon is the unarmed zero value; a def without a row, a
// projectile row or a damage def row is a contract error.
func (catalog *DefinitionCatalog) HuntWeapon(name string) (*policy.HuntWeapon, error) {
	if name == "" {
		return nil, nil
	}
	row, err := catalog.thingRow(name)
	if err != nil {
		return nil, err
	}
	weapon := &policy.HuntWeapon{DefName: name}
	// ThingDef.IsWeapon: an item with verbs or tools that is no apparel.
	if row.GetCategory() != d.ThingCategory_THING_CATEGORY_ITEM || len(row.GetVerbs())+len(row.GetTools()) == 0 || row.GetApparel() != nil {
		return weapon, nil
	}
	weapon.Melee = len(row.GetTools()) > 0
	for _, entry := range row.GetVerbs() {
		props := entry.GetValue()
		melee, err := catalog.ClassIsA(props.GetVerbClass(), meleeVerbClass)
		if err != nil {
			return nil, err
		}
		if !melee {
			weapon.Ranged = true
		}
		verb := policy.HuntVerb{Melee: melee, AIWeapon: props.GetAi_IsWeapon(), Range: float64(props.GetRange()), Warmup: float64(props.GetWarmupTime())}
		if projectile := props.GetDefaultProjectile(); projectile != "" {
			projectileRow, err := catalog.thingRow(projectile)
			if err != nil {
				return nil, err
			}
			verb.Projectile = policy.HuntProjectileOther
			if projectileRow.GetThingClass() == bulletProjectileClass {
				verb.Projectile = policy.HuntProjectileBullet
			}
			if properties := projectileRow.GetProjectile(); properties != nil {
				verb.ExplosionRadius = float64(properties.GetExplosionRadius())
				if damage := properties.GetDamageDef(); damage != "" {
					damageRow := DefRow[*d.DamageDef](catalog, damage)
					if damageRow == nil {
						return nil, contract("catalog has no damage def row for %s, the damage of %s", damage, name)
					}
					verb.DamageDef, verb.DamageWorker = damage, damageRow.GetWorkerClass()
				}
			}
		}
		weapon.Verbs = append(weapon.Verbs, verb)
	}
	return weapon, nil
}
