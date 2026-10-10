package buildingruntime

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/bridge/recordedrows"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// decodeCombatWithCatalog is bridge.DecodeCombat with a catalog standing in
// for the load's: the recorded Core weapons the planning tests name, the
// guns the recorded gear named by range, a plain rifle for every other weapon
// def a combat pawn holds, and the recorded race of each animal and mechanoid
// in the frame.
func decodeCombatWithCatalog(frame *o.BundleSnapshot, guns ...map[string]float32) (bridge.Combat, error) {
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
	named := map[string]float32{}
	for _, m := range guns {
		for def, reach := range m {
			named[def] = reach
		}
	}
	rows := weaponRows(named)
	for _, pawn := range combat.Pawns {
		name := pawn.GetWeapon()
		if name == "" || rows.Has(name) {
			continue
		}
		// The recordings no longer carry the weapon's class or range,
		// so an unnamed recorded weapon is a plain 25-cell rifle.
		rows.CopyThing("Gun_BoltActionRifle", name).Verbs[0].Value.Range = 25
	}
	// The race rows a live read would carry: the recordings hold pawn
	// rows, not the catalog, so each animal and mechanoid in the frame takes
	// the recording's race row of its def (a mechanoid the game does not have
	// takes the Scyther's).
	// A hostile building's row, which the mortar rule reads.
	for _, threat := range combat.Emergency.Facts.Threats {
		if threat.Kind == policy.HostileBuilding && recordedrows.Recorded(threat.Definition) {
			rows.Add(threat.Definition)
		}
	}
	races := map[string]bool{}
	for row := range combat.Detail.Values() {
		def := row.GetPawn().GetDefName()
		if races[def] || !(row.GetMechanoid() || row.GetAnimal()) {
			continue
		}
		races[def] = true
		switch {
		case recordedrows.Recorded(def):
			rows.Add(def)
		case row.GetMechanoid():
			rows.Add("Mech_Scyther")
			rows.CopyThing("Mech_Scyther", def)
		default:
			return combat, fmt.Errorf("recorded animal %s has no recorded race row", def)
		}
	}
	combat.Catalog, err = decodeRows(rows, "load")
	if err != nil {
		return combat, err
	}
	combat.Shells, err = combat.Catalog.MortarShells(policy.MortarSafeRadius)
	return combat, err
}
