package interpreter

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

const adoptRoomCommand = `{"command":"adopt_room","intentId":"shelter-01","room":{"x":2,"z":3,"width":4,"height":4,"entrance":"south"}}`

// adoptRoomInput supplies every interior cell of a four by four room at (2,3)
// plus its south entrance, so only the fact under test is ever missing. A four
// by four rectangle has a two by two interior.
func adoptRoomInput() Input {
	input := inputFixture()
	input.Facts.Cells = []domain.Cell{{X: 3, Z: 4}, {X: 4, Z: 4}, {X: 3, Z: 5}, {X: 4, Z: 5}, {X: 4, Z: 3}}
	return input
}

// An adoption carries no Plan for the strongest reason in this family: it
// issues no construction at all.
func TestAdoptRoomProposal(t *testing.T) {
	proposal, err := clientFixture(t, respond(adoptRoomCommand)).Interpret(context.Background(), adoptRoomInput())
	if err != nil {
		t.Fatal(err)
	}
	room := proposal.AdoptRoom
	if !room.Set() || proposal.AdoptRoomIntent != "shelter-01" || len(proposal.Plan.Actions()) != 0 {
		t.Fatal("incorrect typed adopt_room proposal", room, proposal.AdoptRoomIntent)
	}
	if room.Bounds() != (domain.RoomBounds{X: 2, Z: 3, Width: 4, Height: 4}) || room.Entrance() != domain.South || !room.Rectangular() {
		t.Fatal("incorrect adopted room", room)
	}
	if len(room.Interior()) != 4 || room.EntranceCell() != (domain.Cell{X: 4, Z: 3}) {
		t.Fatal("incorrect derived geometry", room.Interior(), room.EntranceCell())
	}
	// Adoption must never be mistaken for construction.
	if proposal.BuildRoom.Set() || proposal.BuildRoomIntent != "" {
		t.Fatal("adoption produced a construction proposal")
	}
	if proposal.Generation != adoptRoomInput().Current {
		t.Fatal("generation not carried")
	}
}

// A room the model never observed is a room it invented.
func TestAdoptRoomBoundsCellsAgainstFacts(t *testing.T) {
	input := adoptRoomInput()
	input.Facts.Cells = input.Facts.Cells[:3]
	if _, err := clientFixture(t, respond(adoptRoomCommand)).Interpret(context.Background(), input); err == nil {
		t.Fatal("an unobserved adopted interior cell was accepted")
	}
	missingDoor := adoptRoomInput()
	missingDoor.Facts.Cells = missingDoor.Facts.Cells[:4]
	if _, err := clientFixture(t, respond(adoptRoomCommand)).Interpret(context.Background(), missingDoor); err == nil {
		t.Fatal("an unobserved entrance cell was accepted")
	}
}

func TestAdoptRoomNonrectangular(t *testing.T) {
	// A five by five room at (2,3) whose exact interior is the three by three
	// block inside it, entered from the south at (4,3).
	const command = `{"command":"adopt_room","intentId":"shelter-01","room":{"x":2,"z":3,"width":5,"height":5,"entrance":"south",` +
		`"interiorCells":[{"x":3,"z":4},{"x":4,"z":4},{"x":5,"z":4},{"x":3,"z":5},{"x":4,"z":5},{"x":5,"z":5},{"x":3,"z":6},{"x":4,"z":6},{"x":5,"z":6}],` +
		`"entranceCell":{"x":4,"z":3}}`
	input := inputFixture()
	input.Facts.Cells = []domain.Cell{{X: 3, Z: 4}, {X: 4, Z: 4}, {X: 5, Z: 4}, {X: 3, Z: 5}, {X: 4, Z: 5}, {X: 5, Z: 5}, {X: 3, Z: 6}, {X: 4, Z: 6}, {X: 5, Z: 6}, {X: 4, Z: 3}}
	proposal, err := clientFixture(t, respond(command+"}")).Interpret(context.Background(), input)
	if err != nil {
		t.Fatal(err)
	}
	room := proposal.AdoptRoom
	if !room.Set() || room.Rectangular() || !room.EntranceCellSet() || len(room.InteriorCells()) != 9 {
		t.Fatal("incorrect nonrectangular adoption", room)
	}
	if room.EntranceCell() != (domain.Cell{X: 4, Z: 3}) {
		t.Fatal("incorrect exact entrance cell", room.EntranceCell())
	}
}

// Python's geometry validator refuses a half-supplied nonrectangular room; so
// does the domain constructor this command defers to.
func TestAdoptRoomRefusesHalfSuppliedShape(t *testing.T) {
	const command = `{"command":"adopt_room","intentId":"shelter-01","room":{"x":2,"z":3,"width":5,"height":5,"entrance":"south",` +
		`"interiorCells":[{"x":3,"z":4}]}}`
	if _, err := clientFixture(t, respond(command)).Interpret(context.Background(), adoptRoomInput()); err == nil {
		t.Fatal("interior cells without an entrance cell were accepted")
	}
}

func TestAdoptRoomRefusesUnknownFieldsAndIntents(t *testing.T) {
	for _, command := range []string{
		`{"command":"adopt_room","intentId":"shelter-01","room":{"x":2,"z":3,"width":4,"height":4,"entrance":"south","wallDef":"Wall"}}`,
		`{"command":"adopt_room","intentId":"shelter-01","room":{"x":2,"z":3,"width":4,"height":4,"entrance":"south","entranceCell":null}}`,
		`{"command":"adopt_room","intentId":"shelter-01","room":{"x":2,"z":3,"width":4,"entrance":"south"}}`,
		`{"command":"adopt_room","intentId":"not a valid intent!","room":{"x":2,"z":3,"width":4,"height":4,"entrance":"south"}}`,
		`{"command":"adopt_room","intentId":"shelter-01","room":{"x":2,"z":3,"width":2,"height":2,"entrance":"south"}}`,
	} {
		if _, err := clientFixture(t, respond(command)).Interpret(context.Background(), adoptRoomInput()); err == nil {
			t.Fatalf("accepted %s", command)
		}
	}
}
