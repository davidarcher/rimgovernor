package interpreter

import (
	"context"
	"encoding/json"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/model"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

const valid = `{"command":"build","buildings":[{"defName":"Wall","x":2,"z":3,"rotation":"north","stuff":"Granite"}]}`

type completeFunc func(context.Context, model.Request) (model.Response, error)

func (f completeFunc) Complete(c context.Context, r model.Request) (model.Response, error) {
	return f(c, r)
}
func inputFixture() Input {
	generation := domain.GenerationSnapshot{Colony: "colony", Map: 1, Load: "load", Plan: "player-plan", Revision: 2, Direction: 3, Native: 4}
	return Input{UserRequest: "Build a granite wall at (2,3).", ExplicitPlayerRequest: true, Current: generation, Facts: Snapshot{Generation: generation, Width: 20, Height: 20, Definitions: []Definition{{DefName: "Wall", Stuff: []string{"Granite"}}}, Cells: []domain.Cell{{X: 2, Z: 3}}}, ActionIDs: []domain.ActionID{"a1"}}
}
func clientFixture(t *testing.T, fn completeFunc) *Interpreter {
	t.Helper()
	i, err := New(Config{ContextTokens: 16384, MaxOutputTokens: 1024, MaxActions: 4}, fn)
	if err != nil {
		t.Fatal(err)
	}
	return i
}
func assertKind(t *testing.T, err error, kind FailureKind) {
	t.Helper()
	var failure *Failure
	if !errors.As(err, &failure) || failure.Kind != kind {
		t.Fatalf("got %v want %s", err, kind)
	}
}
func TestProposalFromActualLocalHTTP(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		var request struct {
			Messages  []model.Message `json:"messages"`
			MaxTokens int             `json:"max_tokens"`
		}
		if err := json.NewDecoder(r.Body).Decode(&request); err != nil {
			t.Error(err)
		}
		if request.MaxTokens != 1024 || request.Messages[len(request.Messages)-1].Content != inputFixture().UserRequest {
			t.Error("request or reservation changed")
		}
		response := `{"choices":[{"index":0,"message":{"role":"assistant","content":` + string(mustJSON(valid)) + `},"finish_reason":"stop"}]}`
		w.Write([]byte(response))
	}))
	defer server.Close()
	transport, err := model.NewClient(model.Config{BaseURL: server.URL + "/v1", Model: "local", Timeout: time.Second, MaxResponseBytes: 65536})
	if err != nil {
		t.Fatal(err)
	}
	defer transport.Close()
	i, err := New(Config{32768, 1024, 4}, transport)
	if err != nil {
		t.Fatal(err)
	}
	proposal, err := i.Interpret(context.Background(), inputFixture())
	if err != nil {
		t.Fatal(err)
	}
	if proposal.Plan.ID() != "player-plan" || proposal.Plan.Revision() != 2 || !proposal.Generation.Matches(inputFixture().Current) {
		t.Fatal("lost caller identity")
	}
	actions := proposal.Plan.Actions()
	b, ok := actions[0].Building()
	if !ok || b.Definition() != "Wall" || b.Stuff() != "Granite" || b.Cell() != (domain.Cell{X: 2, Z: 3}) || actions[0].ID() != "a1" {
		t.Fatal("incorrect typed proposal")
	}
}
func mustJSON(value string) []byte { data, _ := json.Marshal(value); return data }
func TestContextTrimsOnlyOptionalOldestEntries(t *testing.T) {
	i := clientFixture(t, func(_ context.Context, r model.Request) (model.Response, error) {
		if r.Messages[0].Role != model.System || r.Messages[len(r.Messages)-1].Content != inputFixture().UserRequest {
			t.Fatal("authoritative text lost")
		}
		all := ""
		for _, m := range r.Messages {
			all += m.Content
		}
		if strings.Contains(all, "old-context") || !strings.Contains(all, "new-context") {
			t.Fatal("wrong context trimmed")
		}
		return model.Response{Text: valid, FinishReason: model.Stop}, nil
	})
	input := inputFixture()
	input.Context = []string{strings.Repeat("old-context", 2000), "new-context"}
	proposal, err := i.Interpret(context.Background(), input)
	if err != nil {
		t.Fatal(err)
	}
	if proposal.Budget.DroppedContext != 1 || proposal.Budget.ChargedUnits > proposal.Budget.InputAllowance || proposal.Budget.OriginalUnits <= proposal.Budget.ChargedUnits {
		t.Fatal("incorrect budget reporting")
	}
}
func TestInvalidInputNeverCallsModel(t *testing.T) {
	i := clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
		t.Fatal("model called")
		return model.Response{}, nil
	})
	for _, tc := range []struct {
		name string
		edit func(*Input)
		kind FailureKind
	}{
		{"no authority", func(in *Input) { in.ExplicitPlayerRequest = false }, NoAuthority},
		{"stale", func(in *Input) { in.Facts.Generation.Native++ }, StaleFacts},
		{"budget", func(in *Input) { in.UserRequest = strings.Repeat("x", 20000) }, BudgetExceeded},
		{"duplicate IDs", func(in *Input) { in.ActionIDs = []domain.ActionID{"a1", "a1"} }, InvalidInput},
		{"no facts", func(in *Input) { in.Facts.Cells = nil }, InvalidInput},
	} {
		t.Run(tc.name, func(t *testing.T) {
			input := inputFixture()
			tc.edit(&input)
			_, err := i.Interpret(context.Background(), input)
			assertKind(t, err, tc.kind)
		})
	}
}
func TestRefusesUnresolvedOrMalformedCommands(t *testing.T) {
	for _, tc := range []struct {
		name, text string
		kind       FailureKind
	}{
		{"malformed", "{", InvalidCommand}, {"tool", `{"command":"tool","name":"spawn"}`, UnsupportedCommand},
		{"unknown root", strings.Replace(valid, `"command":"build"`, `"command":"build","tool_calls":[]`, 1), InvalidCommand},
		{"duplicate", strings.Replace(valid, `"x":2`, `"x":2,"x":2`, 1), InvalidCommand},
		{"case", strings.Replace(valid, `"defName"`, `"DefName"`, 1), InvalidCommand},
		{"null", strings.Replace(valid, `"stuff":"Granite"`, `"stuff":null`, 1), InvalidCommand},
		{"fraction", strings.Replace(valid, `"x":2`, `"x":2.0`, 1), InvalidCommand},
		{"unobserved replacement text", strings.Replace(valid, "Wall", `\ud800`, 1), UnknownFacts},
		{"unknown definition", strings.Replace(valid, "Wall", "Invented", 1), UnknownFacts},
		{"unknown material", strings.Replace(valid, "Granite", "Steel", 1), UnknownFacts},
		{"default material", strings.Replace(valid, "Granite", "", 1), UnknownFacts},
		{"unobserved cell", strings.Replace(valid, `"x":2`, `"x":4`, 1), UnknownFacts},
		{"map bounds", strings.Replace(valid, `"x":2`, `"x":20`, 1), UnknownFacts},
		{"rotation", strings.Replace(valid, "north", "up", 1), InvalidCommand},
		{"too large", strings.Repeat(" ", 65537), InvalidCommand},
	} {
		t.Run(tc.name, func(t *testing.T) {
			i := clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
				return model.Response{Text: tc.text, FinishReason: model.Stop}, nil
			})
			p, err := i.Interpret(context.Background(), inputFixture())
			assertKind(t, err, tc.kind)
			if len(p.Plan.Actions()) != 0 {
				t.Fatal("failure exposed partial plan")
			}
		})
	}
}
func TestModelFailureAndCancellationNeverPropose(t *testing.T) {
	for _, tc := range []struct {
		name     string
		response model.Response
		err      error
	}{{"length", model.Response{Text: valid, FinishReason: model.Length}, nil}, {"missing finish", model.Response{Text: valid}, nil}, {"refusal", model.Response{Text: valid, FinishReason: model.Stop}, model.ErrRefusal}, {"toolcalls", model.Response{}, model.ErrToolCalls}} {
		t.Run(tc.name, func(t *testing.T) {
			i := clientFixture(t, func(context.Context, model.Request) (model.Response, error) { return tc.response, tc.err })
			p, err := i.Interpret(context.Background(), inputFixture())
			assertKind(t, err, ModelFailure)
			if len(p.Plan.Actions()) != 0 {
				t.Fatal("failure exposed plan")
			}
		})
	}
	ctx, cancel := context.WithCancel(context.Background())
	i := clientFixture(t, func(ctx context.Context, _ model.Request) (model.Response, error) {
		cancel()
		<-ctx.Done()
		return model.Response{Text: valid, FinishReason: model.Stop}, nil
	})
	_, err := i.Interpret(ctx, inputFixture())
	if !errors.Is(err, context.Canceled) {
		t.Fatal(err)
	}
}

func TestCallerRefreshCannotChangeCapturedFacts(t *testing.T) {
	input := inputFixture()
	i := clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
		input.Facts.Definitions[0].Stuff[0] = "Steel"
		input.Facts.Cells[0] = domain.Cell{X: 9, Z: 9}
		input.ActionIDs[0] = "changed"
		return model.Response{Text: valid, FinishReason: model.Stop}, nil
	})
	proposal, err := i.Interpret(context.Background(), input)
	if err != nil || proposal.Plan.Actions()[0].ID() != "a1" {
		t.Fatalf("snapshot changed: %v", err)
	}
}

func TestActionCountAndExplicitDefaultMaterial(t *testing.T) {
	i := clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: valid, FinishReason: model.Stop}, nil
	})
	input := inputFixture()
	input.ActionIDs = append(input.ActionIDs, "a2")
	_, err := i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidCommand)
	i = clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: strings.Replace(valid, "Granite", "", 1), FinishReason: model.Stop}, nil
	})
	input = inputFixture()
	input.Facts.Definitions[0].AllowDefaultStuff = true
	if _, err := i.Interpret(context.Background(), input); err != nil {
		t.Fatal(err)
	}
}

func TestResearchProposal(t *testing.T) {
	i := clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: `{"command":"research","project":"Electricity"}`, FinishReason: model.Stop}, nil
	})
	input := inputFixture()
	input.Facts.ResearchProjects = []string{"Electricity"}
	proposal, err := i.Interpret(context.Background(), input)
	if err != nil {
		t.Fatal(err)
	}
	actions := proposal.Plan.Actions()
	if len(actions) != 1 || actions[0].ID() != "a1" {
		t.Fatal("incorrect research action identity")
	}
	research, ok := actions[0].ResearchSelect()
	if !ok || research.Project() != "Electricity" {
		t.Fatal("incorrect typed research proposal")
	}
}

func TestResearchRefusesUnknownProjectOrWrongActionCount(t *testing.T) {
	response := func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: `{"command":"research","project":"Electricity"}`, FinishReason: model.Stop}, nil
	}
	i := clientFixture(t, response)
	input := inputFixture()
	// No selectable research facts supplied: the requested project is unknown.
	_, err := i.Interpret(context.Background(), input)
	assertKind(t, err, UnknownFacts)

	input = inputFixture()
	input.Facts.ResearchProjects = []string{"Electricity"}
	input.ActionIDs = append(input.ActionIDs, "a2")
	i = clientFixture(t, response)
	_, err = i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidCommand)
}

func TestResearchProjectsValidatedAndBounded(t *testing.T) {
	i := clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
		t.Fatal("model called")
		return model.Response{}, nil
	})
	input := inputFixture()
	input.Facts.ResearchProjects = []string{"Electricity", "Electricity"}
	_, err := i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidInput)

	input = inputFixture()
	input.Facts.ResearchProjects = []string{""}
	_, err = i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidInput)
}

func zoneCellFacts() []domain.Cell { return []domain.Cell{{X: 0, Z: 0}, {X: 1, Z: 0}} }

// A population policy is colony configuration, not a plan of native actions:
// the proposal carries the typed policy and no plan at all.
func TestSetPopulationPolicyProposalCarriesNoPlan(t *testing.T) {
	response := func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: `{"command":"set_population_policy","maximum":12,"foodDays":30.5}`, FinishReason: model.Stop}, nil
	}
	proposal, err := clientFixture(t, response).Interpret(context.Background(), inputFixture())
	if err != nil {
		t.Fatal(err)
	}
	if !proposal.PopulationPolicy.Set() || proposal.PopulationPolicy.Maximum() != 12 || proposal.PopulationPolicy.FoodDays() != 30.5 {
		t.Fatal("incorrect typed population policy proposal", proposal.PopulationPolicy)
	}
	if len(proposal.Plan.Actions()) != 0 {
		t.Fatal("a population policy must propose no actions")
	}
	if proposal.Generation != inputFixture().Current {
		t.Fatal("population policy must resolve against the input generation")
	}
}

func TestSetPopulationPolicyRefusesOutOfRangeValues(t *testing.T) {
	for _, text := range []string{
		`{"command":"set_population_policy","maximum":0,"foodDays":30}`,
		`{"command":"set_population_policy","maximum":101,"foodDays":30}`,
		`{"command":"set_population_policy","maximum":-1,"foodDays":30}`,
		`{"command":"set_population_policy","maximum":12,"foodDays":0}`,
		`{"command":"set_population_policy","maximum":12,"foodDays":121}`,
	} {
		t.Run(text, func(t *testing.T) {
			response := func(context.Context, model.Request) (model.Response, error) {
				return model.Response{Text: text, FinishReason: model.Stop}, nil
			}
			_, err := clientFixture(t, response).Interpret(context.Background(), inputFixture())
			assertKind(t, err, InvalidCommand)
		})
	}
}

// An expedition policy is configuration too, and is carried as the partial
// patch the player asked for: the interpreter cannot see the limits in force,
// so it must not invent values for the limits the request leaves alone.
func TestSetExpeditionPolicyProposalCarriesOnlyRequestedLimits(t *testing.T) {
	response := func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: `{"command":"set_expedition_policy","maximumTravelDays":9.5,"keepHomeDoctor":false}`, FinishReason: model.Stop}, nil
	}
	proposal, err := clientFixture(t, response).Interpret(context.Background(), inputFixture())
	if err != nil {
		t.Fatal(err)
	}
	if proposal.ExpeditionPolicy.Empty() {
		t.Fatal("an expedition policy proposal must carry the request")
	}
	if value, ok := proposal.ExpeditionPolicy.MaximumTravelDays.Get(); !ok || value != 9.5 {
		t.Fatal("incorrect typed expedition policy proposal", proposal.ExpeditionPolicy)
	}
	if value, ok := proposal.ExpeditionPolicy.KeepHomeDoctor.Get(); !ok || value {
		t.Fatal("an explicit false must survive as a supplied value", proposal.ExpeditionPolicy)
	}
	// Everything the request did not name must stay absent so the store can
	// preserve whatever is already in force.
	if proposal.ExpeditionPolicy.MinimumHomeColonists.Present() || proposal.ExpeditionPolicy.RequireReturnStorage.Present() {
		t.Fatal("unrequested limits must not be invented", proposal.ExpeditionPolicy)
	}
	if len(proposal.Plan.Actions()) != 0 || proposal.PopulationPolicy.Set() {
		t.Fatal("an expedition policy must propose no actions and no other policy")
	}
	if proposal.Generation != inputFixture().Current {
		t.Fatal("expedition policy must resolve against the input generation")
	}
	// A later merge is the store's job; the proposal preserves the earlier
	// value by omission alone.
	merged, err := proposal.ExpeditionPolicy.Apply(domain.DefaultExpeditionPolicy())
	if err != nil || merged.MaximumTravelDays() != 9.5 || merged.KeepHomeDoctor() ||
		merged.MinimumHomeColonists() != 1 || !merged.RequireReturnStorage() {
		t.Fatal(merged, err)
	}
}

func TestSetExpeditionPolicyRefusesOutOfRangeValues(t *testing.T) {
	for _, text := range []string{
		`{"command":"set_expedition_policy","minimumHomeColonists":0}`,
		`{"command":"set_expedition_policy","minimumHomeColonists":101}`,
		`{"command":"set_expedition_policy","minimumHomeFoodDays":61}`,
		`{"command":"set_expedition_policy","travelFoodMarginDays":-1}`,
		`{"command":"set_expedition_policy","maximumTravelDays":0}`,
		`{"command":"set_expedition_policy","maximumTravelDays":61}`,
		`{"command":"set_expedition_policy","maximumCaravans":21}`,
		`{"command":"set_expedition_policy","minimumGoodwill":101}`,
		`{"command":"set_expedition_policy","minimumDestinationTemperature":51}`,
		`{"command":"set_expedition_policy","maximumDestinationTemperature":-51}`,
		// Both ends supplied at once are cross-checked here, where both are
		// known; a lone end is cross-checked at merge time instead.
		`{"command":"set_expedition_policy","minimumDestinationTemperature":40,"maximumDestinationTemperature":10}`,
	} {
		t.Run(text, func(t *testing.T) {
			response := func(context.Context, model.Request) (model.Response, error) {
				return model.Response{Text: text, FinishReason: model.Stop}, nil
			}
			_, err := clientFixture(t, response).Interpret(context.Background(), inputFixture())
			assertKind(t, err, InvalidCommand)
		})
	}
}

// A per-pawn population decision proposes no actions either, but unlike the
// two policies it names an individual, so the pawn is bounded against facts.
func TestSetPopulationDecisionProposalCarriesNoPlan(t *testing.T) {
	response := func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: `{"command":"set_population_decision","pawn":"Thing_A","decision":"capture"}`, FinishReason: model.Stop}, nil
	}
	input := inputFixture()
	input.Facts.Pawns = []domain.PawnID{"Thing_A", "Thing_B"}
	proposal, err := clientFixture(t, response).Interpret(context.Background(), input)
	if err != nil {
		t.Fatal(err)
	}
	if !proposal.PopulationDecision.Set() || proposal.PopulationDecision.Pawn() != "Thing_A" ||
		proposal.PopulationDecision.Decision() != domain.PopulationCapture {
		t.Fatal("incorrect typed population decision proposal", proposal.PopulationDecision)
	}
	if len(proposal.Plan.Actions()) != 0 || proposal.PopulationPolicy.Set() || !proposal.ExpeditionPolicy.Empty() {
		t.Fatal("a population decision must propose no actions and no policy")
	}
	if proposal.Generation != input.Current {
		t.Fatal("population decision must resolve against the input generation")
	}
}

func TestSetPopulationDecisionBoundsPawnAndDecision(t *testing.T) {
	input := inputFixture()
	input.Facts.Pawns = []domain.PawnID{"Thing_A"}
	unknown := func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: `{"command":"set_population_decision","pawn":"Thing_Missing","decision":"rescue"}`, FinishReason: model.Stop}, nil
	}
	_, err := clientFixture(t, unknown).Interpret(context.Background(), input)
	assertKind(t, err, UnknownFacts)

	unsupported := func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: `{"command":"set_population_decision","pawn":"Thing_A","decision":"release"}`, FinishReason: model.Stop}, nil
	}
	_, err = clientFixture(t, unsupported).Interpret(context.Background(), input)
	assertKind(t, err, InvalidCommand)
}

func TestResourcePolicyProposalsCarryNoPlan(t *testing.T) {
	input := inputFixture()
	input.Facts.ResourceDefinitions = []string{"Steel", "WoodLog"}
	spending := func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: `{"command":"modify_resource_policy","resource":"Steel","spending":"defense_only"}`, FinishReason: model.Stop}, nil
	}
	proposal, err := clientFixture(t, spending).Interpret(context.Background(), input)
	if err != nil {
		t.Fatal(err)
	}
	value, ok := proposal.ResourcePolicy.Spending.Get()
	if proposal.ResourcePolicy.Resource != "Steel" || !ok || value != domain.ResourceSpendingDefenseOnly || proposal.ResourcePolicy.Reserve.Present() {
		t.Fatal("incorrect typed resource spending proposal", proposal.ResourcePolicy)
	}
	if len(proposal.Plan.Actions()) != 0 || proposal.PopulationPolicy.Set() || !proposal.ExpeditionPolicy.Empty() || proposal.PopulationDecision.Set() {
		t.Fatal("a resource policy must propose no actions and no other policy")
	}
	if proposal.Generation != input.Current {
		t.Fatal("resource policy must resolve against the input generation")
	}

	reserve := func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: `{"command":"set_resource_reserve","resource":"WoodLog","reserve":250}`, FinishReason: model.Stop}, nil
	}
	proposal, err = clientFixture(t, reserve).Interpret(context.Background(), input)
	if err != nil {
		t.Fatal(err)
	}
	amount, ok := proposal.ResourcePolicy.Reserve.Get()
	if proposal.ResourcePolicy.Resource != "WoodLog" || !ok || amount != 250 || proposal.ResourcePolicy.Spending.Present() {
		t.Fatal("incorrect typed resource reserve proposal", proposal.ResourcePolicy)
	}
}

func TestResourcePolicyBoundsResourceAndRange(t *testing.T) {
	input := inputFixture()
	input.Facts.ResourceDefinitions = []string{"Steel"}
	for _, text := range []string{
		`{"command":"modify_resource_policy","resource":"Plasteel","spending":"stop"}`,
		`{"command":"set_resource_reserve","resource":"Plasteel","reserve":10}`,
	} {
		response := func(context.Context, model.Request) (model.Response, error) {
			return model.Response{Text: text, FinishReason: model.Stop}, nil
		}
		_, err := clientFixture(t, response).Interpret(context.Background(), input)
		assertKind(t, err, UnknownFacts)
	}
	for _, text := range []string{
		`{"command":"modify_resource_policy","resource":"Steel","spending":"hoard"}`,
		`{"command":"set_resource_reserve","resource":"Steel","reserve":-1}`,
		`{"command":"set_resource_reserve","resource":"Steel","reserve":10001}`,
	} {
		response := func(context.Context, model.Request) (model.Response, error) {
			return model.Response{Text: text, FinishReason: model.Stop}, nil
		}
		_, err := clientFixture(t, response).Interpret(context.Background(), input)
		assertKind(t, err, InvalidCommand)
	}
}

func TestResourcePolicyFactsValidatedAndBounded(t *testing.T) {
	i := clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
		t.Fatal("model called")
		return model.Response{}, nil
	})
	input := inputFixture()
	input.Facts.ResourceDefinitions = []string{"Steel", "Steel"}
	_, err := i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidInput)

	input = inputFixture()
	input.Facts.ResourceDefinitions = []string{" "}
	_, err = i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidInput)
}
