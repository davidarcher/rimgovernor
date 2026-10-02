// Package combatlab holds the standard combat fixtures (#854, epic #845):
// fixed layouts on the blank lab (cases.Lab, #729) that every combat
// tactic case and recording stages instead of playing a colony into a
// raid. A fixture is plain data built relative to the lab's centre cell;
// Stage wipes the lab to the fixture's colonist count and applies it in
// one test/lab_stage call. Hostiles are spawned at fixed cells with fixed
// weapons, skills and no apparel, under an assault lord, never through the
// storyteller.
package combatlab

import (
	"fmt"
	"slices"
)

// StageTool stages a fixture spec on a wiped lab
// (scripts/fixtures/DebugStartFixture.cs, carried by every fixture build).
const StageTool = "test/lab_stage"

// Thing is one structure or item: a Def (ThingDef) with optional Stuff.
type Thing struct {
	Def   string `json:"def"`
	Stuff string `json:"stuff,omitempty"`
	X     int    `json:"x"`
	Z     int    `json:"z"`
	// Hostile stages a building under the lab hostiles' faction (#930).
	Hostile bool `json:"hostile,omitempty"`
	// HitPoints, when set, stages the building damaged (#1150).
	HitPoints int `json:"hitPoints,omitempty"`
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
	// Hediffs are extra HediffDef names added at staging, e.g. a drug
	// high or addiction (#1056).
	Hediffs []string `json:"hediffs,omitempty"`
	// Apparel is one worn apparel def, staged charged if a shield (#1049).
	Apparel string `json:"apparel,omitempty"`
	// Trained names the TrainableDefs an Animal has learned (#1057).
	Trained []string `json:"trained,omitempty"`
	// Injured stages the pawn with a blunt bruise (#1080).
	Injured bool `json:"injured,omitempty"`
	// Inventory are ThingDef names put one each in the pawn's inventory,
	// e.g. a carried go-juice (#1311).
	Inventory []string `json:"inventory,omitempty"`
	// StunTicks stuns the pawn directly for that many ticks (#1118).
	StunTicks int `json:"stunTicks,omitempty"`
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
	// Roof, when set, roofs a rectangle (#1071: overhead mountain).
	Roof *Roof `json:"roof,omitempty"`
	// Siege, when set, puts the hostiles under a real siege lord
	// (LordJob_Siege) camped at its cell instead of an assault lord
	// (#1147): the game places the camp's blueprints and drops its supplies.
	Siege *Siege `json:"siege,omitempty"`
	// PrisonBreak starts a prison break by the first prisoner once staged
	// (#1080).
	PrisonBreak bool `json:"prisonBreak,omitempty"`
	// Sappers stages the hostiles' assault lord as a sapper raid: it digs
	// a path through the walls to the colony (#1149).
	Sappers bool `json:"sappers,omitempty"`
	// Layout, when set, is the complete defense layout record the metrics
	// run stores before serving (#890), so the planner holds this
	// fixture's line instead of refusing the hold for want of one.
	Layout *Layout `json:"-"`
}

// Layout is a fixture's hand-written defense layout: the firing line and
// its fall-back cells, the direction from the enemy toward home, and the
// choke the blockers hold, if any. The planner's own layout needs a
// colony's killbox plan and the build of its tiers, minutes of game time
// on the blank lab; the fixture states the geometry it already staged.
type Layout struct {
	Firing, Retreat []Cell
	// Toward is a domain.Rotation name ("south": the enemy is north).
	Toward string
	Choke  *Cell
}

// Roof is a RoofDef over the cells MinX..MaxX, MinZ..MaxZ inclusive.
type Roof struct {
	Def  string `json:"def"`
	MinX int    `json:"minX"`
	MinZ int    `json:"minZ"`
	MaxX int    `json:"maxX"`
	MaxZ int    `json:"maxZ"`
}

// Siege is a siege lord's camp spot and blueprint points (#1147).
type Siege struct {
	X      int     `json:"x"`
	Z      int     `json:"z"`
	Points float64 `json:"points"`
}

// Cell is one map cell.
type Cell struct{ X, Z int }

const (
	Colonist = "colonist"
	Hostile  = "hostile"
	// Animal is a generated player animal of Kind; Manhunter a wild Kind
	// gone permanently manhunter (#1057).
	Animal    = "animal"
	Manhunter = "manhunter"
	// Wild is a calm factionless animal of Kind (#1116).
	Wild = "wild"
	// Prisoner is a generated hostile Kind held as the colony's prisoner.
	Prisoner = "prisoner"
	// Insect is an insect Kind of the insects' faction under an assault
	// lord (#1071).
	Insect = "insect"
	// Mech is a mechanoid Kind of the mechanoids' faction under an assault
	// lord (#1118).
	Mech = "mech"

	rifle     = "Gun_AssaultRifle"
	longsword = "MeleeWeapon_LongSword"
	club      = "MeleeWeapon_Club"
	gunner    = "Mercenary_Gunner"
	slasher   = "Mercenary_Slasher"
)

// Names are the fixtures in landing order; #854 lands the first three,
// lab-pods (#870) joins with its rooms (#897), lab-breach with its sapper
// raid (#1149), lab-siege with its siege lord (#1154), lab-manhunter and
// lab-mech with #1146, lab-ranged-shield with #1153. lab-infestation (#1071) builds but has no metrics
// baseline yet.
var Names = []string{"lab-open", "lab-choke", "lab-ranged", "lab-pods", "lab-breach", "lab-siege", "lab-manhunter", "lab-mech", "lab-ranged-shield"}

// Build returns the named fixture around the lab centre (cx, cz).
func Build(name string, cx, cz int) (Fixture, error) {
	switch name {
	case "lab-open":
		return open(cx, cz), nil
	case "lab-choke":
		return choke(cx, cz), nil
	case "lab-ranged":
		return ranged(cx, cz), nil
	case "lab-ranged-shield":
		return rangedShield(cx, cz), nil
	case "lab-pods":
		return pods(cx, cz), nil
	case "lab-siege":
		return siege(cx, cz), nil
	case "lab-manhunter":
		return manhunter(cx, cz), nil
	case "lab-prison":
		return prison(cx, cz), nil
	case "lab-infestation":
		return infestation(cx, cz), nil
	case "lab-mech":
		return mech(cx, cz), nil
	case "lab-mech-line":
		return mechLine(cx, cz), nil
	case "lab-breach":
		return breach(cx, cz), nil
	}
	return Fixture{}, fmt.Errorf("combatlab: no fixture %q", name)
}

// mech (#1118): one rifleman and a scyther 25 cells north, stunned for
// mechStunTicks at staging.
func mech(cx, cz int) Fixture {
	return Fixture{Name: "lab-mech", Colonists: 1, Pawns: []Pawn{
		{Side: Colonist, Index: 0, X: cx, Z: cz - 12, Weapon: rifle},
		{Side: Mech, Kind: "Mech_Scyther", X: cx, Z: cz + 13, StunTicks: mechStunTicks},
	}}
}

// mechLine (#1184): lab-ranged's four riflemen and held line against two
// lancers 25 cells north, a mech fight the squad takes on (lab-mech is the
// shelter case), so the first attacks' target (#863) is checked on mechs.
func mechLine(cx, cz int) Fixture {
	f := ranged(cx, cz)
	f.Name = "lab-mech-line"
	f.Pawns = slices.DeleteFunc(f.Pawns, func(p Pawn) bool { return p.Side == Hostile })
	for _, dx := range []int{-2, 2} {
		f.Pawns = append(f.Pawns, Pawn{Side: Mech, Kind: "Mech_Lancer", X: cx + dx, Z: cz + 16})
	}
	return f
}

// Breach room (#1149): an 11x11 granite wall ring with no door around the
// centre; the sappers wait breachRaid cells north of it.
const (
	breachHalf = 5
	breachRaid = 15
)

// breach (#1149): three riflemen sealed in a doorless walled room and three
// club slashers (a sapper-capable kind, mining 8) north of it under a
// sapper assault lord: with no path in they mine through the north wall.
// The staging is seeded, so the raid is the same every run.
func breach(cx, cz int) Fixture {
	f := Fixture{Name: "lab-breach", Colonists: 3, Sappers: true}
	for x := cx - breachHalf; x <= cx+breachHalf; x++ {
		for z := cz - breachHalf; z <= cz+breachHalf; z++ {
			if x == cx-breachHalf || x == cx+breachHalf || z == cz-breachHalf || z == cz+breachHalf {
				f.Things = append(f.Things, Thing{Def: "Wall", Stuff: "BlocksGranite", X: x, Z: z})
			}
		}
	}
	for i, dx := range []int{-2, 0, 2} {
		f.Pawns = append(f.Pawns, Pawn{Side: Colonist, Index: i, X: cx + dx, Z: cz - 2, Weapon: rifle})
		f.Pawns = append(f.Pawns, Pawn{Side: Hostile, Kind: slasher, X: cx + dx, Z: cz + breachHalf + breachRaid, Weapon: club, WeaponStuff: "WoodLog"})
	}
	return f
}

// mechStunTicks is lab-mech's staged stun.
const mechStunTicks = 600

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
	// The riflemen's line four cells behind the gap, both sides of the
	// lane; the brawlers block the gap itself.
	gap := Cell{cx, cz + chokeHalf}
	f.Layout = &Layout{Toward: "south", Choke: &gap}
	for _, dx := range []int{-2, 2, -4, 4} {
		f.Layout.Firing = append(f.Layout.Firing, Cell{cx + dx, cz + chokeHalf - 4})
		f.Layout.Retreat = append(f.Layout.Retreat, Cell{cx + dx, cz - chokeHalf + 2})
	}
	return f
}

// Pod rooms (#897): the landing room is a 13x13 granite ring around the
// drop centre, 10 cells north, with a door in the middle of its south
// wall; the safe room a 7x7 ring 13 cells south with a door in its north
// wall.
const (
	podsDrop     = 10
	podsHalf     = 6
	podsSafe     = -13
	podsSafeHalf = 3
)

// pods: four riflemen on an open field between two walled rooms, an
// unarmed colonist inside the landing room, and four rifle raiders dropped
// by center drop pods around the landing room's centre (#870, #897). For
// the drop-pod tactics (draft before the open tick, evacuate, doorway
// pairs, wait and strike) and the pods strategy in combat_events.
func pods(cx, cz int) Fixture {
	f := Fixture{Name: "lab-pods", Colonists: 5, Arrival: "CenterDrop"}
	f.Things = append(f.Things, walledRoom(cx, cz+podsDrop, podsHalf, Cell{cx, cz + podsDrop - podsHalf})...)
	f.Things = append(f.Things, walledRoom(cx, cz+podsSafe, podsSafeHalf, Cell{cx, cz + podsSafe + podsSafeHalf})...)
	for i, dx := range []int{-3, -1, 1, 3} {
		f.Pawns = append(f.Pawns, Pawn{Side: Colonist, Index: i, X: cx + dx, Z: cz - 3, Weapon: rifle})
	}
	f.Pawns = append(f.Pawns, Pawn{Side: Colonist, Index: 4, X: cx + 4, Z: cz + podsDrop - 4})
	for i := 0; i < 4; i++ {
		f.Pawns = append(f.Pawns, Pawn{Side: Hostile, Kind: gunner, X: cx, Z: cz + podsDrop, Weapon: rifle})
	}
	return f
}

// Siege offsets (#1051): our mortar 15 cells south of the centre, the
// siege camp spot 20 north, 35 apart (past the vanilla mortar's 29.9-cell
// minimum range).
const (
	siegeOurMortar  = -15
	siegeCampMortar = 20
	// siegePoints is the camp's blueprint points, a mid-size vanilla
	// siege (RaidStrategyWorker_Siege takes 20-30% of the raid's points).
	siegePoints = 500
)

// siege: three riflemen by an unroofed player mortar with one HE and one
// EMP shell beside it, and four raiders under a real siege lord at the camp
// spot 20 cells north (#1147): the game places the camp's blueprints,
// drops its supplies, and the raiders build the frames. For the
// counter-battery shell op (#1051) and the siege tactics.
func siege(cx, cz int) Fixture {
	f := Fixture{Name: "lab-siege", Colonists: 3}
	f.Things = append(f.Things,
		Thing{Def: "Turret_Mortar", X: cx, Z: cz + siegeOurMortar},
		Thing{Def: "Shell_HighExplosive", X: cx + 2, Z: cz + siegeOurMortar - 1},
		Thing{Def: "Shell_EMP", X: cx - 2, Z: cz + siegeOurMortar - 1})
	f.Siege = &Siege{X: cx, Z: cz + siegeCampMortar, Points: siegePoints}
	for i, dx := range []int{-2, 0, 2} {
		f.Pawns = append(f.Pawns, Pawn{Side: Colonist, Index: i, X: cx + dx, Z: cz + siegeOurMortar + 3, Weapon: rifle})
	}
	for _, dx := range []int{-3, -1, 1, 3} {
		f.Pawns = append(f.Pawns, Pawn{Side: Hostile, Kind: gunner, X: cx + dx, Z: cz + siegeCampMortar, Weapon: rifle})
	}
	return f
}

// walledRoom is a granite wall ring of half edge half around (x, z) with
// a wooden door at door.
func walledRoom(x, z, half int, door Cell) []Thing {
	var out []Thing
	for wx := x - half; wx <= x+half; wx++ {
		for wz := z - half; wz <= z+half; wz++ {
			switch {
			case wx == door.X && wz == door.Z:
				out = append(out, Thing{Def: "Door", Stuff: "WoodLog", X: wx, Z: wz})
			case wx == x-half || wx == x+half || wz == z-half || wz == z+half:
				out = append(out, Thing{Def: "Wall", Stuff: "BlocksGranite", X: wx, Z: wz})
			}
		}
	}
	return out
}

// ranged: four riflemen, one empty cell apart, behind a seven-cell sandbag
// line; four rifle raiders 25 cells north. For spacing, friendly fire,
// cover angle, focus fire and the combat.geometry read.
func ranged(cx, cz int) Fixture {
	f := Fixture{Name: "lab-ranged", Colonists: 4}
	for dx := -3; dx <= 3; dx++ {
		f.Things = append(f.Things, Thing{Def: "Sandbags", X: cx + dx, Z: cz - 8})
	}
	f.Layout = &Layout{Toward: "south"}
	for i, dx := range []int{-3, -1, 1, 3} {
		f.Pawns = append(f.Pawns, Pawn{Side: Colonist, Index: i, X: cx + dx, Z: cz - 9, Weapon: rifle})
		f.Pawns = append(f.Pawns, Pawn{Side: Hostile, Kind: gunner, X: cx + dx, Z: cz + 16, Weapon: rifle})
		// The line is the cells behind the sandbags, its fall-back five
		// cells further south.
		f.Layout.Firing = append(f.Layout.Firing, Cell{cx + dx, cz - 9})
		f.Layout.Retreat = append(f.Layout.Retreat, Cell{cx + dx, cz - 14})
	}
	return f
}

// rangedShield (#866, #1153): lab-ranged plus a fifth colonist, a
// longsword fighter in a charged shield belt, two cells behind the line's
// middle. The four riflemen are unchanged (a belted pawn cannot fire).
// The belt makes it the fight's tank: its formation cell lies ahead of a
// gunner, toward the raiders; the sandbags fill the gunners' front cells,
// so it is the nearest standable cell ahead.
func rangedShield(cx, cz int) Fixture {
	f := ranged(cx, cz)
	f.Name = "lab-ranged-shield"
	f.Colonists = 5
	f.Pawns = append(f.Pawns, Pawn{Side: Colonist, Index: 4, X: cx, Z: cz - 11, Weapon: longsword, Apparel: "Apparel_ShieldBelt"})
	return f
}

// manhunter (#1057): one colonist, a release-trained husky beside her and
// an untrained one, two permanent-manhunter wargs 15 cells north.
func manhunter(cx, cz int) Fixture {
	return Fixture{Name: "lab-manhunter", Colonists: 1, Pawns: []Pawn{
		{Side: Colonist, Index: 0, X: cx, Z: cz},
		{Side: Animal, Kind: "Husky", X: cx + 1, Z: cz, Trained: []string{"Tameness", "Obedience", "Release"}},
		{Side: Animal, Kind: "Husky", X: cx - 1, Z: cz},
		{Side: Manhunter, Kind: "Warg", X: cx - 2, Z: cz + 15},
		{Side: Manhunter, Kind: "Warg", X: cx + 2, Z: cz + 15},
	}}
}

// Infestation room (#1071): an 11x11 granite ring (natural rock) 10 cells
// north of the centre, roofed thick (overhead mountain) wall to wall, with
// one opening, the tunnel, in the middle of its south wall.
const (
	infestRoom = 10
	infestHalf = 5
)

// infestation (#1071): a hive in a mountain room with three insects around
// it; two longsword brawlers south of the tunnel, two riflemen behind them
// and a frag grenadier in line with the tunnel and in throw range of the
// hive. The line holds the tunnel, the brawlers block it.
func infestation(cx, cz int) Fixture {
	rz := cz + infestRoom
	f := Fixture{Name: "lab-infestation", Colonists: 5,
		Roof: &Roof{Def: "RoofRockThick", MinX: cx - infestHalf, MinZ: rz - infestHalf, MaxX: cx + infestHalf, MaxZ: rz + infestHalf}}
	for x := cx - infestHalf; x <= cx+infestHalf; x++ {
		for z := rz - infestHalf; z <= rz+infestHalf; z++ {
			edge := x == cx-infestHalf || x == cx+infestHalf || z == rz-infestHalf || z == rz+infestHalf
			if edge && !(x == cx && z == rz-infestHalf) {
				f.Things = append(f.Things, Thing{Def: "Granite", X: x, Z: z})
			}
		}
	}
	f.Things = append(f.Things, Thing{Def: "Hive", X: cx, Z: rz + 2})
	f.Pawns = []Pawn{
		{Side: Insect, Kind: "Megascarab", X: cx - 2, Z: rz + 1},
		{Side: Insect, Kind: "Spelopede", X: cx + 2, Z: rz + 1},
		{Side: Insect, Kind: "Megaspider", X: cx, Z: rz},
		{Side: Colonist, Index: 0, X: cx - 1, Z: cz + 3, Weapon: longsword, WeaponStuff: "Steel"},
		{Side: Colonist, Index: 1, X: cx + 1, Z: cz + 3, Weapon: longsword, WeaponStuff: "Steel"},
		{Side: Colonist, Index: 2, X: cx - 3, Z: cz, Weapon: rifle},
		{Side: Colonist, Index: 3, X: cx + 3, Z: cz, Weapon: rifle},
		{Side: Colonist, Index: 4, X: cx, Z: cz + 1, Weapon: "Weapon_GrenadeFrag"},
	}
	tunnel := Cell{cx, rz - infestHalf}
	f.Layout = &Layout{Toward: "south", Choke: &tunnel}
	for _, dx := range []int{-3, 3} {
		f.Layout.Firing = append(f.Layout.Firing, Cell{cx + dx, cz})
		f.Layout.Retreat = append(f.Layout.Retreat, Cell{cx + dx, cz - 5})
	}
	return f
}

// prison (#1080): two prisoners, one bruised, break out of a 7x7 granite
// cell 8 cells north with a door in its south wall; four wardens wait
// south of it: two unarmed, one with a mace, one with a revolver.
func prison(cx, cz int) Fixture {
	f := Fixture{Name: "lab-prison", Colonists: 4, PrisonBreak: true}
	f.Things = walledRoom(cx, cz+8, 3, Cell{cx, cz + 5})
	f.Pawns = []Pawn{
		{Side: Prisoner, Kind: slasher, X: cx - 1, Z: cz + 9},
		{Side: Prisoner, Kind: slasher, X: cx + 1, Z: cz + 9, Injured: true},
		{Side: Colonist, Index: 0, X: cx - 3, Z: cz},
		{Side: Colonist, Index: 1, X: cx - 1, Z: cz},
		{Side: Colonist, Index: 2, X: cx + 1, Z: cz, Weapon: "MeleeWeapon_Mace", WeaponStuff: "Steel"},
		{Side: Colonist, Index: 3, X: cx + 3, Z: cz - 2, Weapon: "Gun_Revolver"},
	}
	return f
}
