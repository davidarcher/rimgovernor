package interpreter

import (
	"context"
	"errors"
	"strings"
	"testing"
)

func TestModelEvaluateWorldShape(t *testing.T) {
	t.Parallel()
	command, err := decode(`{"command":"evaluate_world"}`, 1)
	if err != nil || command.Command != "evaluate_world" {
		t.Fatalf("evaluate_world shape: %v %v", command, err)
	}
	// The contract is a bare kind discriminator, so any extra field is a
	// filtered evaluation this command does not offer and is refused outright
	// rather than silently dropped.
	for _, text := range []string{
		`{"command":"evaluate_world","caravan":"Thing_1"}`,
		`{"command":"evaluate_world","quest":"Quest_1"}`,
		`{"command":"evaluate_world","scope":"caravans"}`,
		`{"command":"evaluate_world","travelFoodMarginDays":0.5}`,
		`{"command":"evaluate_world","goal":null}`,
	} {
		t.Run(text, func(t *testing.T) {
			if _, err := decode(text, 1); err == nil {
				t.Fatal("argument-carrying evaluate_world accepted")
			}
		})
	}
}

func TestEvaluateWorldProposalCarriesNothingButTheFlag(t *testing.T) {
	t.Parallel()
	input := inputFixture()
	proposal, err := clientFixture(t, goalReply(`{"command":"evaluate_world"}`)).Interpret(context.Background(), input)
	if err != nil {
		t.Fatal(err)
	}
	if !proposal.EvaluateWorld {
		t.Fatal("evaluate_world must set its own proposal flag")
	}
	// It is the emptiest proposal in the family: no plan, no configuration, no
	// named entity, no construction. The advisory itself is read from
	// buildingruntime.WorldEvaluation by the consumer, never composed here.
	if len(proposal.Plan.Actions()) != 0 || proposal.PopulationPolicy.Set() || !proposal.ExpeditionPolicy.Empty() ||
		proposal.PopulationDecision.Set() || !proposal.ResourcePolicy.Empty() || proposal.CreateGoal.Set() ||
		proposal.CancelGoal != "" || proposal.BuildRoom.Set() || proposal.AdoptRoom.Set() ||
		proposal.CancelConstructionIntent != "" || proposal.RelocateConstructionIntent != "" {
		t.Fatal("world evaluation must propose no actions, policy, goal or construction")
	}
	if proposal.Generation != input.Current {
		t.Fatal("world evaluation must resolve against the input generation")
	}
	// Every other command leaves the flag clear, so a consumer can branch on it
	// alone.
	other, err := clientFixture(t, goalReply(`{"command":"create_goal","goal":"MaintainWood"}`)).Interpret(context.Background(), input)
	if err != nil || other.EvaluateWorld {
		t.Fatal("unrelated command set the world-evaluation flag", err)
	}
}

func TestEvaluateWorldRejectsArgumentsThroughTheWholeInterpretation(t *testing.T) {
	t.Parallel()
	input := inputFixture()
	_, err := clientFixture(t, goalReply(`{"command":"evaluate_world","caravan":"Thing_1"}`)).Interpret(context.Background(), input)
	var failure *Failure
	if !errors.As(err, &failure) || failure.Kind != InvalidCommand {
		t.Fatal("argument-carrying evaluate_world accepted", err)
	}
}

// The rules the model is given must offer the command and must not suggest it
// can order anything.
func TestEvaluateWorldRulesOfferTheZeroArgumentShape(t *testing.T) {
	t.Parallel()
	if !strings.Contains(rules, `{"command":"evaluate_world"}`) {
		t.Fatal("rules omit the world evaluation command shape")
	}
	if strings.Contains(rules, `"command":"evaluate_world","`) {
		t.Fatal("rules offer an argument evaluate_world does not carry")
	}
}
