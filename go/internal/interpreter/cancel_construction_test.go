package interpreter

import (
	"context"
	"errors"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/model"
)

func TestModelCancelConstructionCommandShape(t *testing.T) {
	t.Parallel()
	got, err := decode(`{"command":"cancel_construction","intentId":"north-barracks"}`, 1)
	if err != nil || got.Command != "cancel_construction" || got.IntentID == nil || *got.IntentID != "north-barracks" {
		t.Fatalf("cancel_construction shape: %v %v", got, err)
	}
	// Decoding bounds only the shape: whether the intent was ever submitted,
	// and whether any of its placements are still pending, belong to the
	// interpreter and the store respectively.
	if got.Room != nil {
		t.Fatal("cancellation must not carry a room shell")
	}
	for _, text := range []string{
		`{"command":"cancel_construction"}`,
		`{"command":"cancel_construction","intentId":null}`,
		`{"command":"cancel_construction","intentId":""}`,
		`{"command":"cancel_construction","intentId":7}`,
		`{"command":"cancel_construction","intentId":["a"]}`,
		`{"command":"cancel_construction","intentId":"a b"}`,
		`{"command":"cancel_construction","intentId":"a","reason":"b"}`,
		// There is deliberately no per-placement or per-cell selector: a model
		// may withdraw a whole named construction or nothing.
		`{"command":"cancel_construction","intentId":"a","cells":[{"x":1,"z":1}]}`,
		`{"command":"cancel_construction","intentId":"a","x":1,"z":1}`,
	} {
		t.Run(text, func(t *testing.T) {
			if _, err := decode(text, 1); err == nil {
				t.Fatal("invalid cancel_construction accepted")
			}
		})
	}
}

func cancelConstructionReply(text string) func(context.Context, model.Request) (model.Response, error) {
	return func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: text, FinishReason: model.Stop}, nil
	}
}

func TestCancelConstructionBoundsIntentAgainstObservedIntents(t *testing.T) {
	t.Parallel()
	input := inputFixture()
	input.Facts.ObservedConstructionIntents = []string{"north-barracks", "kitchen_2"}
	proposal, err := clientFixture(t, cancelConstructionReply(`{"command":"cancel_construction","intentId":"kitchen_2"}`)).Interpret(context.Background(), input)
	if err != nil {
		t.Fatal(err)
	}
	if proposal.CancelConstructionIntent != "kitchen_2" || proposal.BuildRoomIntent != "" || proposal.BuildRoom.Set() {
		t.Fatal("incorrect typed cancellation proposal", proposal.CancelConstructionIntent)
	}
	if len(proposal.Plan.Actions()) != 0 || proposal.PopulationPolicy.Set() || !proposal.ExpeditionPolicy.Empty() ||
		proposal.PopulationDecision.Set() || !proposal.ResourcePolicy.Empty() || proposal.CreateGoal.Set() || proposal.CancelGoal != "" {
		t.Fatal("cancellation must propose no actions and no policy")
	}
	if proposal.Generation != input.Current {
		t.Fatal("cancellation must resolve against the input generation")
	}
	// An intent the facts never named cannot be withdrawn, however plausible
	// it looks next to one that was.
	for _, name := range []string{"kitchen", "kitchen_3", "north-barracks-extra", "absent"} {
		_, err := clientFixture(t, cancelConstructionReply(`{"command":"cancel_construction","intentId":"`+name+`"}`)).Interpret(context.Background(), input)
		var failure *Failure
		if !errors.As(err, &failure) || failure.Kind != UnknownFacts {
			t.Fatal("unobserved construction intent accepted", name, err)
		}
	}
	// With no submitted constructions at all nothing is cancellable.
	bare := inputFixture()
	_, err = clientFixture(t, cancelConstructionReply(`{"command":"cancel_construction","intentId":"kitchen_2"}`)).Interpret(context.Background(), bare)
	var failure *Failure
	if !errors.As(err, &failure) || failure.Kind != UnknownFacts {
		t.Fatal(err)
	}
}

func TestObservedConstructionIntentFactsAreBoundedAndUnique(t *testing.T) {
	t.Parallel()
	reply := cancelConstructionReply(`{"command":"cancel_construction","intentId":"north-barracks"}`)
	input := inputFixture()
	input.Facts.ObservedConstructionIntents = []string{"north-barracks", "north-barracks"}
	if _, err := clientFixture(t, reply).Interpret(context.Background(), input); err == nil {
		t.Fatal("duplicate observed construction intent accepted")
	}
	for _, bad := range []string{"", "  ", "north barracks", strings.Repeat("a", 41)} {
		input.Facts.ObservedConstructionIntents = []string{bad}
		if _, err := clientFixture(t, reply).Interpret(context.Background(), input); err == nil {
			t.Fatal("malformed observed construction intent accepted", bad)
		}
	}
	input.Facts.ObservedConstructionIntents = make([]string, 1025)
	for n := range input.Facts.ObservedConstructionIntents {
		input.Facts.ObservedConstructionIntents[n] = "intent-" + string(rune('a'+n%26)) + string(rune('a'+n/26))
	}
	if _, err := clientFixture(t, reply).Interpret(context.Background(), input); err == nil {
		t.Fatal("unbounded observed construction intent list accepted")
	}
}

// The rules must offer the command, and must not offer a per-cell selector the
// decoder would refuse.
func TestCancelConstructionRulesShape(t *testing.T) {
	t.Parallel()
	if !strings.Contains(rules, `"command":"cancel_construction"`) || !strings.Contains(rules, "cancel_construction") {
		t.Fatal("rules omit the cancellation command shape")
	}
	if !strings.Contains(rules, "observed construction intent list") {
		t.Fatal("rules must bound the intent against supplied facts")
	}
	if strings.Contains(rules, `"command":"cancel_construction","cells"`) {
		t.Fatal("rules offer an unsupported per-cell cancellation")
	}
}
