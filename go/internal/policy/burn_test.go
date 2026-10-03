package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

var burnRoom = Rectangle{X: 10, Z: 10, Width: 3, Height: 3}

func burnPawn(id domain.PawnID, at domain.Cell) BurnCandidate {
	no, yes := domain.Known(false), domain.Known(true)
	return BurnCandidate{
		Arm:  EquipCandidatePawn{Pawn: id, Dead: no, Downed: no, Drafted: no, MentalState: no, IncapableOfViolence: no, Position: at},
		Fire: FireSafetyPawnFacts{Pawn: id, Dead: no, Downed: no, Drafted: no, MentalState: no, NeedsTend: no, Bleeding: no, FirefightingEnabled: yes},
	}
}

func burnReq() BurnRequest {
	far := domain.Cell{X: 40, Z: 40}
	return BurnRequest{Interior: burnRoom, Stored: BurnStoredCells, Pawns: []BurnCandidate{burnPawn("b", far), burnPawn("a", far)},
		Molotovs: []EquipCandidateWeapon{{Thing: "m2", Definition: MolotovDef}, {Thing: "m1", Definition: MolotovDef}, {Thing: "x", Definition: "Gun"}}}
}

// A burn is a batch, not an item: below the threshold nothing is lit.
func TestPlanBurnWaitsForTheBatch(t *testing.T) {
	r := burnReq()
	r.Stored = BurnStoredCells - 1
	if _, v := PlanBurn(r); v != BurnBelowBatch {
		t.Fatal(v)
	}
	r.Stored = BurnStoredCells
	order, v := PlanBurn(r)
	if v != BurnReady || order.Target != (domain.Cell{X: 11, Z: 11}) {
		t.Fatal(order, v)
	}
}

// The lowest-ID able colonist burns; a holder goes first and needs no pickup.
func TestPlanBurnPicksTheBurner(t *testing.T) {
	order, _ := PlanBurn(burnReq())
	if order.Burner != "a" || order.Molotov == nil || order.Molotov.Thing != "m1" {
		t.Fatal(order)
	}
	r := burnReq()
	r.Pawns[0].Holds = true
	order, _ = PlanBurn(r)
	if order.Burner != "b" || order.Molotov != nil {
		t.Fatal(order)
	}
	r = burnReq()
	r.Pawns[1].Arm.IncapableOfViolence = domain.Known(true)
	if order, _ = PlanBurn(r); order.Burner != "b" {
		t.Fatal("an incapable colonist burned", order)
	}
	r = burnReq()
	r.Molotovs = nil
	if _, v := PlanBurn(r); v != BurnNoMolotov {
		t.Fatal(v)
	}
}

// Nothing burns without another colonist standing by to fight fire, while a
// fire burns, or with a colonist in the room.
func TestPlanBurnGates(t *testing.T) {
	r := burnReq()
	r.Pawns[1].Fire.FirefightingEnabled = domain.Known(false)
	r.Pawns[0].Fire.FirefightingEnabled = domain.Known(false)
	if _, v := PlanBurn(r); v != BurnNoStandby {
		t.Fatal(v)
	}
	r = burnReq()
	r.Pawns = r.Pawns[:1]
	if _, v := PlanBurn(r); v != BurnNoStandby {
		t.Fatal("a lone burner is its own standby", v)
	}
	r = burnReq()
	r.Fire = true
	if _, v := PlanBurn(r); v != BurnFireActive {
		t.Fatal(v)
	}
	r = burnReq()
	r.Pawns[0].Arm.Position = domain.Cell{X: 11, Z: 12}
	if _, v := PlanBurn(r); v != BurnOccupied {
		t.Fatal(v)
	}
	r = burnReq()
	r.Pawns = nil
	if _, v := PlanBurn(r); v != BurnNoBurner {
		t.Fatal(v)
	}
}

func TestIncineratorAshIsTheInteriorsOnly(t *testing.T) {
	filth := []UpkeepFilth{{ID: "z", Definition: AshDef, Cell: domain.Cell{X: 11, Z: 10}}, {ID: "a", Definition: AshDef, Cell: domain.Cell{X: 12, Z: 12}},
		{ID: "out", Definition: AshDef, Cell: domain.Cell{X: 13, Z: 10}}, {ID: "mud", Definition: "Filth_Dirt", Cell: domain.Cell{X: 11, Z: 11}}}
	got := IncineratorAsh(filth, burnRoom)
	if len(got) != 2 || got[0].ID != "a" || got[1].ID != "z" {
		t.Fatal(got)
	}
}
