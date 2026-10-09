package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func TestCombatBatchObservationDoesNotInventHistory(t *testing.T) {
	v := CombatView{Pawns: []CombatPawnState{{ID: "a", Cell: domain.Known(domain.Cell{X: 1, Z: 2}), Target: "enemy", GoJuice: true}}, Orderable: []domain.PawnID{"a"}}
	for _, kind := range []string{"draft", "move", "attack", "drug"} {
		o := domain.CombatCommand{Kind: kind, Pawn: "a"}
		if kind == "move" {
			o.Cell = domain.Cell{X: 1, Z: 2}
		}
		if kind == "attack" {
			o.Target = "enemy"
		}
		if kind == "drug" {
			o.Drug = "GoJuice"
		}
		b, err := domain.NewCombatBatch("fight", "key", []domain.CombatCommand{o})
		if err != nil {
			t.Fatal(err)
		}
		if got := CombatBatchObserved(b, v); got != (kind == "draft" || kind == "move") {
			t.Fatal(kind, got)
		}
	}
}
