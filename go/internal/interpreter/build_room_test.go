package interpreter

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/model"
)

const buildRoomCommand = `{"command":"build_room","intentId":"shelter-01","room":{"x":2,"z":3,"width":4,"height":4,"wallDef":"Wall","doorDef":"Door","material":"Granite","entrance":"south","purpose":"shelter"}}`

// buildRoomInput supplies the definitions and every perimeter anchor a four by
// four room at (2,3) expands to, so only the fact under test is ever missing.
func buildRoomInput() Input {
	input := inputFixture()
	input.Facts.Definitions = []Definition{{DefName: "Wall", Stuff: []string{"Granite"}}, {DefName: "Door", Stuff: []string{"Granite"}}}
	room, err := domain.NewRoomShell(domain.RoomBounds{X: 2, Z: 3, Width: 4, Height: 4}, "Wall", "Door", "Granite", domain.South, domain.ShelterRoom)
	if err != nil {
		panic(err)
	}
	var cells []domain.Cell
	for _, placement := range room.Placements() {
		cells = append(cells, placement.Cell())
	}
	input.Facts.Cells = cells
	return input
}

func respond(text string) completeFunc {
	return func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: text, FinishReason: model.Stop}, nil
	}
}

// A room shell carries no Plan: its expansion is larger than MaxActions can
// express, so the whole shell travels and is expanded once at submission.
func TestBuildRoomProposal(t *testing.T) {
	proposal, err := clientFixture(t, respond(buildRoomCommand)).Interpret(context.Background(), buildRoomInput())
	if err != nil {
		t.Fatal(err)
	}
	room := proposal.BuildRoom
	if !room.Set() || proposal.BuildRoomIntent != "shelter-01" || len(proposal.Plan.Actions()) != 0 {
		t.Fatal("incorrect typed build_room proposal", room, proposal.BuildRoomIntent)
	}
	if room.Bounds() != (domain.RoomBounds{X: 2, Z: 3, Width: 4, Height: 4}) || room.WallDefinition() != "Wall" ||
		room.DoorDefinition() != "Door" || room.Material() != "Granite" || room.Entrance() != domain.South || room.Purpose() != domain.ShelterRoom {
		t.Fatal("incorrect room shell", room)
	}
	if len(room.Placements()) != 12 || room.Door() != (domain.Cell{X: 4, Z: 3}) {
		t.Fatal("incorrect expansion", room.Placements(), room.Door())
	}
	if proposal.Generation != buildRoomInput().Current {
		t.Fatal("generation not carried")
	}
}

// Nothing in the request may be invented: the definitions and material are
// bounded exactly as a build placement's, and every expanded cell exactly as a
// create_zone footprint's.
func TestBuildRoomRefusesUnknownFacts(t *testing.T) {
	offMap := `{"command":"build_room","intentId":"i","room":{"x":18,"z":18,"width":4,"height":4,"wallDef":"Wall","doorDef":"Door","material":"Granite","entrance":"south","purpose":"shelter"}}`
	for _, tc := range []struct {
		name, text string
		edit       func(*Input)
	}{
		{"unobserved perimeter cell", buildRoomCommand, func(in *Input) { in.Facts.Cells = in.Facts.Cells[1:] }},
		{"unknown wall definition", buildRoomCommand, func(in *Input) { in.Facts.Definitions = in.Facts.Definitions[1:] }},
		{"unknown door definition", buildRoomCommand, func(in *Input) { in.Facts.Definitions = in.Facts.Definitions[:1] }},
		{"material not allowed", buildRoomCommand, func(in *Input) {
			in.Facts.Definitions = []Definition{{DefName: "Wall", Stuff: []string{"Steel"}}, {DefName: "Door", Stuff: []string{"Steel"}}}
		}},
		{"room leaves the map", offMap, func(in *Input) {}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			input := buildRoomInput()
			tc.edit(&input)
			_, err := clientFixture(t, respond(tc.text)).Interpret(context.Background(), input)
			assertKind(t, err, UnknownFacts)
		})
	}
}

// The domain constructor owns the shape rules; the interpreter reports them as
// an invalid command rather than a missing fact.
func TestBuildRoomRefusesInvalidShell(t *testing.T) {
	for _, tc := range []struct{ name, text string }{
		{"no interior", `{"command":"build_room","intentId":"i","room":{"x":2,"z":3,"width":3,"height":4,"wallDef":"Wall","doorDef":"Door","material":"Granite","entrance":"south","purpose":"shelter"}}`},
		{"wall equals door", `{"command":"build_room","intentId":"i","room":{"x":2,"z":3,"width":4,"height":4,"wallDef":"Wall","doorDef":"Wall","material":"Granite","entrance":"south","purpose":"shelter"}}`},
		{"unsupported entrance", `{"command":"build_room","intentId":"i","room":{"x":2,"z":3,"width":4,"height":4,"wallDef":"Wall","doorDef":"Door","material":"Granite","entrance":"up","purpose":"shelter"}}`},
		{"unsupported purpose", `{"command":"build_room","intentId":"i","room":{"x":2,"z":3,"width":4,"height":4,"wallDef":"Wall","doorDef":"Door","material":"Granite","entrance":"south","purpose":"fortress"}}`},
		{"invalid intent", `{"command":"build_room","intentId":"not valid","room":{"x":2,"z":3,"width":4,"height":4,"wallDef":"Wall","doorDef":"Door","material":"Granite","entrance":"south","purpose":"shelter"}}`},
		{"missing room field", `{"command":"build_room","intentId":"i","room":{"x":2,"z":3,"width":4,"height":4,"wallDef":"Wall","doorDef":"Door","material":"Granite","entrance":"south"}}`},
		{"null room field", `{"command":"build_room","intentId":"i","room":{"x":2,"z":3,"width":4,"height":4,"wallDef":"Wall","doorDef":"Door","material":"Granite","entrance":"south","purpose":null}}`},
		{"extra command field", `{"command":"build_room","intentId":"i","room":{"x":2,"z":3,"width":4,"height":4,"wallDef":"Wall","doorDef":"Door","material":"Granite","entrance":"south","purpose":"shelter"},"extra":1}`},
		{"missing room", `{"command":"build_room","intentId":"i"}`},
	} {
		t.Run(tc.name, func(t *testing.T) {
			_, err := clientFixture(t, respond(tc.text)).Interpret(context.Background(), buildRoomInput())
			assertKind(t, err, InvalidCommand)
		})
	}
}

// An empty material is the permitted native default, and only when the
// definition allows it.
func TestBuildRoomDefaultMaterial(t *testing.T) {
	text := `{"command":"build_room","intentId":"i","room":{"x":2,"z":3,"width":4,"height":4,"wallDef":"Wall","doorDef":"Door","material":"","entrance":"north","purpose":"storage"}}`
	input := buildRoomInput()
	input.Facts.Definitions = []Definition{{DefName: "Wall", AllowDefaultStuff: true}, {DefName: "Door", AllowDefaultStuff: true}}
	proposal, err := clientFixture(t, respond(text)).Interpret(context.Background(), input)
	if err != nil {
		t.Fatal(err)
	}
	if proposal.BuildRoom.Material() != "" || proposal.BuildRoom.Purpose() != domain.StorageRoom || proposal.BuildRoom.Entrance() != domain.North {
		t.Fatal(proposal.BuildRoom)
	}

	input = buildRoomInput()
	_, err = clientFixture(t, respond(text)).Interpret(context.Background(), input)
	assertKind(t, err, UnknownFacts)
}

// The rules the model is given must describe the command it is allowed to emit.
func TestBuildRoomRulesDocumented(t *testing.T) {
	for _, fragment := range []string{`"command":"build_room"`, "intentId", "wallDef", "doorDef", "entrance", "purpose"} {
		if !contains(rules, fragment) {
			t.Fatal("rules omit", fragment)
		}
	}
}

func contains(haystack, needle string) bool {
	for i := 0; i+len(needle) <= len(haystack); i++ {
		if haystack[i:i+len(needle)] == needle {
			return true
		}
	}
	return false
}
