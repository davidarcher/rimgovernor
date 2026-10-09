package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func doseView(hostiles int, hostile CombatPawnState) (CombatView, map[domain.PawnID]bool, map[domain.PawnID]CombatPawnState) {
	defender := CombatPawnState{ID: "d1", Cell: domain.Known(domain.Cell{X: 0, Z: 0}), WeaponRange: 25, CarriedDrugs: domain.Known([]string{"GoJuice"})}
	view := CombatView{
		Drugs:     []string{"GoJuice"},
		Pawns:     []CombatPawnState{defender},
		Defenders: []SquadDefenderFacts{squadDefender("d1", true)},
		Orderable: []domain.PawnID{"d1"},
	}
	for i := 0; i < hostiles; i++ {
		h := hostile
		h.ID = domain.PawnID("h" + string(rune('1'+i)))
		view.Pawns = append(view.Pawns, h)
		view.Threats = append(view.Threats, SquadThreatFacts{ID: PawnID(h.ID), BodySize: domain.Known(1.0)})
	}
	state := map[domain.PawnID]CombatPawnState{}
	for _, p := range view.Pawns {
		state[p.ID] = p
	}
	return view, map[domain.PawnID]bool{"d1": true}, state
}

func TestDoseOrders(t *testing.T) {
	near := CombatPawnState{Cell: domain.Known(domain.Cell{X: 30, Z: 0})}
	far := CombatPawnState{Cell: domain.Known(domain.Cell{X: 60, Z: 0})}
	cases := []struct {
		name     string
		hostiles int
		hostile  CombatPawnState
		want     bool
	}{
		{"lone rat is fought sober", 1, near, false},
		{"outmatched squad doses in reach", 2, near, true},
		{"outmatched squad waits out of reach", 2, far, false},
		{"one mech is dangerous", 1, CombatPawnState{Cell: near.Cell, Kind: "Mech_Lancer", Mech: true}, true},
		{"one go-juiced raider is dangerous", 1, CombatPawnState{Cell: near.Cell, GoJuice: true}, true},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			view, orderable, state := doseView(c.hostiles, c.hostile)
			if c.hostile.Kind == "Mech_Lancer" {
				view.Threats[0].Mech = true
			}
			var m CombatMemory
			got := doseOrders(view, &m, nil, orderable, state)
			if (len(got) == 1) != c.want {
				t.Fatalf("orders %v, want dose %v", got, c.want)
			}
			if c.want && (got[0] != CombatOrder{Pawn: "d1", Kind: OrderDrug, Drug: "GoJuice", Reason: ReasonDrug}) {
				t.Fatalf("order %+v", got[0])
			}
			if again := doseOrders(view, &m, nil, orderable, state); len(again) != 0 {
				t.Fatalf("dosed twice: %v", again)
			}
		})
	}
}

func TestDoseOrdersSkipsHighAndDowned(t *testing.T) {
	near := CombatPawnState{Cell: domain.Known(domain.Cell{X: 5, Z: 0})}
	for _, mod := range []func(*CombatPawnState){
		func(s *CombatPawnState) { s.GoJuice = true },
		func(s *CombatPawnState) { s.Downed = true },
	} {
		view, orderable, state := doseView(2, near)
		d := state["d1"]
		mod(&d)
		state["d1"] = d
		var m CombatMemory
		if got := doseOrders(view, &m, nil, orderable, state); len(got) != 0 {
			t.Fatalf("dosed %+v: %v", d, got)
		}
	}
}

func TestDoseOrdersRequireCarriedDrug(t *testing.T) {
	for _, tc := range []struct {
		name    string
		carried domain.Fact[[]string]
		want    string
	}{
		{"unknown inventory", domain.Unknown[[]string](), ""},
		{"empty inventory", domain.Known([]string{}), ""},
		{"noncombat drug", domain.Known([]string{"Beer"}), ""},
		{"different carried combat drug", domain.Known([]string{"GoJuice"}), "GoJuice"},
		{"catalog preference among carried drugs", domain.Known([]string{"GoJuice", "Yayo"}), "Yayo"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			view, orderable, state := doseView(2, CombatPawnState{Cell: domain.Known(domain.Cell{X: 5, Z: 0})})
			view.Drugs = []string{"Yayo", "GoJuice"}
			d := state["d1"]
			d.CarriedDrugs = tc.carried
			state["d1"] = d
			var memory CombatMemory
			got := doseOrders(view, &memory, nil, orderable, state)
			if tc.want == "" {
				if len(got) != 0 || len(memory.Dosed) != 0 {
					t.Fatalf("unavailable drug produced orders %v or attempt marks %v", got, memory.Dosed)
				}
				// A later observation of a carried drug may still authorize a dose.
				d.CarriedDrugs = domain.Known([]string{"GoJuice"})
				state["d1"] = d
				got = doseOrders(view, &memory, nil, orderable, state)
				if len(got) != 1 || got[0].Drug != "GoJuice" {
					t.Fatalf("later available dose = %v", got)
				}
			} else if len(got) != 1 || got[0].Drug != tc.want {
				t.Fatalf("dose = %v, want %s", got, tc.want)
			}
		})
	}
}

// Only a carrier is dosed, and CarryEntries plans no carried dose for a child
// or a pawn addicted to, withdrawing from or highly tolerant of the chemical,
// so the native order needs no child, tolerance or addiction veto of its own.
func TestCarryEntriesExcludeChildAndRiskyPawns(t *testing.T) {
	drug, ok := CarryDrug(CoreItemFacts(), nil)
	if !ok {
		t.Fatal("no combat drug")
	}
	carries := func(pawn WorkPawn) bool {
		entries, known := CarryEntries(pawn, CoreItemFacts(), drug)
		if !known {
			t.Fatal("unknown")
		}
		return len(entries) == 1 && entries[0].TakeToInventory == 1
	}
	if !carries(drugPawn("adult", 30, nil)) {
		t.Fatal("plain adult does not carry")
	}
	if carries(drugPawn("child", 9, nil)) {
		t.Fatal("child carries")
	}
	for name, c := range map[string]ChemicalState{
		"tolerant":   {Chemical: drug.Chemical, Tolerance: domain.Known(HighTolerance)},
		"addicted":   {Chemical: drug.Chemical, Addiction: domain.Known(0.3)},
		"withdrawal": {Chemical: drug.Chemical, Withdrawal: true},
	} {
		if carries(drugPawn(name, 30, nil, c)) {
			t.Fatalf("%s carries", name)
		}
	}
}
