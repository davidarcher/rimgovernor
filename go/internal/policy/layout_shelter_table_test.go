package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The shelter's research table relocates into the laboratory through the
// TidyLayout review, and the shelter retires once it is gone (#2047).
func TestShelterTableRelocatesToTheLaboratoryThenTheShelterRetires(t *testing.T) {
	f := newShelterRetireFixture(t)
	f.rooms.Shapes = testShapes
	in := f.shelter.Interior
	b, err := domain.NewBuilding("SimpleResearchBench", domain.Cell{X: in.X, Z: in.Z}, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	table := CurrentBuilding{ID: "bench", Building: b, Cells: rectCells(Rectangle{X: in.X, Z: in.Z, Width: 3, Height: 2})}
	built := []CurrentBuilding{table}
	if ShelterRetirable(f.plan, f.rooms, f.sleeping, built) {
		t.Fatal("retirable with the table in the shelter")
	}
	proposal, ok := ShelterTableMove(f.plan, f.rooms, f.sleeping, built, nil)
	if !ok || len(proposal.Moves) != 1 {
		t.Fatalf("no relocation proposed: %+v", proposal)
	}
	move := proposal.Moves[0]
	lab := f.plan.roomsOf(PlannedLab)[0].Interior
	if move.Thing != "bench" || move.Def != "SimpleResearchBench" || !rectInside(lab, move.To) || move.From != cellsRectangle(table.Cells) {
		t.Fatalf("move = %+v, lab %+v", move, lab)
	}
	// The review ranks it and hands it to the furniture executor, once idle.
	review := PlanTidyLayout(TidyRequest{Tier: domain.Known(BuildTierMasonry), Busy: domain.Known(false), Relocate: &proposal})
	if !review.Active || review.Proposal == nil || review.Proposal.Moves[0].Thing != "bench" {
		t.Fatalf("review = %+v", review)
	}
	if review = PlanTidyLayout(TidyRequest{Tier: domain.Known(BuildTierMasonry), Busy: domain.Known(true), Relocate: &proposal}); review.Active {
		t.Fatalf("busy review = %+v", review)
	}
	// A table already tried (a failed move) is never proposed again and keeps
	// the shelter planned.
	if _, ok := ShelterTableMove(f.plan, f.rooms, f.sleeping, built, map[string]bool{"bench": true}); ok {
		t.Fatal("tidied table proposed again")
	}
	if ShelterRetirable(f.plan, f.rooms, f.sleeping, built) {
		t.Fatal("retirable while the table stands in the shelter")
	}
	// A colonist still without a bedroom bed: no relocation yet.
	unhoused := f.sleeping
	unhoused.People = append([]SleepingPerson(nil), f.sleeping.People...)
	unhoused.People[0].OwnedBed = domain.Known("spot1")
	if _, ok := ShelterTableMove(f.plan, f.rooms, unhoused, built, nil); ok {
		t.Fatal("relocated before the colonists are housed")
	}
	// Moved: the table stands in the laboratory, the shelter retires and the
	// next review proposes nothing.
	moved := []CurrentBuilding{{ID: "bench", Building: b, Cells: rectCells(move.To)}}
	if !ShelterRetirable(f.plan, f.rooms, f.sleeping, moved) {
		t.Fatal("not retirable after the table moved")
	}
	if _, ok := ShelterTableMove(f.plan, f.rooms, f.sleeping, moved, nil); ok {
		t.Fatal("moved table proposed again")
	}
	next, changed := f.replan(t, true, nil)
	if !changed || len(next.roomsOf(PlannedShelter)) != 0 {
		t.Fatalf("shelter kept after the move (changed=%v)", changed)
	}
}
