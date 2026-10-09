package policy

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestNativeHoldTargetExclusionTransitions(t *testing.T) {
	for _, mode := range []string{"hold", "AttackStatic", "specialist", "squad"} {
		for _, safe := range []bool{false, true} {
			t.Run(mode+map[bool]string{false: "-stop", true: "-retarget"}[safe], func(t *testing.T) {
				v := holdView()
				job := "Wait_Combat"
				if mode == "AttackStatic" {
					job = mode
				}
				cell := domain.Cell{X: 9, Z: 23}
				v.Pawns[0].Cell, v.Pawns[0].Job = domain.Known(cell), job
				v.Pawns[0].Target, v.Pawns[0].Stance = "boom", StanceWarmup
				v.Pawns[0].WeaponRange, v.Pawns[0].WeaponFacts = 30, WeaponDef{Ranged: true, Range: 30, Reach: 30}
				v.Pawns[0].FireMode = FireAtWill
				v.Threats, v.Positional = nil, nil
				boom, position, pawn := combatAnimal("boom", "Boomrat", domain.Cell{X: 9, Z: 20}, 5)
				v.Threats, v.Positional = append(v.Threats, boom), append(v.Positional, position)
				v.Pawns = append(v.Pawns, pawn)
				if safe {
					threat, pos := combatRaider("safe", domain.Cell{X: 9, Z: 5})
					v.Threats, v.Positional = append(v.Threats, threat), append(v.Positional, pos)
					v.Pawns = append(v.Pawns, CombatPawnState{ID: "safe", Cell: domain.Known(domain.Cell{X: 9, Z: 5})})
				}
				m := CombatMemory{Tactic: TacticHold, Roles: []CombatRole{{Pawn: "a", Ranged: true, Cell: &cell}}}
				if mode == "specialist" {
					v.Pawns[0].WeaponFacts.EMP = true
				}
				if mode == "squad" {
					m.Tactic = TacticSquad
					m.Roles[0].Target = "boom"
				}
				orders, _ := decideStop(t, v, StopEvent{}, m)
				mine := slices.DeleteFunc(orders, func(o CombatOrder) bool { return o.Pawn != "a" })
				if mode != "hold" {
					if len(mine) != 0 {
						t.Fatalf("specialist aim interrupted: %+v", mine)
					}
					return
				}
				kind := OrderStop
				if safe {
					kind = OrderAttack
				}
				if !slices.ContainsFunc(mine, func(o CombatOrder) bool { return o.Kind == kind && (!safe || o.Target == "safe") }) {
					t.Fatalf("safety orders: %+v", mine)
				}
				if !safe {
					if !slices.ContainsFunc(mine, func(o CombatOrder) bool { return o.Kind == OrderFireMode && o.FireMode == HoldFire }) {
						t.Fatalf("autonomous fire still enabled: %+v", mine)
					}
					v.Pawns[0].FireMode, v.Pawns[0].Target, v.Pawns[0].Stance = HoldFire, "", StanceIdle
					orders, m = decideStop(t, v, StopEvent{}, m)
					if slices.ContainsFunc(orders, func(o CombatOrder) bool { return o.Pawn == "a" }) {
						t.Fatalf("held exclusion repeated orders: %+v", orders)
					}
					v.Threats, v.Positional = nil, nil
					v.Pawns = slices.DeleteFunc(v.Pawns, func(p CombatPawnState) bool { return p.ID == "boom" })
					threat, pos := combatRaider("safe", domain.Cell{X: 9, Z: 5})
					v.Threats, v.Positional = append(v.Threats, threat), append(v.Positional, pos)
					v.Pawns = append(v.Pawns, CombatPawnState{ID: "safe", Cell: domain.Known(domain.Cell{X: 9, Z: 5})})
					orders, _ = decideStop(t, v, StopEvent{}, m)
					if !slices.ContainsFunc(orders, func(o CombatOrder) bool { return o.Pawn == "a" && o.Kind == OrderFireMode && o.FireMode == FireAtWill }) {
						t.Fatalf("safe hold did not restore fire: %+v", orders)
					}
				}
			})
		}
	}
}

func TestNativeHoldFireModeSafeguards(t *testing.T) {
	v := holdView()
	cell := domain.Cell{X: 9, Z: 23}
	v.Pawns[0].Cell, v.Pawns[0].Job = domain.Known(cell), "Wait_Combat"
	v.Pawns[0].WeaponFacts = WeaponDef{Ranged: true}
	v.Pawns[0].Target, v.Pawns[0].Stance, v.Pawns[0].FireMode = "r1", StanceWarmup, FireAtWill
	v.Pawns = append(v.Pawns, CombatPawnState{ID: "r1", Target: "blocker", Stance: StanceMelee}, CombatPawnState{ID: "blocker", Target: "r1", Stance: StanceMelee})
	v.Defenders = append(v.Defenders, combatRifleman("blocker"))
	m := CombatMemory{Tactic: TacticHold, Roles: []CombatRole{{Pawn: "a", Ranged: true, Cell: &cell}, {Pawn: "blocker", Duty: DutyBlocker, Target: "r1"}}}
	orders, m := decideStop(t, v, StopEvent{}, m)
	if !slices.ContainsFunc(orders, func(o CombatOrder) bool { return o.Pawn == "a" && o.Kind == OrderFireMode && o.FireMode == HoldFire }) {
		t.Fatalf("unsafe shot not held: %+v", orders)
	}
	v.Pawns[0].FireMode, v.Pawns[0].Stance, v.Pawns[0].Target = HoldFire, StanceIdle, ""
	for i := range v.Pawns {
		if v.Pawns[i].ID == "r1" || v.Pawns[i].ID == "blocker" {
			v.Pawns[i].Stance, v.Pawns[i].Target = StanceIdle, ""
		}
	}
	orders, _ = decideStop(t, v, StopEvent{}, m)
	if !slices.ContainsFunc(orders, func(o CombatOrder) bool { return o.Pawn == "a" && o.Kind == OrderFireMode && o.FireMode == FireAtWill }) {
		t.Fatalf("fire mode not restored: %+v", orders)
	}
}
