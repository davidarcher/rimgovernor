// Package combatlab holds the standard combat fixtures (#854, epic #845):
// fixed layouts on the blank lab (cases.Lab, #729) that every combat
// tactic case and recording stages instead of playing a colony into a
// raid. A fixture is plain data built relative to the lab's centre cell;
// Stage wipes the lab to the fixture's colonist count and applies it in
// one test/lab_stage call. Hostiles are spawned at fixed cells with fixed
// weapons, skills and no apparel, under an assault lord, never through the
// storyteller.
package combatlab

import "fmt"

// StageTool stages a fixture spec on a wiped lab
// (scripts/fixtures/DebugStartFixture.cs, carried by every fixture build).
const StageTool = "test/lab_stage"

// Thing is one structure or item: a Def (ThingDef) with optional Stuff.
type Thing struct {
	Def   string `json:"def"`
	Stuff string `json:"stuff,omitempty"`
	X     int    `json:"x"`
	Z     int    `json:"z"`
}

// Pawn is one staged combatant. A colonist is the lab's Index-th colonist
// (by thing id) moved to its cell; a hostile is a generated Kind.
type Pawn struct {
	Side        string `json:"side"`
	Index       int    `json:"index"`
	Kind        string `json:"kind,omitempty"`
	X           int    `json:"x"`
	Z           int    `json:"z"`
	Weapon      string `json:"weapon,omitempty"`
	WeaponStuff string `json:"weaponStuff,omitempty"`
	// Downed stages the pawn downed under anesthetic (#867).
	Downed bool `json:"downed,omitempty"`
}

// Fixture is one staged combat. Colonists is the lab wipe's colonist
// count; every colonist index below it is placed by Pawns.
type Fixture struct {
	Name      string  `json:"-"`
	Colonists int     `json:"-"`
	Things    []Thing `json:"things"`
	Pawns     []Pawn  `json:"pawns"`
	// Arrival, when set, is a PawnsArrivalModeDef that drops the hostiles
	// in around the first hostile's cell instead of spawning each (#870).
	Arrival string `json:"arrival,omitempty"`
}

const (
	Colonist = "colonist"
	Hostile  = "hostile"

	rifle     = "Gun_AssaultRifle"
	longsword = "MeleeWeapon_LongSword"
	club      = "MeleeWeapon_Club"
	gunner    = "Mercenary_Gunner"
	slasher   = "Mercenary_Slasher"
)

// Names are the fixtures in landing order; #854 lands the first three and
// reserves lab-breach, lab-pods, lab-mech, lab-manhunter and lab-siege for
// the first #845 child that needs each. lab-pods (#870) is built but not
// listed until a tactic case fights it (the metrics baselines run Names).
var Names = []string{"lab-open", "lab-choke", "lab-ranged"}

// Build returns the named fixture around the lab centre (cx, cz).
func Build(name string, cx, cz int) (Fixture, error) {
	switch name {
	case "lab-open":
		return open(cx, cz), nil
	case "lab-choke":
		return choke(cx, cz), nil
	case "lab-ranged":
		return ranged(cx, cz), nil
	case "lab-pods":
		return pods(cx, cz), nil
	}
	return Fixture{}, fmt.Errorf("combatlab: no fixture %q", name)
}

// open: three riflemen on an open field behind four stone chunks, three
// club raiders 20 cells north. The smoke fixture for the combat clock,
// orders and recorder.
func open(cx, cz int) Fixture {
	f := Fixture{Name: "lab-open", Colonists: 3}
	for _, dx := range []int{-3, -1, 1, 3} {
		f.Things = append(f.Things, Thing{Def: "ChunkGranite", X: cx + dx, Z: cz - 9})
	}
	for i, dx := range []int{-2, 0, 2} {
		f.Pawns = append(f.Pawns, Pawn{Side: Colonist, Index: i, X: cx + dx, Z: cz - 10, Weapon: rifle})
		f.Pawns = append(f.Pawns, Pawn{Side: Hostile, Kind: slasher, X: cx + dx, Z: cz + 10, Weapon: club, WeaponStuff: "WoodLog"})
	}
	return f
}

// chokeHalf is the room's half edge: a 15x15 granite ring.
const chokeHalf = 7

// choke: a 15x15 granite-walled room whose only opening is one cell in the
// middle of the north wall. Three longsword brawlers stand two cells inside
// the gap, two riflemen further back; six club raiders wait 13 cells north
// of the gap. For melee blocking, peeling and the fallback line.
func choke(cx, cz int) Fixture {
	f := Fixture{Name: "lab-choke", Colonists: 5}
	for x := cx - chokeHalf; x <= cx+chokeHalf; x++ {
		for z := cz - chokeHalf; z <= cz+chokeHalf; z++ {
			edge := x == cx-chokeHalf || x == cx+chokeHalf || z == cz-chokeHalf || z == cz+chokeHalf
			if edge && !(x == cx && z == cz+chokeHalf) {
				f.Things = append(f.Things, Thing{Def: "Wall", Stuff: "BlocksGranite", X: x, Z: z})
			}
		}
	}
	for i, dx := range []int{-1, 0, 1} {
		f.Pawns = append(f.Pawns, Pawn{Side: Colonist, Index: i, X: cx + dx, Z: cz + chokeHalf - 2, Weapon: longsword, WeaponStuff: "Steel"})
	}
	f.Pawns = append(f.Pawns,
		Pawn{Side: Colonist, Index: 3, X: cx - 2, Z: cz, Weapon: rifle},
		Pawn{Side: Colonist, Index: 4, X: cx + 2, Z: cz, Weapon: rifle})
	for dx := -5; dx <= 5; dx += 2 {
		f.Pawns = append(f.Pawns, Pawn{Side: Hostile, Kind: slasher, X: cx + dx, Z: cz + chokeHalf + 13, Weapon: club, WeaponStuff: "WoodLog"})
	}
	return f
}

// pods: three riflemen on an open field and four rifle raiders dropped by
// center drop pods around a cell 10 cells north (#870). For drop-pod tactics and the
// pods strategy in combat_events.
func pods(cx, cz int) Fixture {
	f := Fixture{Name: "lab-pods", Colonists: 3, Arrival: "CenterDrop"}
	for i, dx := range []int{-2, 0, 2} {
		f.Pawns = append(f.Pawns, Pawn{Side: Colonist, Index: i, X: cx + dx, Z: cz - 5, Weapon: rifle})
	}
	for i := 0; i < 4; i++ {
		f.Pawns = append(f.Pawns, Pawn{Side: Hostile, Kind: gunner, X: cx, Z: cz + 10, Weapon: rifle})
	}
	return f
}

// ranged: four riflemen, one empty cell apart, behind a seven-cell sandbag
// line; four rifle raiders 25 cells north. For spacing, friendly fire,
// cover angle, focus fire and the combat.geometry read.
func ranged(cx, cz int) Fixture {
	f := Fixture{Name: "lab-ranged", Colonists: 4}
	for dx := -3; dx <= 3; dx++ {
		f.Things = append(f.Things, Thing{Def: "Sandbags", X: cx + dx, Z: cz - 8})
	}
	for i, dx := range []int{-3, -1, 1, 3} {
		f.Pawns = append(f.Pawns, Pawn{Side: Colonist, Index: i, X: cx + dx, Z: cz - 9, Weapon: rifle})
		f.Pawns = append(f.Pawns, Pawn{Side: Hostile, Kind: gunner, X: cx + dx, Z: cz + 16, Weapon: rifle})
	}
	return f
}
