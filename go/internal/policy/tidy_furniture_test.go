package policy

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// tidyTestRole is a scratch role no catalog row or production template uses.
const tidyTestRole RoomRole = "TidyTest"

// swapInteriorTemplate installs t for role for the test's lifetime and
// restores the registry entry (or its absence) on cleanup, so a test
// template never outlives its test or collides with a real one.
func swapInteriorTemplate(t *testing.T, role RoomRole, tmpl InteriorTemplate) {
	t.Helper()
	prev, had := interiorTemplates[role]
	interiorTemplates[role] = tmpl
	t.Cleanup(func() {
		if had {
			interiorTemplates[role] = prev
		} else {
			delete(interiorTemplates, role)
		}
	})
}

// tidyFurnitureFixture is an idle colony with no zones and one 8x6 test
// room at (0,0) whose door is south of (1,0), so canonical and world cells
// coincide.
func tidyFurnitureFixture(t *testing.T, slots []InteriorPiece, pieces ...TidyPiece) TidyRequest {
	swapInteriorTemplate(t, tidyTestRole, InteriorTemplate{Name: "tidy-test", Plan: func(InteriorFrame, InteriorPieceDef) ([]InteriorPiece, bool) {
		return append([]InteriorPiece(nil), slots...), len(slots) > 0
	}})
	r := tidyFixture()
	r.Items = nil
	r.Rooms = []TidyRoom{{ID: "Room_9", Room: InteriorRoom{Role: tidyTestRole, Interior: Rectangle{0, 0, 8, 6}, Doors: []domain.Cell{{X: 1, Z: -1}}}, Pieces: pieces}}
	return r
}

func tidyPiece(thing, def string, size domain.Cell, rot domain.Rotation, x, z int32) TidyPiece {
	p := NewInteriorPiece("", def, size, rot, domain.Cell{X: x, Z: z})
	return TidyPiece{Thing: thing, Def: def, Size: size, Rot: rot, Rect: p.Rect}
}

func TestTidyFurnitureMovesAnOffPlanPieceToItsSlot(t *testing.T) {
	bed := domain.Cell{X: 1, Z: 2}
	slots := []InteriorPiece{NewInteriorPiece("bed", "Bed", bed, domain.North, domain.Cell{X: 3, Z: 4})}
	r := tidyFurnitureFixture(t, slots, tidyPiece("Bed_1", "Bed", bed, domain.East, 5, 0), tidyPiece("Lamp_1", "StandingLamp", domain.Cell{X: 1, Z: 1}, domain.North, 7, 5))
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
	r = tidyFurnitureFixture(t, slots, tidyPiece("Bed_1", "Bed", bed, domain.East, 5, 0))
	r.Tidied = []string{"Bed_1"}
	if review := PlanTidyLayout(r); review.Active {
		t.Fatalf("tidied review = %+v", review)
	}
}

func TestTidyFurnitureKeepsTheTidyGates(t *testing.T) {
	bed := domain.Cell{X: 1, Z: 2}
	slots := []InteriorPiece{NewInteriorPiece("bed", "Bed", bed, domain.North, domain.Cell{X: 3, Z: 4})}
	r := tidyFurnitureFixture(t, slots, tidyPiece("Bed_1", "Bed", bed, domain.East, 5, 0))
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
	r := tidyFurnitureFixture(t, slots, tidyPiece("D", "Dresser", one, domain.North, 7, 0), tidyPiece("E", "EndTable", one, domain.North, 3, 5))
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
	r := tidyFurnitureFixture(t, slots, tidyPiece("D", "Dresser", one, domain.North, 5, 5), tidyPiece("E", "EndTable", one, domain.North, 3, 5))
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
	r := tidyFurnitureFixture(t, slots, pieces...)
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

// A bedroom with a standing double bed is planned around that bed, so the
// bed on its slot is on plan rather than off a single-Bed plan.
func TestTidyFurnitureRoomsPlanTheStandingBed(t *testing.T) {
	interior := Rectangle{X: 0, Z: 0, Width: 6, Height: 5}
	door := domain.Cell{X: 1, Z: -1}
	plan, ok := PlanInterior(InteriorRoom{Role: RoomRoleBedroom, Interior: interior, Doors: []domain.Cell{door}}, InteriorPieceDefFor("DoubleBed"))
	if !ok {
		t.Fatal("no double-bed plan")
	}
	bed := plan.Pieces[0]
	b, err := domain.NewBuilding("DoubleBed", domain.Cell{X: bed.Rect.X, Z: bed.Rect.Z}, bed.Rot, "WoodLog")
	if err != nil {
		t.Fatal(err)
	}
	census := CurrentConstruction{Colony: true, Buildings: []CurrentBuilding{{ID: "Bed_1", Building: b, Cells: rectCells(bed.Rect)}}}
	rooms := RoomObservation{Rooms: []Room{{ID: "Room_1", Role: domain.Known(RoomRoleBedroom), Enclosed: domain.Known(true), Cells: rectCells(interior)}}}
	got := TidyFurnitureRooms(rooms, census, []SiteCell{{Cell: door, Doorway: domain.Known(true)}})
	if len(got) != 1 || len(got[0].Room.Standing) != 1 || got[0].Room.Standing[0] != "DoubleBed" {
		t.Fatalf("rooms %+v", got)
	}
	r := tidyFixture()
	r.Items, r.Rooms = nil, got
	if review := PlanTidyLayout(r); review.Active || review.Candidates != 0 {
		t.Fatalf("double bed flagged off plan: %+v", review)
	}
}
