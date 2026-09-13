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
	i, err := New(Config{16384, 1024, 4}, transport)
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

func TestTendAndRescueProposals(t *testing.T) {
	for _, tc := range []struct {
		command, roleA, roleB string
	}{{"tend", "doctor", "patient"}, {"rescue", "rescuer", "patient"}} {
		t.Run(tc.command, func(t *testing.T) {
			text := `{"command":"` + tc.command + `","` + tc.roleA + `":"Thing_A","` + tc.roleB + `":"Thing_B"}`
			i := clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
				return model.Response{Text: text, FinishReason: model.Stop}, nil
			})
			input := inputFixture()
			input.Facts.Pawns = []domain.PawnID{"Thing_A", "Thing_B"}
			proposal, err := i.Interpret(context.Background(), input)
			if err != nil {
				t.Fatal(err)
			}
			actions := proposal.Plan.Actions()
			if len(actions) != 1 || actions[0].ID() != "a1" {
				t.Fatal("incorrect action identity")
			}
			switch tc.command {
			case "tend":
				tend, ok := actions[0].Tend()
				if !ok || tend.Doctor() != "Thing_A" || tend.Patient() != "Thing_B" {
					t.Fatal("incorrect typed tend proposal")
				}
			case "rescue":
				rescue, ok := actions[0].Rescue()
				if !ok || rescue.Rescuer() != "Thing_A" || rescue.Patient() != "Thing_B" {
					t.Fatal("incorrect typed rescue proposal")
				}
			}
		})
	}
}

func TestTendRescueRefusesUnknownPawnOrWrongActionCount(t *testing.T) {
	response := func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: `{"command":"tend","doctor":"Thing_A","patient":"Thing_B"}`, FinishReason: model.Stop}, nil
	}
	i := clientFixture(t, response)
	input := inputFixture()
	// Neither pawn is in the observed facts.
	_, err := i.Interpret(context.Background(), input)
	assertKind(t, err, UnknownFacts)

	input = inputFixture()
	input.Facts.Pawns = []domain.PawnID{"Thing_A", "Thing_B"}
	input.ActionIDs = append(input.ActionIDs, "a2")
	i = clientFixture(t, response)
	_, err = i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidCommand)
}

func TestDraftProposal(t *testing.T) {
	i := clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: `{"command":"draft","pawn":"Thing_A"}`, FinishReason: model.Stop}, nil
	})
	input := inputFixture()
	input.Facts.Pawns = []domain.PawnID{"Thing_A"}
	proposal, err := i.Interpret(context.Background(), input)
	if err != nil {
		t.Fatal(err)
	}
	actions := proposal.Plan.Actions()
	if len(actions) != 1 || actions[0].ID() != "a1" {
		t.Fatal("incorrect draft action identity")
	}
	draft, ok := actions[0].OwnedDraft()
	if !ok || draft.Pawn() != "Thing_A" {
		t.Fatal("incorrect typed draft proposal")
	}
}

func TestDraftRefusesUnknownPawnOrWrongActionCount(t *testing.T) {
	response := func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: `{"command":"draft","pawn":"Thing_A"}`, FinishReason: model.Stop}, nil
	}
	i := clientFixture(t, response)
	input := inputFixture()
	_, err := i.Interpret(context.Background(), input)
	assertKind(t, err, UnknownFacts)

	input = inputFixture()
	input.Facts.Pawns = []domain.PawnID{"Thing_A"}
	input.ActionIDs = append(input.ActionIDs, "a2")
	i = clientFixture(t, response)
	_, err = i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidCommand)
}

func TestCaravanProposal(t *testing.T) {
	i := clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: `{"command":"caravan","crew":["Thing_A"],"cargo":[{"defName":"Silver","count":50}],"destinationTile":3}`, FinishReason: model.Stop}, nil
	})
	input := inputFixture()
	input.Facts.Pawns = []domain.PawnID{"Thing_A"}
	input.Facts.CargoDefinitions = []string{"Silver"}
	input.Facts.DestinationTiles = []int32{3}
	proposal, err := i.Interpret(context.Background(), input)
	if err != nil {
		t.Fatal(err)
	}
	actions := proposal.Plan.Actions()
	if len(actions) != 1 || actions[0].ID() != "a1" {
		t.Fatal("incorrect caravan action identity")
	}
	departure, ok := actions[0].CaravanDeparture()
	if !ok || len(departure.Crew()) != 1 || departure.Crew()[0] != "Thing_A" || departure.DestinationTile() != 3 ||
		len(departure.Cargo()) != 1 || departure.Cargo()[0].Definition != "Silver" || departure.Cargo()[0].Count != 50 {
		t.Fatal("incorrect typed caravan proposal")
	}
}

func TestCaravanRefusesUnknownFactsOrWrongActionCount(t *testing.T) {
	text := `{"command":"caravan","crew":["Thing_A"],"cargo":[{"defName":"Silver","count":50}],"destinationTile":3}`
	response := func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: text, FinishReason: model.Stop}, nil
	}
	for _, tc := range []struct {
		name string
		edit func(*Input)
	}{
		{"unknown crew", func(in *Input) { in.Facts.CargoDefinitions = []string{"Silver"}; in.Facts.DestinationTiles = []int32{3} }},
		{"unknown cargo", func(in *Input) { in.Facts.Pawns = []domain.PawnID{"Thing_A"}; in.Facts.DestinationTiles = []int32{3} }},
		{"unknown destination", func(in *Input) { in.Facts.Pawns = []domain.PawnID{"Thing_A"}; in.Facts.CargoDefinitions = []string{"Silver"} }},
	} {
		t.Run(tc.name, func(t *testing.T) {
			input := inputFixture()
			tc.edit(&input)
			i := clientFixture(t, response)
			_, err := i.Interpret(context.Background(), input)
			assertKind(t, err, UnknownFacts)
		})
	}
	input := inputFixture()
	input.Facts.Pawns = []domain.PawnID{"Thing_A"}
	input.Facts.CargoDefinitions = []string{"Silver"}
	input.Facts.DestinationTiles = []int32{3}
	input.ActionIDs = append(input.ActionIDs, "a2")
	i := clientFixture(t, response)
	_, err := i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidCommand)
}

func TestCargoAndDestinationFactsValidatedAndBounded(t *testing.T) {
	i := clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
		t.Fatal("model called")
		return model.Response{}, nil
	})
	input := inputFixture()
	input.Facts.CargoDefinitions = []string{"Silver", "Silver"}
	_, err := i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidInput)

	input = inputFixture()
	input.Facts.DestinationTiles = []int32{3, 3}
	_, err = i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidInput)

	input = inputFixture()
	input.Facts.DestinationTiles = []int32{-1}
	_, err = i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidInput)
}

func TestHusbandryProposal(t *testing.T) {
	i := clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: `{"command":"husbandry","animal":"Thing_A","method":"train","trainableDef":"Sit"}`, FinishReason: model.Stop}, nil
	})
	input := inputFixture()
	input.Facts.Pawns = []domain.PawnID{"Thing_A"}
	input.Facts.TrainableDefinitions = []string{"Sit"}
	proposal, err := i.Interpret(context.Background(), input)
	if err != nil {
		t.Fatal(err)
	}
	actions := proposal.Plan.Actions()
	if len(actions) != 1 || actions[0].ID() != "a1" {
		t.Fatal("incorrect husbandry action identity")
	}
	husbandry, ok := actions[0].Husbandry()
	if !ok || husbandry.Animal() != "Thing_A" || husbandry.Method() != domain.HusbandryTrain || husbandry.TrainableDef() != "Sit" {
		t.Fatal("incorrect typed husbandry proposal")
	}

	i = clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: `{"command":"husbandry","animal":"Thing_A","method":"slaughter"}`, FinishReason: model.Stop}, nil
	})
	input = inputFixture()
	input.Facts.Pawns = []domain.PawnID{"Thing_A"}
	proposal, err = i.Interpret(context.Background(), input)
	if err != nil {
		t.Fatal(err)
	}
	husbandry, ok = proposal.Plan.Actions()[0].Husbandry()
	if !ok || husbandry.Animal() != "Thing_A" || husbandry.Method() != domain.HusbandrySlaughter || husbandry.TrainableDef() != "" {
		t.Fatal("incorrect typed husbandry slaughter proposal")
	}
}

func TestHusbandryRefusesUnknownFactsOrWrongActionCount(t *testing.T) {
	text := `{"command":"husbandry","animal":"Thing_A","method":"train","trainableDef":"Sit"}`
	response := func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: text, FinishReason: model.Stop}, nil
	}
	for _, tc := range []struct {
		name string
		edit func(*Input)
	}{
		{"unknown animal", func(in *Input) { in.Facts.TrainableDefinitions = []string{"Sit"} }},
		{"unknown trainable def", func(in *Input) { in.Facts.Pawns = []domain.PawnID{"Thing_A"} }},
	} {
		t.Run(tc.name, func(t *testing.T) {
			input := inputFixture()
			tc.edit(&input)
			i := clientFixture(t, response)
			_, err := i.Interpret(context.Background(), input)
			assertKind(t, err, UnknownFacts)
		})
	}
	input := inputFixture()
	input.Facts.Pawns = []domain.PawnID{"Thing_A"}
	input.Facts.TrainableDefinitions = []string{"Sit"}
	input.ActionIDs = append(input.ActionIDs, "a2")
	i := clientFixture(t, response)
	_, err := i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidCommand)
}

func TestTrainableDefinitionsValidatedAndBounded(t *testing.T) {
	i := clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
		t.Fatal("model called")
		return model.Response{}, nil
	})
	input := inputFixture()
	input.Facts.TrainableDefinitions = []string{"Sit", "Sit"}
	_, err := i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidInput)
}

func TestRecoveryServiceProposal(t *testing.T) {
	i := clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: `{"command":"recover","pawn":"Thing_A","thing":"Thing_B","method":"repair"}`, FinishReason: model.Stop}, nil
	})
	input := inputFixture()
	input.Facts.Pawns = []domain.PawnID{"Thing_A"}
	input.Facts.ServiceTargets = []string{"Thing_B"}
	proposal, err := i.Interpret(context.Background(), input)
	if err != nil {
		t.Fatal(err)
	}
	actions := proposal.Plan.Actions()
	if len(actions) != 1 || actions[0].ID() != "a1" {
		t.Fatal("incorrect recovery service action identity")
	}
	service, ok := actions[0].RecoveryService()
	if !ok || service.Pawn() != "Thing_A" || service.Thing() != "Thing_B" || service.Method() != domain.RecoveryServiceRepair {
		t.Fatal("incorrect typed recovery service proposal")
	}
}

func TestRecoveryServiceRefusesUnknownFactsOrWrongActionCount(t *testing.T) {
	text := `{"command":"recover","pawn":"Thing_A","thing":"Thing_B","method":"repair"}`
	response := func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: text, FinishReason: model.Stop}, nil
	}
	for _, tc := range []struct {
		name string
		edit func(*Input)
	}{
		{"unknown pawn", func(in *Input) { in.Facts.ServiceTargets = []string{"Thing_B"} }},
		{"unknown thing", func(in *Input) { in.Facts.Pawns = []domain.PawnID{"Thing_A"} }},
	} {
		t.Run(tc.name, func(t *testing.T) {
			input := inputFixture()
			tc.edit(&input)
			i := clientFixture(t, response)
			_, err := i.Interpret(context.Background(), input)
			assertKind(t, err, UnknownFacts)
		})
	}
	input := inputFixture()
	input.Facts.Pawns = []domain.PawnID{"Thing_A"}
	input.Facts.ServiceTargets = []string{"Thing_B"}
	input.ActionIDs = append(input.ActionIDs, "a2")
	i := clientFixture(t, response)
	_, err := i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidCommand)
}

func TestServiceTargetsValidatedAndBounded(t *testing.T) {
	i := clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
		t.Fatal("model called")
		return model.Response{}, nil
	})
	input := inputFixture()
	input.Facts.ServiceTargets = []string{"Thing_B", "Thing_B"}
	_, err := i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidInput)
}

func TestBedAssignProposal(t *testing.T) {
	i := clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: `{"command":"bed_assign","pawn":"Thing_A","bed":"Thing_Bed1"}`, FinishReason: model.Stop}, nil
	})
	input := inputFixture()
	input.Facts.Pawns = []domain.PawnID{"Thing_A"}
	input.Facts.BedTargets = []string{"Thing_Bed1"}
	input.Facts.PawnBeds = []PawnBed{{Pawn: "Thing_A", Bed: "Thing_Bed0"}}
	proposal, err := i.Interpret(context.Background(), input)
	if err != nil {
		t.Fatal(err)
	}
	actions := proposal.Plan.Actions()
	if len(actions) != 1 || actions[0].ID() != "a1" {
		t.Fatal("incorrect bed_assign action identity")
	}
	assign, ok := actions[0].BedAssign()
	if !ok || assign.Pawn() != "Thing_A" || assign.Bed() != "Thing_Bed1" || assign.PreviousBed().Clear() || assign.PreviousBed().ID() != "Thing_Bed0" {
		t.Fatal("incorrect typed bed_assign proposal")
	}

	i = clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: `{"command":"bed_assign","pawn":"Thing_A","bed":"Thing_Bed1"}`, FinishReason: model.Stop}, nil
	})
	input = inputFixture()
	input.Facts.Pawns = []domain.PawnID{"Thing_A"}
	input.Facts.BedTargets = []string{"Thing_Bed1"}
	input.Facts.PawnBeds = []PawnBed{{Pawn: "Thing_A", Bed: ""}}
	proposal, err = i.Interpret(context.Background(), input)
	if err != nil {
		t.Fatal(err)
	}
	assign, ok = proposal.Plan.Actions()[0].BedAssign()
	if !ok || !assign.PreviousBed().Clear() {
		t.Fatal("incorrect typed bed_assign clear-previous proposal")
	}
}

func TestBedAssignRefusesUnknownFactsOrWrongActionCount(t *testing.T) {
	text := `{"command":"bed_assign","pawn":"Thing_A","bed":"Thing_Bed1"}`
	response := func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: text, FinishReason: model.Stop}, nil
	}
	for _, tc := range []struct {
		name string
		edit func(*Input)
	}{
		{"unknown pawn", func(in *Input) { in.Facts.BedTargets = []string{"Thing_Bed1"} }},
		{"unknown bed", func(in *Input) {
			in.Facts.Pawns = []domain.PawnID{"Thing_A"}
			in.Facts.PawnBeds = []PawnBed{{Pawn: "Thing_A", Bed: ""}}
		}},
		{"missing previous bed fact", func(in *Input) {
			in.Facts.Pawns = []domain.PawnID{"Thing_A"}
			in.Facts.BedTargets = []string{"Thing_Bed1"}
		}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			input := inputFixture()
			tc.edit(&input)
			i := clientFixture(t, response)
			_, err := i.Interpret(context.Background(), input)
			assertKind(t, err, UnknownFacts)
		})
	}
	input := inputFixture()
	input.Facts.Pawns = []domain.PawnID{"Thing_A"}
	input.Facts.BedTargets = []string{"Thing_Bed1"}
	input.Facts.PawnBeds = []PawnBed{{Pawn: "Thing_A", Bed: ""}}
	input.ActionIDs = append(input.ActionIDs, "a2")
	i := clientFixture(t, response)
	_, err := i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidCommand)
}

func TestBedFactsValidatedAndBounded(t *testing.T) {
	i := clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
		t.Fatal("model called")
		return model.Response{}, nil
	})
	input := inputFixture()
	input.Facts.BedTargets = []string{"Thing_Bed1", "Thing_Bed1"}
	_, err := i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidInput)

	input = inputFixture()
	input.Facts.PawnBeds = []PawnBed{{Pawn: "Thing_A", Bed: ""}, {Pawn: "Thing_A", Bed: ""}}
	_, err = i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidInput)

	input = inputFixture()
	input.Facts.PawnBeds = []PawnBed{{Pawn: "Thing_A", Bed: "Thing_A"}}
	_, err = i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidInput)
}

func TestPawnFactsValidatedAndBounded(t *testing.T) {
	i := clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
		t.Fatal("model called")
		return model.Response{}, nil
	})
	input := inputFixture()
	input.Facts.Pawns = []domain.PawnID{"Thing_A", "Thing_A"}
	_, err := i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidInput)

	input = inputFixture()
	input.Facts.Pawns = []domain.PawnID{""}
	_, err = i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidInput)
}
