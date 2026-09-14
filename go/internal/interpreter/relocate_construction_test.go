package interpreter

import (
	"context"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

const relocateConstructionCommand = `{"command":"relocate_construction","intentId":"shelter-01","replacement":{"x":2,"z":3,"width":4,"height":4,"wallDef":"Wall","doorDef":"Door","material":"Granite","entrance":"south","purpose":"shelter"}}`

// relocateInput is buildRoomInput plus the observed intent the replacement is
// moving, so both halves of the command's bounding are satisfied and only the
// fact under test is ever missing.
func relocateInput() Input {
	input := buildRoomInput()
	input.Facts.ObservedConstructionIntents = []string{"shelter-01", "kitchen_2"}
	return input
}

func TestModelRelocateConstructionCommandShape(t *testing.T) {
	t.Parallel()
	got, err := decode(relocateConstructionCommand, 1)
	if err != nil || got.Command != "relocate_construction" || got.IntentID == nil || *got.IntentID != "shelter-01" || got.Room == nil {
		t.Fatalf("relocate_construction shape: %v %v", got, err)
	}
	if *got.Room.X != 2 || *got.Room.Width != 4 || *got.Room.WallDef != "Wall" || *got.Room.Entrance != "south" {
		t.Fatal("replacement shell not decoded", *got.Room)
	}
	for _, text := range []string{
		`{"command":"relocate_construction"}`,
		`{"command":"relocate_construction","intentId":"a"}`,
		`{"command":"relocate_construction","intentId":null,"replacement":{"x":2,"z":3,"width":4,"height":4,"wallDef":"Wall","doorDef":"Door","material":"Granite","entrance":"south","purpose":"shelter"}}`,
		`{"command":"relocate_construction","intentId":"a b","replacement":{"x":2,"z":3,"width":4,"height":4,"wallDef":"Wall","doorDef":"Door","material":"Granite","entrance":"south","purpose":"shelter"}}`,
		// The replacement is a whole shell: no field of it may be left implied,
		// because relocation replaces a rectangle's entire perimeter.
		`{"command":"relocate_construction","intentId":"a","replacement":{"x":2,"z":3,"width":4,"height":4,"wallDef":"Wall","doorDef":"Door","material":"Granite","entrance":"south"}}`,
		`{"command":"relocate_construction","intentId":"a","replacement":{"x":2,"z":3,"width":4,"height":4,"wallDef":"Wall","doorDef":"Door","material":"Granite","entrance":null,"purpose":"shelter"}}`,
		`{"command":"relocate_construction","intentId":"a","replacement":{"x":2,"z":3,"width":4,"height":4,"wallDef":"Wall","doorDef":"Door","material":"Granite","entrance":"south","purpose":"shelter","roof":true}}`,
		// Python's replacement may be a buildings list; Go's is a room shell and
		// nothing else, so there is no other arm to name.
		`{"command":"relocate_construction","intentId":"a","replacement":{"buildings":[{"defName":"Wall","x":1,"z":1,"rotation":"north","stuff":""}]}}`,
		// There is deliberately no per-cell selector, exactly as in cancellation.
		`{"command":"relocate_construction","intentId":"a","replacement":{"x":2,"z":3,"width":4,"height":4,"wallDef":"Wall","doorDef":"Door","material":"Granite","entrance":"south","purpose":"shelter"},"cells":[{"x":1,"z":1}]}`,
	} {
		t.Run(text, func(t *testing.T) {
			if _, err := decode(text, 1); err == nil {
				t.Fatal("invalid relocate_construction accepted")
			}
		})
	}
}

// The proposal carries the replacement shell and the intent it supersedes, and
// no actions at all: the expansion exceeds MaxActions and which of the
// superseded placements still have a live order is journalled progress.
func TestRelocateConstructionProposal(t *testing.T) {
	t.Parallel()
	proposal, err := clientFixture(t, respond(relocateConstructionCommand)).Interpret(context.Background(), relocateInput())
	if err != nil {
		t.Fatal(err)
	}
	room := proposal.RelocateConstruction
	if !room.Set() || proposal.RelocateConstructionIntent != "shelter-01" || len(proposal.Plan.Actions()) != 0 {
		t.Fatal("incorrect typed relocate_construction proposal", room, proposal.RelocateConstructionIntent)
	}
	if room.Bounds() != (domain.RoomBounds{X: 2, Z: 3, Width: 4, Height: 4}) || room.Material() != "Granite" || room.Entrance() != domain.South {
		t.Fatal("incorrect replacement shell", room)
	}
	// Relocation is exclusive of every other outcome: it is neither a fresh
	// placement nor a bare cancellation, and it sets no policy.
	if proposal.BuildRoom.Set() || proposal.BuildRoomIntent != "" || proposal.CancelConstructionIntent != "" ||
		proposal.AdoptRoom.Set() || proposal.PopulationPolicy.Set() || !proposal.ExpeditionPolicy.Empty() ||
		proposal.PopulationDecision.Set() || !proposal.ResourcePolicy.Empty() || proposal.CreateGoal.Set() || proposal.CancelGoal != "" {
		t.Fatal("relocation populated another command's outcome", proposal)
	}
	if proposal.Generation != relocateInput().Current {
		t.Fatal("relocation must resolve against the input generation")
	}
}

// A construction the facts never named cannot be moved, however plausible the
// name looks next to one that was.
func TestRelocateConstructionBoundsIntentAgainstObservedIntents(t *testing.T) {
	t.Parallel()
	for _, name := range []string{"shelter", "shelter-02", "kitchen", "absent"} {
		text := strings.Replace(relocateConstructionCommand, `"intentId":"shelter-01"`, `"intentId":"`+name+`"`, 1)
		_, err := clientFixture(t, respond(text)).Interpret(context.Background(), relocateInput())
		assertKind(t, err, UnknownFacts)
	}
	// With no submitted constructions at all nothing is relocatable, even
	// though the replacement itself is perfectly well grounded.
	bare := buildRoomInput()
	_, err := clientFixture(t, respond(relocateConstructionCommand)).Interpret(context.Background(), bare)
	assertKind(t, err, UnknownFacts)
}

// The destination is bounded exactly as a build_room shell's is: replacing a
// real construction does not make the ground it moves onto real.
func TestRelocateConstructionBoundsReplacementAgainstFacts(t *testing.T) {
	t.Parallel()
	for _, tc := range []struct {
		name string
		edit func(*Input)
	}{
		{"unobserved perimeter cell", func(in *Input) { in.Facts.Cells = in.Facts.Cells[1:] }},
		{"unknown wall definition", func(in *Input) { in.Facts.Definitions = in.Facts.Definitions[1:] }},
		{"material not allowed", func(in *Input) {
			in.Facts.Definitions = []Definition{{DefName: "Wall", Stuff: []string{"Steel"}}, {DefName: "Door", Stuff: []string{"Steel"}}}
		}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			input := relocateInput()
			tc.edit(&input)
			_, err := clientFixture(t, respond(relocateConstructionCommand)).Interpret(context.Background(), input)
			assertKind(t, err, UnknownFacts)
		})
	}
	// A shell the domain constructor rejects is an invalid command, not a
	// missing fact -- a three-wide room has no interior.
	narrow := `{"command":"relocate_construction","intentId":"shelter-01","replacement":{"x":2,"z":3,"width":3,"height":4,"wallDef":"Wall","doorDef":"Door","material":"Granite","entrance":"south","purpose":"shelter"}}`
	_, err := clientFixture(t, respond(narrow)).Interpret(context.Background(), relocateInput())
	assertKind(t, err, InvalidCommand)
}

func TestRelocateConstructionRulesShape(t *testing.T) {
	t.Parallel()
	if !strings.Contains(rules, `"command":"relocate_construction"`) {
		t.Fatal("rules omit the relocation command shape")
	}
	if !strings.Contains(rules, "observed construction intent list") {
		t.Fatal("rules must bound the intent against supplied facts")
	}
	if !strings.Contains(rules, "must differ from the construction it replaces") {
		t.Fatal("rules must not offer a relocation that changes nothing")
	}
	// The replacement is a room shell; no buildings-list arm is offered.
	if strings.Contains(rules, `"replacement":{"buildings"`) {
		t.Fatal("rules offer an unsupported buildings replacement")
	}
}
