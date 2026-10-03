package buildingruntime

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// decodeCombatWithCatalog is bridge.DecodeCombat with a catalog standing in
// for the load's (#1723): the Core weapons the planning tests name, and a
// plain ranged or melee weapon row for every other def a combat pawn holds.
func decodeCombatWithCatalog(frame *o.BundleSnapshot) (bridge.Combat, error) {
	combat, err := bridge.DecodeCombat(frame)
	if err != nil {
		return combat, err
	}
	defs := bridge.CoreWeaponFixtures()
	for _, pawn := range combat.Pawns {
		name := pawn.GetWeapon()
		if name == "" || slices.ContainsFunc(defs, func(d bridge.FixtureDef) bool { return d.Name == name }) {
			continue
		}
		weapon := &bridge.FixtureWeapon{VerbClass: "Verse.Verb_Shoot", Range: float32(pawn.GetWeaponRange()), DamageDef: "Bullet"}
		if pawn.GetWeaponMelee() {
			weapon = &bridge.FixtureWeapon{Capacities: []string{"Cut"}}
		}
		defs = append(defs, bridge.FixtureDef{Name: name, Weapon: weapon})
	}
	combat.Catalog = bridge.FixtureCatalog("load", defs...)
	return combat, nil
}
