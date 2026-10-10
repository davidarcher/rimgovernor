package buildingruntime

import (
	"encoding/json"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/bridge/recordedrows"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// recordedWeapons adapts recordings made before the weapon facts left the
// gear rows. Those carried each gear item's class and range inline;
// a live frame names the item's def in the things table and the catalog's row
// says the rest. upgrade strips the legacy keys from a recorded pawn reply
// and keeps, per gear item, a def row standing in for what the item said:
// a gun of that range, or a club.
type recordedWeapons struct {
	defs   map[string]string  // gear thing id to def name
	ranges map[string]float32 // gun def name to its range
}

const recordedMelee = "MeleeWeapon_Club"

// upgrade is raw, a recorded ListPawnsReply, without the legacy gear keys.
func (r *recordedWeapons) upgrade(raw []byte) ([]byte, error) {
	var v any
	if err := json.Unmarshal(raw, &v); err != nil {
		return nil, err
	}
	if r.defs == nil {
		r.defs, r.ranges = map[string]string{}, map[string]float32{}
	}
	r.walk(v)
	return json.Marshal(v)
}

func (r *recordedWeapons) walk(v any) {
	switch v := v.(type) {
	case []any:
		for _, item := range v {
			r.walk(item)
		}
	case map[string]any:
		for _, child := range v {
			r.walk(child)
		}
		thing, ok := v["thing"].(map[string]any)
		if !ok {
			return
		}
		if weapon, _ := v["weapon"].(bool); weapon {
			id, _ := thing["id"].(string)
			if ranged, _ := v["ranged"].(bool); ranged {
				reach, _ := v["range"].(float64)
				def := fmt.Sprintf("Recorded_Gun_%v", reach)
				r.defs[id], r.ranges[def] = def, float32(reach)
			} else {
				r.defs[id] = recordedMelee
			}
		}
		for _, key := range []string{"weapon", "ranged", "melee", "range"} {
			delete(v, key)
		}
	}
}

// editRecordedPawns applies edit to each pawn row of a recorded
// ListPawnsReply in its recorded (JSON) shape, which still carries the
// legacy gear keys.
func editRecordedPawns(raw []byte, edit func(pawn map[string]any)) ([]byte, error) {
	var v map[string]any
	if err := json.Unmarshal(raw, &v); err != nil {
		return nil, err
	}
	observed, _ := v["observed"].(map[string]any)
	pawns, _ := observed["pawns"].([]any)
	for _, p := range pawns {
		pawn, _ := p.(map[string]any)
		edit(pawn)
	}
	return json.Marshal(v)
}

// recordedMeleeGear turns a pawn row's recorded gear into melee gear.
func recordedMeleeGear(pawn map[string]any) {
	equipment, _ := pawn["equipment"].(map[string]any)
	items, _ := equipment["equipped"].([]any)
	for _, i := range items {
		item, _ := i.(map[string]any)
		item["ranged"], item["melee"] = false, true
		delete(item, "range")
	}
}

// things is the frame's things table for the recorded gear.
func (r *recordedWeapons) things() bridge.Things {
	var out bridge.Things
	for id, def := range r.defs {
		out = out.With(id, &o.Thing{Thing: &o.EntityRef{Id: proto.String(id), DefName: proto.String(def)}})
	}
	return out
}

// guns are the gun defs the recorded gear names, by their range.
func (r *recordedWeapons) guns() map[string]float32 { return r.ranges }

// weaponRows are the base rows plus a plain bolt-action rifle of the asked
// range for every gun def the recorded gear names.
func weaponRows(guns map[string]float32) *recordedrows.Slice {
	rows := newCatalogRows()
	for def, reach := range guns {
		rows.CopyThing("Gun_BoltActionRifle", def).Verbs[0].Value.Range = reach
	}
	return rows
}
