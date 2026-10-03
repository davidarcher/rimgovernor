package policy

import (
	"math"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// tribalView is holdView with its two raiders seen as tribal archers
// (great bows, range 30), sniper a at range 45 and b, c at range 30.
func tribalView() CombatView {
	view := holdView()
	for _, t := range view.Positional {
		view.Pawns = append(view.Pawns, CombatPawnState{ID: domain.PawnID(t.ID), Cell: t.Position, Kind: "Tribal_Archer", Weapon: "Bow_Great", WeaponRange: 30})
	}
	for i := range view.Pawns[:3] {
		view.Pawns[i].WeaponRange = 30
	}
	view.Pawns[0].WeaponRange = 45
	return view
}

// {tribal archers at 30, a at 45, b and c at 30} -> a stands off its
// nearest archer at 0.9*45 and shoots it; b and c hold their line cells.
// {one pirate among them} -> no stand-off.
func TestTribalStandoff(t *testing.T) {
	view := tribalView()
	_, m := decideStop(t, view, StopEvent{}, CombatMemory{})
	for _, r := range m.Roles {
		switch r.Pawn {
		case "a":
			if r.Duty != DutyHarasser || r.Target == "" || r.Cell == nil {
				t.Fatalf("a role %+v", r)
			}
			var target domain.Cell
			for _, p := range view.Pawns {
				if p.ID == r.Target {
					target, _ = p.Cell.Value()
				}
			}
			if d := math.Hypot(float64(r.Cell.X-target.X), float64(r.Cell.Z-target.Z)); math.Abs(d-40.5) > 1 {
				t.Fatalf("a at %v, %.1f from %s", *r.Cell, d, r.Target)
			}
		default:
			if r.Duty == DutyHarasser {
				t.Fatalf("outranged gunner harasses: %+v", r)
			}
		}
	}

	mixed := tribalView()
	mixed.Pawns[len(mixed.Pawns)-1].Kind = "Pirate"
	_, m = decideStop(t, mixed, StopEvent{}, CombatMemory{})
	for _, r := range m.Roles {
		if r.Duty == DutyHarasser {
			t.Fatalf("stand-off with a pirate in the raid: %+v", r)
		}
	}
}

// {archer, pila thrower, berserker, grenadier} -> grenadier, berserker,
// pila thrower, archer.
func TestTribalPilaTier(t *testing.T) {
	view := holdView()
	view.Threats, view.Positional = nil, nil
	hostiles := []CombatPawnState{
		{ID: "h1", Kind: "Grenadier_Destructive", Weapon: "Weapon_GrenadeFrag", WeaponFacts: coreWeapons["Weapon_GrenadeFrag"]},
		{ID: "h2", Kind: "Tribal_Berserker", Weapon: "MeleeWeapon_Club"},
		{ID: "h3", Kind: "Tribal_Warrior", Weapon: "Pila"},
		{ID: "h0", Kind: "Tribal_Archer", Weapon: "Bow_Recurve"},
	}
	for i, h := range hostiles {
		cell := domain.Cell{X: 8 + int32(i), Z: 10}
		h.Cell = domain.Known(cell)
		s, d := combatRaider(PawnID(h.ID), cell)
		view.Threats, view.Positional = append(view.Threats, s), append(view.Positional, d)
		view.Pawns = append(view.Pawns, h)
	}
	var got []domain.PawnID
	for _, h := range rankThreats(view) {
		got = append(got, h.ID)
	}
	if want := []domain.PawnID{"h1", "h2", "h3", "h0"}; !reflect.DeepEqual(got, want) {
		t.Fatalf("ranked %v, want %v", got, want)
	}
}
