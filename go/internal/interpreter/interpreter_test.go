package interpreter

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/model"
)

const (
	explainOnly  = `{"explanation":"The autopilot is growing rice because food runway is four days.","guidance":null}`
	activateGoal = `{"explanation":"Prioritising food.","guidance":{"kind":"activate_goal","goal":"EnsureFoodSupply"}}`
	valid        = activateGoal
)

type completeFunc func(context.Context, model.Request) (model.Response, error)

func (f completeFunc) Complete(c context.Context, r model.Request) (model.Response, error) {
	return f(c, r)
}
func inputFixture() Input {
	generation := domain.GenerationSnapshot{Colony: "colony", Map: 1, Load: "load", Plan: "root", Revision: 2, Native: 4}
	return Input{UserRequest: "What are you doing about food?", Current: generation, Facts: Facts{
		Generation:      generation,
		Colony:          Colony{Tick: 100, ColonistCount: 3, Resources: []Resource{{"Steel", 120}, {"WoodLog", 300}}},
		Pawns:           []Pawn{{ID: "Thing_Human1", Label: "Bob", Colonist: true}, {ID: "Thing_Human9", Downed: true}},
		Goals:           []Goal{{ID: "goal-food", Kind: "EnsureFoodSupply", Source: domain.AutopilotGoal, Status: domain.GoalActive, Need: domain.NeedDeficit, Priority: 1}},
		PolicyResources: []string{"Silver"},
	}}
}
func clientFixture(t *testing.T, fn completeFunc) *Interpreter {
	t.Helper()
	i, err := New(Config{ContextTokens: 16384, MaxOutputTokens: 1024}, fn)
	if err != nil {
		t.Fatal(err)
	}
	return i
}
func replying(t *testing.T, text string) *Interpreter {
	t.Helper()
	return clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: text, FinishReason: model.Stop}, nil
	})
}
func assertKind(t *testing.T, err error, kind FailureKind) {
	t.Helper()
	var failure *Failure
	if !errors.As(err, &failure) || failure.Kind != kind {
		t.Fatalf("got %v want %s", err, kind)
	}
}
func TestGuidanceFromActualLocalHTTP(t *testing.T) {
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
		if !strings.Contains(request.Messages[1].Content, `"goal-food"`) || !strings.Contains(request.Messages[1].Content, `"Bob"`) {
			t.Error("facts not supplied as data")
		}
		w.Write([]byte(`{"choices":[{"index":0,"message":{"role":"assistant","content":` + string(mustJSON(activateGoal)) + `},"finish_reason":"stop"}]}`))
	}))
	defer server.Close()
	transport, err := model.NewClient(model.Config{BaseURL: server.URL + "/v1", Model: "local", Timeout: time.Second, MaxResponseBytes: 65536})
	if err != nil {
		t.Fatal(err)
	}
	defer transport.Close()
	i, err := New(Config{32768, 1024}, transport)
	if err != nil {
		t.Fatal(err)
	}
	guidance, err := i.Interpret(context.Background(), inputFixture())
	if err != nil {
		t.Fatal(err)
	}
	if guidance.Kind != ActivateGoal || guidance.ActivateGoal != domain.EnsureFoodSupplyGoal || guidance.Explanation != "Prioritising food." || !guidance.Generation.Matches(inputFixture().Current) {
		t.Fatalf("%+v", guidance)
	}
}
func mustJSON(value string) []byte { data, _ := json.Marshal(value); return data }

func TestExplainOnlyCarriesNoNudge(t *testing.T) {
	guidance, err := replying(t, explainOnly).Interpret(context.Background(), inputFixture())
	if err != nil || guidance.Kind != Explain || guidance.ActivateGoal != "" || guidance.CancelGoal != "" || guidance.PopulationPolicy.Set() || !guidance.ExpeditionPolicy.Empty() || guidance.PopulationDecision.Set() || !guidance.ResourcePolicy.Empty() {
		t.Fatalf("%+v %v", guidance, err)
	}
	if !strings.Contains(guidance.Explanation, "rice") {
		t.Fatal(guidance.Explanation)
	}
}

func TestEveryNudgeKindDecodesToItsPolicyInput(t *testing.T) {
	wrap := func(g string) string { return `{"explanation":"ok","guidance":` + g + `}` }
	for _, tc := range []struct {
		name, guidance string
		check          func(Guidance) bool
	}{
		{"cancel", `{"kind":"cancel_goal","goalId":"goal-food"}`, func(g Guidance) bool { return g.Kind == CancelGoal && g.CancelGoal == "goal-food" }},
		{"population policy", `{"kind":"set_population_policy","maximum":8,"foodDays":20}`, func(g Guidance) bool {
			return g.Kind == SetPopulationPolicy && g.PopulationPolicy.Maximum() == 8 && g.PopulationPolicy.FoodDays() == 20
		}},
		{"expedition", `{"kind":"set_expedition_policy","maximumTravelDays":3,"keepHomeDoctor":true}`, func(g Guidance) bool {
			days, ok := g.ExpeditionPolicy.MaximumTravelDays.Get()
			doctor, ok2 := g.ExpeditionPolicy.KeepHomeDoctor.Get()
			return g.Kind == SetExpeditionPolicy && ok && days == 3 && ok2 && doctor && !g.ExpeditionPolicy.MaximumCaravans.Present()
		}},
		{"decision", `{"kind":"set_population_decision","pawn":"Thing_Human9","decision":"rescue"}`, func(g Guidance) bool {
			return g.Kind == SetPopulationDecision && g.PopulationDecision.Pawn() == "Thing_Human9" && g.PopulationDecision.Decision() == domain.PopulationRescue
		}},
		{"spending", `{"kind":"set_resource_policy","resource":"Steel","spending":"stop"}`, func(g Guidance) bool {
			s, ok := g.ResourcePolicy.Spending.Get()
			return g.Kind == SetResourcePolicy && g.ResourcePolicy.Resource == "Steel" && ok && s == domain.ResourceSpendingStop && !g.ResourcePolicy.Reserve.Present()
		}},
		{"reserve on policy resource", `{"kind":"set_resource_policy","resource":"Silver","reserve":250}`, func(g Guidance) bool {
			r, ok := g.ResourcePolicy.Reserve.Get()
			return g.Kind == SetResourcePolicy && g.ResourcePolicy.Resource == "Silver" && ok && r == 250 && !g.ResourcePolicy.Spending.Present()
		}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			guidance, err := replying(t, wrap(tc.guidance)).Interpret(context.Background(), inputFixture())
			if err != nil || !tc.check(guidance) || guidance.Explanation != "ok" {
				t.Fatalf("%+v %v", guidance, err)
			}
		})
	}
}

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
		return model.Response{Text: explainOnly, FinishReason: model.Stop}, nil
	})
	input := inputFixture()
	input.Context = []string{strings.Repeat("old-context", 2000), "new-context"}
	guidance, err := i.Interpret(context.Background(), input)
	if err != nil {
		t.Fatal(err)
	}
	if guidance.Budget.DroppedContext != 1 || guidance.Budget.ChargedUnits > guidance.Budget.InputAllowance || guidance.Budget.OriginalUnits <= guidance.Budget.ChargedUnits {
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
		{"stale", func(in *Input) { in.Facts.Generation.Native++ }, StaleFacts},
		{"budget", func(in *Input) { in.UserRequest = strings.Repeat("x", 20000) }, BudgetExceeded},
		{"empty request", func(in *Input) { in.UserRequest = "  " }, InvalidInput},
		{"duplicate pawn", func(in *Input) { in.Facts.Pawns = append(in.Facts.Pawns, in.Facts.Pawns[0]) }, InvalidInput},
		{"duplicate goal", func(in *Input) { in.Facts.Goals = append(in.Facts.Goals, in.Facts.Goals[0]) }, InvalidInput},
		{"bad goal priority", func(in *Input) { in.Facts.Goals[0].Priority = 9 }, InvalidInput},
		{"bad population policy fact", func(in *Input) { in.Facts.PopulationPolicy = &PopulationPolicy{Maximum: 0, FoodDays: 1} }, InvalidInput},
		{"negative stock", func(in *Input) { in.Facts.Colony.Resources[0].Units = -1 }, InvalidInput},
		{"bad current", func(in *Input) { in.Current.Colony = ""; in.Facts.Generation.Colony = "" }, InvalidInput},
	} {
		t.Run(tc.name, func(t *testing.T) {
			input := inputFixture()
			tc.edit(&input)
			_, err := i.Interpret(context.Background(), input)
			assertKind(t, err, tc.kind)
		})
	}
}
func TestRefusesUnresolvedOrMalformedReplies(t *testing.T) {
	wrap := func(g string) string { return `{"explanation":"ok","guidance":` + g + `}` }
	for _, tc := range []struct {
		name, text string
		kind       FailureKind
	}{
		{"malformed", "{", InvalidGuidance},
		{"no explanation", `{"guidance":null}`, InvalidGuidance},
		{"blank explanation", `{"explanation":" ","guidance":null}`, InvalidGuidance},
		{"extra root", `{"explanation":"x","guidance":null,"tool_calls":[]}`, InvalidGuidance},
		{"old command shape", `{"command":"build","buildings":[]}`, InvalidGuidance},
		{"unknown kind", wrap(`{"kind":"build","defName":"Wall"}`), InvalidGuidance},
		{"research", wrap(`{"kind":"research","project":"Microelectronics"}`), InvalidGuidance},
		{"duplicate key", wrap(`{"kind":"activate_goal","goal":"EnsureFoodSupply","goal":"EnsureFoodSupply"}`), InvalidGuidance},
		{"unknown goal kind", wrap(`{"kind":"activate_goal","goal":"ConquerWorld"}`), InvalidGuidance},
		{"cancel by kind name", wrap(`{"kind":"cancel_goal","goalId":"EnsureFoodSupply"}`), UnknownFacts},
		{"cancel with extra field", wrap(`{"kind":"cancel_goal","goalId":"goal-food","revision":1}`), InvalidGuidance},
		{"population out of range", wrap(`{"kind":"set_population_policy","maximum":500,"foodDays":20}`), InvalidGuidance},
		{"population fraction", wrap(`{"kind":"set_population_policy","maximum":5.5,"foodDays":20}`), InvalidGuidance},
		{"expedition unknown limit", wrap(`{"kind":"set_expedition_policy","maximumWalkingDays":3}`), InvalidGuidance},
		{"expedition empty", wrap(`{"kind":"set_expedition_policy"}`), InvalidGuidance},
		{"expedition out of range", wrap(`{"kind":"set_expedition_policy","maximumCaravans":99}`), InvalidGuidance},
		{"unknown pawn", wrap(`{"kind":"set_population_decision","pawn":"Bob","decision":"rescue"}`), UnknownFacts},
		{"unknown decision", wrap(`{"kind":"set_population_decision","pawn":"Thing_Human9","decision":"execute"}`), InvalidGuidance},
		{"unknown resource", wrap(`{"kind":"set_resource_policy","resource":"Plasteel","spending":"stop"}`), UnknownFacts},
		{"both resource halves", wrap(`{"kind":"set_resource_policy","resource":"Steel","spending":"stop","reserve":1}`), InvalidGuidance},
		{"reserve out of range", wrap(`{"kind":"set_resource_policy","resource":"Steel","reserve":99999}`), InvalidGuidance},
		{"unknown spending", wrap(`{"kind":"set_resource_policy","resource":"Steel","spending":"hoard"}`), InvalidGuidance},
		{"too large", strings.Repeat(" ", 65537), InvalidGuidance},
	} {
		t.Run(tc.name, func(t *testing.T) {
			g, err := replying(t, tc.text).Interpret(context.Background(), inputFixture())
			assertKind(t, err, tc.kind)
			if g.Kind != "" || g.Explanation != "" {
				t.Fatal("failure exposed partial guidance")
			}
		})
	}
}
func TestModelFailureAndCancellationNeverGuide(t *testing.T) {
	for _, tc := range []struct {
		name     string
		response model.Response
		err      error
	}{{"length", model.Response{Text: valid, FinishReason: model.Length}, nil}, {"missing finish", model.Response{Text: valid}, nil}, {"refusal", model.Response{Text: valid, FinishReason: model.Stop}, model.ErrRefusal}, {"toolcalls", model.Response{}, model.ErrToolCalls}} {
		t.Run(tc.name, func(t *testing.T) {
			i := clientFixture(t, func(context.Context, model.Request) (model.Response, error) { return tc.response, tc.err })
			g, err := i.Interpret(context.Background(), inputFixture())
			assertKind(t, err, ModelFailure)
			if g.Kind != "" {
				t.Fatal("failure exposed guidance")
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
		input.Facts.Goals[0].ID = "changed"
		input.Facts.Pawns[1].ID = "changed"
		return model.Response{Text: `{"explanation":"ok","guidance":{"kind":"cancel_goal","goalId":"goal-food"}}`, FinishReason: model.Stop}, nil
	})
	guidance, err := i.Interpret(context.Background(), input)
	if err != nil || guidance.CancelGoal != "goal-food" {
		t.Fatalf("snapshot changed: %v", err)
	}
}
