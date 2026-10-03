package buildingruntime

import (
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// decodeCombatWithCatalog is bridge.DecodeCombat with a catalog standing in
// for the load's (#1723): the Core weapons the planning tests name, and a
// plain ranged or melee weapon row for every other def a combat pawn holds.
func decodeCombatWithCatalog(frame *o.BundleSnapshot, extra ...bridge.FixtureDef) (bridge.Combat, error) {
	combat, err := bridge.DecodeCombat(frame)
	if err != nil {
		return combat, err
	}
	// Recordings made before the frames carried their gear in the things
	// table name each primary weapon only on the pawn's mirror row (its def);
	// the gear ref joins that def here.
	for _, pawn := range combat.Pawns {
		for _, row := range frame.GetPawns().GetPawns() {
			equipment := row.GetEquipment()
			if row.GetPawn().GetId() != pawn.GetId() || equipment.GetPrimaryId() == "" || pawn.GetWeapon() == "" || combat.Things.Has(equipment.GetPrimaryId()) {
				continue
			}
			combat.Things = combat.Things.With(equipment.GetPrimaryId(), &o.Thing{Thing: &o.EntityRef{Id: proto.String(equipment.GetPrimaryId()), DefName: proto.String(pawn.GetWeapon())}})
		}
	}
	defs := append(bridge.CoreWeaponFixtures(), extra...)
	for _, pawn := range combat.Pawns {
		name := pawn.GetWeapon()
		if name == "" || slices.ContainsFunc(defs, func(d bridge.FixtureDef) bool { return d.Name == name }) {
			continue
		}
		// The recordings no longer carry the weapon's class or range (#1723),
		// so an unnamed recorded weapon is a plain 25-cell rifle.
		weapon := &bridge.FixtureWeapon{VerbClass: "Verse.Verb_Shoot", Range: 25, DamageDef: "Bullet"}
		defs = append(defs, bridge.FixtureDef{Name: name, Weapon: weapon})
	}
	// The race rows a live read would carry (#1722): the recordings hold pawn
	// rows, not the catalog, so each animal and mechanoid in the frame is a
	// race here, a recorded Cougar with the predator and manhunter numbers its
	// rows held.
	races := map[string]bool{}
	for row := range combat.Detail.Values() {
		def := row.GetPawn().GetDefName()
		if races[def] {
			continue
		}
		switch {
		case row.GetMechanoid():
			races[def] = true
			defs = append(defs, bridge.FixtureDef{Name: def, Race: &bridge.FixtureRace{Props: &d.RaceProperties{}, Facts: &o.RaceFacts{Mechanoid: true}}})
		case row.GetAnimal():
			races[def] = true
			props, ok := recordedRaces[def]
			if !ok {
				return combat, fmt.Errorf("recorded animal %s has no recorded race row", def)
			}
			defs = append(defs, bridge.FixtureDef{Name: def, Race: &bridge.FixtureRace{Props: props, Facts: &o.RaceFacts{Animal: true}}})
		}
	}
	combat.Catalog = bridge.FixtureCatalog("load", defs...)
	recorded, err := fullCatalogRows()
	if err != nil {
		return combat, err
	}
	for class, rows := range recorded {
		combat.Catalog.Defs[class] = rows
	}
	combat.Shells, err = combat.Catalog.MortarShells(policy.MortarSafeRadius)
	return combat, err
}

// recordedRaces are the race rows the recorded fights' animals had before the
// catalog carried them (#1722): a recorded Cougar is a predator of body size
// 1 that turns on a hit half the time.
var recordedRaces = map[string]*d.RaceProperties{
	"Cougar": {Predator: true, BaseBodySize: 1, ManhunterOnDamageChance: 0.5},
}
