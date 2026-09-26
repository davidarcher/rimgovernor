package policy

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// tidyTestSlots is the canonical plan of the test template, registered on
// a role no production template claims so the furniture kind is tested
// against the registry generically.
var tidyTestSlots []InteriorPiece

func init() {
	RegisterInteriorTemplate(RoomRoleTomb, InteriorTemplate{Name: "tidy-test", Plan: func(InteriorFrame, InteriorPieceDef) ([]InteriorPiece, bool) {
		return append([]InteriorPiece(nil), tidyTestSlots...), len(tidyTestSlots) > 0
	}})
}

// tidyFurnitureFixture is an idle colony with no zones and one 8x6 test
// room at (0,0) whose door is south of (1,0), so canonical and world cells
// coincide.
func tidyFurnitureFixture(slots []InteriorPiece, pieces ...TidyPiece) TidyRequest {
	tidyTestSlots = slots
	r := tidyFixture()
	r.Items = nil
	r.Rooms = []TidyRoom{{ID: "Room_9", Room: InteriorRoom{Role: RoomRoleTomb, Interior: Rectangle{0, 0, 8, 6}, Doors: []domain.Cell{{X: 1, Z: -1}}}, Pieces: pieces}}
	return r
}

func tidyPiece(thing, def string, size domain.Cell, rot domain.Rotation, x, z int32) TidyPiece {
	p := NewInteriorPiece("", def, size, rot, domain.Cell{X: x, Z: z})
	return TidyPiece{Thing: thing, Def: def, Size: size, Rot: rot, Rect: p.Rect}
}

func TestTidyFurnitureMovesAnOffPlanPieceToItsSlot(t *testing.T) {
	bed := domain.Cell{X: 1, Z: 2}
	slots := []InteriorPiece{NewInteriorPiece("bed", "Bed", bed, domain.North, domain.Cell{X: 3, Z: 4})}
	r := tidyFurnitureFixture(slots, tidyPiece("Bed_1", "Bed", bed, domain.East, 5, 0), tidyPiece("Lamp_1", "StandingLamp", domain.Cell{X: 1, Z: 1}, domain.North, 7, 5))
	review := PlanTidyLayout(r)
	p := review.Proposal
	if !review.Active || p == nil || p.Item.Kind != TidyFurniture || p.Item.ID != "Room_9" || len(p.Moves) != 1 {
		t.Fatalf("review = %+v", review)
	}
	m := p.Moves[0]
	if m.Thing != "Bed_1" || m.Slot != "bed" || m.To != (Rectangle{3, 4, 1, 2}) || m.Rot != domain.North || m.Temp || len(m.After) != 0 || !strings.Contains(p.Explanation, "1 of 1") {
		t.Fatalf("move = %+v (%s)", m, p.Explanation)
	}
	if m.Anchor() != (domain.Cell{X: 3, Z: 4}) {
		t.Fatalf("anchor = %v", m.Anchor())
	}
	// On plan: nothing to do. Tidied: never moved again.
	r.Rooms[0].Pieces[0] = tidyPiece("Bed_1", "Bed", bed, domain.North, 3, 4)
	if review := PlanTidyLayout(r); review.Active || review.Candidates != 0 {
		t.Fatalf("on-plan review = %+v", review)
	}
	r = tidyFurnitureFixture(slots, tidyPiece("Bed_1", "Bed", bed, domain.East, 5, 0))
	r.Tidied = []string{"Bed_1"}
	if review := PlanTidyLayout(r); review.Active {
		t.Fatalf("tidied review = %+v", review)
	}
}

func TestTidyFurnitureKeepsTheTidyGates(t *testing.T) {
	bed := domain.Cell{X: 1, Z: 2}
	slots := []InteriorPiece{NewInteriorPiece("bed", "Bed", bed, domain.North, domain.Cell{X: 3, Z: 4})}
	r := tidyFurnitureFixture(slots, tidyPiece("Bed_1", "Bed", bed, domain.East, 5, 0))
	r.Busy = domain.Known(true)
	if review := PlanTidyLayout(r); review.Active || review.Reason != "colony busy" {
		t.Fatalf("busy review = %+v", review)
	}
	r.Busy, r.InFlight = domain.Known(false), true
	if review := PlanTidyLayout(r); !review.Active || review.Proposal != nil {
		t.Fatalf("in-flight review = %+v", review)
	}
}

func TestTidyFurnitureOrdersMovesSoNoPieceBlocksAnotherTarget(t *testing.T) {
	one := domain.Cell{X: 1, Z: 1}
	slots := []InteriorPiece{
		NewInteriorPiece("a", "Dresser", one, domain.North, domain.Cell{X: 3, Z: 5}),
		NewInteriorPiece("b", "EndTable", one, domain.North, domain.Cell{X: 5, Z: 5}),
	}
	// The end table stands on the dresser's slot: it must move first.
	r := tidyFurnitureFixture(slots, tidyPiece("D", "Dresser", one, domain.North, 7, 0), tidyPiece("E", "EndTable", one, domain.North, 3, 5))
	moves := PlanTidyLayout(r).Proposal.Moves
	if len(moves) != 2 || moves[0].Thing != "E" || moves[1].Thing != "D" || len(moves[1].After) != 1 || moves[1].After[0] != 0 {
		t.Fatalf("moves = %+v", moves)
	}
}

func TestTidyFurnitureSwapsThroughAFreeCell(t *testing.T) {
	one := domain.Cell{X: 1, Z: 1}
	slots := []InteriorPiece{
		NewInteriorPiece("a", "Dresser", one, domain.North, domain.Cell{X: 3, Z: 5}),
		NewInteriorPiece("b", "EndTable", one, domain.North, domain.Cell{X: 5, Z: 5}),
	}
	r := tidyFurnitureFixture(slots, tidyPiece("D", "Dresser", one, domain.North, 5, 5), tidyPiece("E", "EndTable", one, domain.North, 3, 5))
	p := PlanTidyLayout(r).Proposal
	if p == nil || len(p.Moves) != 3 || p.Gain != 2 {
		t.Fatalf("proposal = %+v", p)
	}
	park, first, last := p.Moves[0], p.Moves[1], p.Moves[2]
	if !park.Temp || park.Slot != "" || park.To == slots[0].Rect || park.To == slots[1].Rect {
		t.Fatalf("park = %+v", park)
	}
	if first.Temp || first.Thing == park.Thing || len(first.After) != 1 || first.After[0] != 0 {
		t.Fatalf("first = %+v", first)
	}
	if last.Thing != park.Thing || last.From != park.To || len(last.After) != 2 {
		t.Fatalf("last = %+v", last)
	}
}

func TestTidyFurnitureCapsTheRetrofitBatch(t *testing.T) {
	one := domain.Cell{X: 1, Z: 1}
	var slots []InteriorPiece
	var pieces []TidyPiece
	for i := int32(0); i < 10; i++ {
		id := string(rune('A' + i))
		x, z := 2+i%5, 2+i/5*2
		slots = append(slots, NewInteriorPiece("s"+id, "Def"+id, one, domain.North, domain.Cell{X: x, Z: z}))
		pieces = append(pieces, tidyPiece("T"+id, "Def"+id, one, domain.North, x, z+1))
	}
	r := tidyFurnitureFixture(slots, pieces...)
	p := PlanTidyLayout(r).Proposal
	if p == nil || len(p.Moves) != tidyFurnitureCap {
		t.Fatalf("proposal = %+v", p)
	}
	// The next review, with the batch tidied, proposes the rest.
	for _, m := range p.Moves {
		r.Tidied = append(r.Tidied, m.Thing)
	}
	if next := PlanTidyLayout(r).Proposal; next == nil || len(next.Moves) != 10-tidyFurnitureCap {
		t.Fatalf("next = %+v", next)
	}
}
