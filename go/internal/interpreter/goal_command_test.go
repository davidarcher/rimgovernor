package interpreter

import (
	"context"
	"errors"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/model"
)

func TestModelGoalCommandShapes(t *testing.T) {
	t.Parallel()
	create, err := decode(`{"command":"create_goal","goal":"EnsureFoodSupply"}`, 1)
	if err != nil || create.Command != "create_goal" || create.Goal == nil || *create.Goal != "EnsureFoodSupply" {
		t.Fatalf("create_goal shape: %v %v", create, err)
	}
	cancel, err := decode(`{"command":"cancel_goal","goal":"player-abc-MaintainWood"}`, 1)
	if err != nil || cancel.Command != "cancel_goal" || cancel.Goal == nil || *cancel.Goal != "player-abc-MaintainWood" {
		t.Fatalf("cancel_goal shape: %v %v", cancel, err)
	}
	// Decoding bounds only the shape: the kind whitelist and the observed-goal
	// membership check belong to the interpreter.
	unknown, err := decode(`{"command":"create_goal","goal":"EnsureComfort"}`, 1)
	if err != nil || unknown.Goal == nil || *unknown.Goal != "EnsureComfort" {
		t.Fatalf("unsupported kinds decode here and are refused later: %v %v", unknown, err)
	}
	for _, text := range []string{
		`{"command":"create_goal"}`,
		`{"command":"create_goal","goal":null}`,
		`{"command":"create_goal","goal":""}`,
		`{"command":"create_goal","goal":1}`,
		`{"command":"create_goal","goal":["EnsureFoodSupply"]}`,
		// Python's per-goal target fields have no Go counterpart; carrying one
		// makes the command invalid rather than silently dropping it.
		`{"command":"create_goal","goal":"EnsureFoodSupply","food_days":14}`,
		`{"command":"create_goal","goal":"MaintainResource","resource":"Steel","quantity":100}`,
		`{"command":"create_goal","goal":"MaintainResource","deep_extraction":true}`,
		`{"command":"create_goal","goal":"MaintainWaste","unwanted":["Thing_1"]}`,
		`{"command":"create_goal","goal":"MaintainWaste","bury":["Thing_1"]}`,
		`{"command":"cancel_goal"}`,
		`{"command":"cancel_goal","goal":null}`,
		`{"command":"cancel_goal","goal":""}`,
		`{"command":"cancel_goal","goal":7}`,
		`{"command":"cancel_goal","goal":"a","reason":"b"}`,
	} {
		t.Run(text, func(t *testing.T) {
			if _, err := decode(text, 1); err == nil {
				t.Fatal("invalid goal command accepted")
			}
		})
	}
}

func goalReply(text string) func(context.Context, model.Request) (model.Response, error) {
	return func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: text, FinishReason: model.Stop}, nil
	}
}

func TestCreateGoalProposalCarriesNoPlanAndBoundsTheKind(t *testing.T) {
	t.Parallel()
	input := inputFixture()
	proposal, err := clientFixture(t, goalReply(`{"command":"create_goal","goal":"MaintainWaste"}`)).Interpret(context.Background(), input)
	if err != nil {
		t.Fatal(err)
	}
	if proposal.CreateGoal != domain.MaintainWasteGoal || !proposal.CreateGoal.Set() || proposal.CancelGoal != "" {
		t.Fatal("incorrect typed goal activation proposal", proposal.CreateGoal)
	}
	if len(proposal.Plan.Actions()) != 0 || proposal.PopulationPolicy.Set() || !proposal.ExpeditionPolicy.Empty() ||
		proposal.PopulationDecision.Set() || !proposal.ResourcePolicy.Empty() {
		t.Fatal("goal activation must propose no actions and no policy")
	}
	if proposal.Generation != input.Current {
		t.Fatal("goal activation must resolve against the input generation")
	}
	// Every whitelisted kind is accepted, and nothing else is.
	for _, kind := range domain.GoalKinds() {
		got, err := clientFixture(t, goalReply(`{"command":"create_goal","goal":"`+string(kind)+`"}`)).Interpret(context.Background(), input)
		if err != nil || got.CreateGoal != kind {
			t.Fatal(kind, got.CreateGoal, err)
		}
	}
	for _, name := range []string{"EnsureComfort", "MaintainHerd", "ensurefoodsupply", "routine-EnsureFoodSupply"} {
		_, err := clientFixture(t, goalReply(`{"command":"create_goal","goal":"`+name+`"}`)).Interpret(context.Background(), input)
		var failure *Failure
		if !errors.As(err, &failure) || failure.Kind != InvalidCommand {
			t.Fatal("unwhitelisted goal kind accepted", name, err)
		}
	}
}

func TestCancelGoalBoundsIdentityAgainstObservedGoals(t *testing.T) {
	t.Parallel()
	input := inputFixture()
	input.Facts.ObservedGoals = []string{"player-abc-MaintainWood", "routine-1234-EnsureCooking"}
	proposal, err := clientFixture(t, goalReply(`{"command":"cancel_goal","goal":"routine-1234-EnsureCooking"}`)).Interpret(context.Background(), input)
	if err != nil {
		t.Fatal(err)
	}
	if proposal.CancelGoal != domain.GoalID("routine-1234-EnsureCooking") || proposal.CreateGoal.Set() {
		t.Fatal("incorrect typed goal cancellation proposal", proposal.CancelGoal)
	}
	if len(proposal.Plan.Actions()) != 0 || proposal.PopulationPolicy.Set() || !proposal.ExpeditionPolicy.Empty() ||
		proposal.PopulationDecision.Set() || !proposal.ResourcePolicy.Empty() {
		t.Fatal("goal cancellation must propose no actions and no policy")
	}
	if proposal.Generation != input.Current {
		t.Fatal("goal cancellation must resolve against the input generation")
	}
	// Unlike Python's resolve_goal_id there is no fuzzy or prefix matching: a
	// kind name, a prefix or an unobserved identity is refused outright.
	for _, name := range []string{"EnsureCooking", "routine-1234", "player-abc-MaintainWood-extra", "absent"} {
		_, err := clientFixture(t, goalReply(`{"command":"cancel_goal","goal":"`+name+`"}`)).Interpret(context.Background(), input)
		var failure *Failure
		if !errors.As(err, &failure) || failure.Kind != UnknownFacts {
			t.Fatal("unobserved goal identity accepted", name, err)
		}
	}
	// With no observed goals at all nothing is cancellable.
	bare := inputFixture()
	_, err = clientFixture(t, goalReply(`{"command":"cancel_goal","goal":"routine-1234-EnsureCooking"}`)).Interpret(context.Background(), bare)
	var failure *Failure
	if !errors.As(err, &failure) || failure.Kind != UnknownFacts {
		t.Fatal(err)
	}
}

func TestObservedGoalFactsAreBoundedAndUnique(t *testing.T) {
	t.Parallel()
	input := inputFixture()
	input.Facts.ObservedGoals = []string{"goal", "goal"}
	if _, err := clientFixture(t, goalReply(`{"command":"create_goal","goal":"MaintainWood"}`)).Interpret(context.Background(), input); err == nil {
		t.Fatal("duplicate observed goal accepted")
	}
	input.Facts.ObservedGoals = []string{"  "}
	if _, err := clientFixture(t, goalReply(`{"command":"create_goal","goal":"MaintainWood"}`)).Interpret(context.Background(), input); err == nil {
		t.Fatal("blank observed goal accepted")
	}
	input.Facts.ObservedGoals = make([]string, 1025)
	for n := range input.Facts.ObservedGoals {
		input.Facts.ObservedGoals[n] = "goal-" + string(rune('a'+n%26)) + string(rune('a'+n/26))
	}
	if _, err := clientFixture(t, goalReply(`{"command":"create_goal","goal":"MaintainWood"}`)).Interpret(context.Background(), input); err == nil {
		t.Fatal("unbounded observed goal list accepted")
	}
}

// The rules the model is given must name every kind the interpreter accepts,
// and must not offer the per-goal target fields Go cannot carry.
func TestGoalRulesEnumerateEveryKindAndNoTargets(t *testing.T) {
	t.Parallel()
	for _, kind := range domain.GoalKinds() {
		if !strings.Contains(rules, string(kind)) {
			t.Fatal("rules omit a supported goal kind", kind)
		}
	}
	// "quantity" is deliberately absent from this list: set_resource_reserve's
	// own rule text uses the word for its protected reserve.
	for _, absent := range []string{"food_days", "deep_extraction", "unwanted", "bury", "foodDays\",\"goal"} {
		if strings.Contains(rules, absent) {
			t.Fatal("rules offer an unsupported goal target field", absent)
		}
	}
	if !strings.Contains(rules, `"command":"create_goal"`) || !strings.Contains(rules, `"command":"cancel_goal"`) {
		t.Fatal("rules omit a goal command shape")
	}
}
