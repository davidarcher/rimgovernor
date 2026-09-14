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

func TestHoldCaravanProposal(t *testing.T) {
	i := clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: `{"command":"hold_caravan","caravan":"Caravan_A"}`, FinishReason: model.Stop}, nil
	})
	input := inputFixture()
	input.Facts.Caravans = []domain.CaravanID{"Caravan_A"}
	proposal, err := i.Interpret(context.Background(), input)
	if err != nil {
		t.Fatal(err)
	}
	actions := proposal.Plan.Actions()
	if len(actions) != 1 || actions[0].ID() != "a1" {
		t.Fatal("incorrect hold_caravan action identity")
	}
	travel, ok := actions[0].TravelCaravan()
	if !ok || travel.Caravan() != "Caravan_A" || travel.Kind() != domain.TravelStop || travel.DestinationTile() != -1 {
		t.Fatal("incorrect typed hold_caravan proposal", travel)
	}
}

func TestHoldCaravanRefusesUnknownFactsOrWrongActionCount(t *testing.T) {
	text := `{"command":"hold_caravan","caravan":"Caravan_A"}`
	response := func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: text, FinishReason: model.Stop}, nil
	}
	input := inputFixture()
	i := clientFixture(t, response)
	_, err := i.Interpret(context.Background(), input)
	assertKind(t, err, UnknownFacts)

	input = inputFixture()
	input.Facts.Caravans = []domain.CaravanID{"Caravan_A"}
	input.ActionIDs = append(input.ActionIDs, "a2")
	i = clientFixture(t, response)
	_, err = i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidCommand)
}

func TestRouteCaravanProposal(t *testing.T) {
	for _, tc := range []struct {
		name string
		text string
		kind domain.TravelKind
		tile int32
	}{
		{"move", `{"command":"route_caravan","caravan":"Caravan_A","destinationTile":3,"returnHome":false,"visitSettlement":false}`, domain.TravelMove, 3},
		{"visit", `{"command":"route_caravan","caravan":"Caravan_A","destinationTile":3,"returnHome":false,"visitSettlement":true}`, domain.TravelVisit, 3},
		{"return home", `{"command":"route_caravan","caravan":"Caravan_A","destinationTile":null,"returnHome":true,"visitSettlement":false}`, domain.TravelReturnHome, -1},
	} {
		t.Run(tc.name, func(t *testing.T) {
			i := clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
				return model.Response{Text: tc.text, FinishReason: model.Stop}, nil
			})
			input := inputFixture()
			input.Facts.Caravans = []domain.CaravanID{"Caravan_A"}
			input.Facts.DestinationTiles = []int32{3}
			proposal, err := i.Interpret(context.Background(), input)
			if err != nil {
				t.Fatal(err)
			}
			actions := proposal.Plan.Actions()
			if len(actions) != 1 || actions[0].ID() != "a1" {
				t.Fatal("incorrect route_caravan action identity")
			}
			travel, ok := actions[0].TravelCaravan()
			if !ok || travel.Caravan() != "Caravan_A" || travel.Kind() != tc.kind || travel.DestinationTile() != tc.tile {
				t.Fatal("incorrect typed route_caravan proposal", travel)
			}
		})
	}
}

func TestRouteCaravanRefusesUnknownFactsOrWrongActionCount(t *testing.T) {
	text := `{"command":"route_caravan","caravan":"Caravan_A","destinationTile":3,"returnHome":false,"visitSettlement":false}`
	response := func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: text, FinishReason: model.Stop}, nil
	}
	for _, tc := range []struct {
		name string
		edit func(*Input)
	}{
		{"unknown caravan", func(in *Input) { in.Facts.DestinationTiles = []int32{3} }},
		{"unknown destination", func(in *Input) { in.Facts.Caravans = []domain.CaravanID{"Caravan_A"} }},
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
	input.Facts.Caravans = []domain.CaravanID{"Caravan_A"}
	input.Facts.DestinationTiles = []int32{3}
	input.ActionIDs = append(input.ActionIDs, "a2")
	i := clientFixture(t, response)
	_, err := i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidCommand)
}

func TestAcceptQuestProposal(t *testing.T) {
	i := clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: `{"command":"accept_quest","quest":"Quest_A","accepterPawn":"Thing_A","rewardChoice":0}`, FinishReason: model.Stop}, nil
	})
	input := inputFixture()
	input.Facts.QuestAcceptOptions = []QuestAcceptOption{{Quest: "Quest_A", AccepterPawn: "Thing_A", RewardChoice: 0}}
	proposal, err := i.Interpret(context.Background(), input)
	if err != nil {
		t.Fatal(err)
	}
	actions := proposal.Plan.Actions()
	if len(actions) != 1 || actions[0].ID() != "a1" {
		t.Fatal("incorrect accept_quest action identity")
	}
	accept, ok := actions[0].QuestAccept()
	if !ok || accept.Quest() != "Quest_A" || accept.AccepterPawn() != "Thing_A" || accept.RewardChoice() != 0 {
		t.Fatal("incorrect typed accept_quest proposal", accept)
	}
}

func TestAcceptQuestRefusesUnknownFactsOrWrongActionCount(t *testing.T) {
	text := `{"command":"accept_quest","quest":"Quest_A","accepterPawn":"Thing_A","rewardChoice":0}`
	response := func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: text, FinishReason: model.Stop}, nil
	}
	for _, tc := range []struct {
		name string
		edit func(*Input)
	}{
		{"no options at all", func(in *Input) {}},
		{"wrong pawn", func(in *Input) {
			in.Facts.QuestAcceptOptions = []QuestAcceptOption{{Quest: "Quest_A", AccepterPawn: "Thing_B", RewardChoice: 0}}
		}},
		{"wrong reward", func(in *Input) {
			in.Facts.QuestAcceptOptions = []QuestAcceptOption{{Quest: "Quest_A", AccepterPawn: "Thing_A", RewardChoice: 1}}
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
	input.Facts.QuestAcceptOptions = []QuestAcceptOption{{Quest: "Quest_A", AccepterPawn: "Thing_A", RewardChoice: 0}}
	input.ActionIDs = append(input.ActionIDs, "a2")
	i := clientFixture(t, response)
	_, err := i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidCommand)
}

func TestQuestAcceptOptionsValidatedAndBounded(t *testing.T) {
	i := clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
		t.Fatal("model called")
		return model.Response{}, nil
	})
	input := inputFixture()
	input.Facts.QuestAcceptOptions = []QuestAcceptOption{
		{Quest: "Quest_A", AccepterPawn: "Thing_A", RewardChoice: 0},
		{Quest: "Quest_A", AccepterPawn: "Thing_A", RewardChoice: 0},
	}
	_, err := i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidInput)

	input = inputFixture()
	input.Facts.QuestAcceptOptions = []QuestAcceptOption{{Quest: "", AccepterPawn: "Thing_A", RewardChoice: 0}}
	_, err = i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidInput)
}

func TestFulfillQuestProposal(t *testing.T) {
	i := clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: `{"command":"fulfill_quest","quest":"Quest_A","caravan":"Caravan_A","crew":["Thing_A"]}`, FinishReason: model.Stop}, nil
	})
	input := inputFixture()
	input.Facts.FulfillableQuests = []domain.QuestID{"Quest_A"}
	input.Facts.Caravans = []domain.CaravanID{"Caravan_A"}
	input.Facts.Pawns = []domain.PawnID{"Thing_A"}
	proposal, err := i.Interpret(context.Background(), input)
	if err != nil {
		t.Fatal(err)
	}
	actions := proposal.Plan.Actions()
	if len(actions) != 1 || actions[0].ID() != "a1" {
		t.Fatal("incorrect fulfill_quest action identity")
	}
	fulfill, ok := actions[0].QuestFulfill()
	if !ok || fulfill.Quest() != "Quest_A" || fulfill.Caravan() != "Caravan_A" || len(fulfill.CrewIDs()) != 1 || fulfill.CrewIDs()[0] != "Thing_A" {
		t.Fatal("incorrect typed fulfill_quest proposal", fulfill)
	}
}

func TestFulfillQuestRefusesUnknownFactsOrWrongActionCount(t *testing.T) {
	text := `{"command":"fulfill_quest","quest":"Quest_A","caravan":"Caravan_A","crew":["Thing_A"]}`
	response := func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: text, FinishReason: model.Stop}, nil
	}
	for _, tc := range []struct {
		name string
		edit func(*Input)
	}{
		{"unknown quest", func(in *Input) {
			in.Facts.Caravans = []domain.CaravanID{"Caravan_A"}
			in.Facts.Pawns = []domain.PawnID{"Thing_A"}
		}},
		{"unknown caravan", func(in *Input) {
			in.Facts.FulfillableQuests = []domain.QuestID{"Quest_A"}
			in.Facts.Pawns = []domain.PawnID{"Thing_A"}
		}},
		{"unknown crew", func(in *Input) {
			in.Facts.FulfillableQuests = []domain.QuestID{"Quest_A"}
			in.Facts.Caravans = []domain.CaravanID{"Caravan_A"}
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
	input.Facts.FulfillableQuests = []domain.QuestID{"Quest_A"}
	input.Facts.Caravans = []domain.CaravanID{"Caravan_A"}
	input.Facts.Pawns = []domain.PawnID{"Thing_A"}
	input.ActionIDs = append(input.ActionIDs, "a2")
	i := clientFixture(t, response)
	_, err := i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidCommand)
}

func TestGiftSettlementProposal(t *testing.T) {
	i := clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: `{"command":"gift_settlement","caravan":"Caravan_A","settlement":"Settlement_A","faction":"Faction_A","crew":["Thing_A"],"silver":100}`, FinishReason: model.Stop}, nil
	})
	input := inputFixture()
	input.Facts.GiftTargets = []GiftTarget{{Caravan: "Caravan_A", Settlement: "Settlement_A", Faction: "Faction_A"}}
	input.Facts.Pawns = []domain.PawnID{"Thing_A"}
	proposal, err := i.Interpret(context.Background(), input)
	if err != nil {
		t.Fatal(err)
	}
	actions := proposal.Plan.Actions()
	if len(actions) != 1 || actions[0].ID() != "a1" {
		t.Fatal("incorrect gift_settlement action identity")
	}
	gift, ok := actions[0].SettlementGift()
	if !ok || gift.Caravan() != "Caravan_A" || gift.Settlement() != "Settlement_A" || gift.Faction() != "Faction_A" || gift.Silver() != 100 || len(gift.CrewIDs()) != 1 || gift.CrewIDs()[0] != "Thing_A" {
		t.Fatal("incorrect typed gift_settlement proposal", gift)
	}
}

func TestGiftSettlementRefusesUnknownFactsOrWrongActionCount(t *testing.T) {
	text := `{"command":"gift_settlement","caravan":"Caravan_A","settlement":"Settlement_A","faction":"Faction_A","crew":["Thing_A"],"silver":100}`
	response := func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: text, FinishReason: model.Stop}, nil
	}
	for _, tc := range []struct {
		name string
		edit func(*Input)
	}{
		{"unknown target", func(in *Input) { in.Facts.Pawns = []domain.PawnID{"Thing_A"} }},
		{"unknown crew", func(in *Input) {
			in.Facts.GiftTargets = []GiftTarget{{Caravan: "Caravan_A", Settlement: "Settlement_A", Faction: "Faction_A"}}
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
	input.Facts.GiftTargets = []GiftTarget{{Caravan: "Caravan_A", Settlement: "Settlement_A", Faction: "Faction_A"}}
	input.Facts.Pawns = []domain.PawnID{"Thing_A"}
	input.ActionIDs = append(input.ActionIDs, "a2")
	i := clientFixture(t, response)
	_, err := i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidCommand)
}

func TestGiftTargetsValidatedAndBounded(t *testing.T) {
	i := clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
		t.Fatal("model called")
		return model.Response{}, nil
	})
	input := inputFixture()
	input.Facts.GiftTargets = []GiftTarget{
		{Caravan: "Caravan_A", Settlement: "Settlement_A", Faction: "Faction_A"},
		{Caravan: "Caravan_A", Settlement: "Settlement_A", Faction: "Faction_A"},
	}
	_, err := i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidInput)

	input = inputFixture()
	input.Facts.GiftTargets = []GiftTarget{{Caravan: "", Settlement: "Settlement_A", Faction: "Faction_A"}}
	_, err = i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidInput)
}

func zoneCellFacts() []domain.Cell { return []domain.Cell{{X: 0, Z: 0}, {X: 1, Z: 0}} }

func TestCreateZoneGrowingProposal(t *testing.T) {
	i := clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: `{"command":"create_zone","zoneKind":"growing","crop":"Rice","cells":[{"x":0,"z":0},{"x":1,"z":0}]}`, FinishReason: model.Stop}, nil
	})
	input := inputFixture()
	input.Facts.Cells = zoneCellFacts()
	input.Facts.CropDefinitions = []string{"Rice"}
	proposal, err := i.Interpret(context.Background(), input)
	if err != nil {
		t.Fatal(err)
	}
	actions := proposal.Plan.Actions()
	if len(actions) != 1 || actions[0].ID() != "a1" {
		t.Fatal("incorrect create_zone action identity")
	}
	zone, ok := actions[0].ZoneCreate()
	if !ok || zone.Kind() != domain.GrowingZone || zone.Crop() != "Rice" || len(zone.Cells()) != 2 {
		t.Fatal("incorrect typed create_zone proposal", zone)
	}
}

func TestCreateZoneStockpileProposals(t *testing.T) {
	food := func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: `{"command":"create_zone","zoneKind":"stockpile","preset":"food","priority":"important","cells":[{"x":0,"z":0},{"x":1,"z":0}]}`, FinishReason: model.Stop}, nil
	}
	input := inputFixture()
	input.Facts.Cells = zoneCellFacts()
	proposal, err := clientFixture(t, food).Interpret(context.Background(), input)
	if err != nil {
		t.Fatal(err)
	}
	zone, ok := proposal.Plan.Actions()[0].ZoneCreate()
	if !ok || zone.Kind() != domain.StockpileZone || zone.Preset() != domain.FoodPreset || zone.Priority() != domain.ImportantPriority {
		t.Fatal("incorrect typed food stockpile proposal", zone)
	}

	nothing := func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: `{"command":"create_zone","zoneKind":"stockpile","preset":"nothing","priority":"important","allow":["Silver"],"cells":[{"x":0,"z":0},{"x":1,"z":0}]}`, FinishReason: model.Stop}, nil
	}
	input = inputFixture()
	input.Facts.Cells = zoneCellFacts()
	input.Facts.StockpileDefinitions = []string{"Silver"}
	proposal, err = clientFixture(t, nothing).Interpret(context.Background(), input)
	if err != nil {
		t.Fatal(err)
	}
	zone, ok = proposal.Plan.Actions()[0].ZoneCreate()
	if !ok || zone.Preset() != domain.NothingPreset || len(zone.Allow()) != 1 || zone.Allow()[0] != "Silver" {
		t.Fatal("incorrect typed allow-listed stockpile proposal", zone)
	}
}

func TestCreateZoneRefusesUnknownFactsOrWrongActionCount(t *testing.T) {
	growing := `{"command":"create_zone","zoneKind":"growing","crop":"Rice","cells":[{"x":0,"z":0},{"x":1,"z":0}]}`
	nothing := `{"command":"create_zone","zoneKind":"stockpile","preset":"nothing","priority":"important","allow":["Silver"],"cells":[{"x":0,"z":0},{"x":1,"z":0}]}`
	for _, tc := range []struct {
		name string
		text string
		edit func(*Input)
	}{
		{"unobserved cell", growing, func(in *Input) { in.Facts.CropDefinitions = []string{"Rice"} }},
		{"unknown crop", growing, func(in *Input) { in.Facts.Cells = zoneCellFacts() }},
		{"unknown allow-list definition", nothing, func(in *Input) { in.Facts.Cells = zoneCellFacts() }},
	} {
		t.Run(tc.name, func(t *testing.T) {
			response := func(context.Context, model.Request) (model.Response, error) {
				return model.Response{Text: tc.text, FinishReason: model.Stop}, nil
			}
			input := inputFixture()
			tc.edit(&input)
			_, err := clientFixture(t, response).Interpret(context.Background(), input)
			assertKind(t, err, UnknownFacts)
		})
	}
	response := func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: growing, FinishReason: model.Stop}, nil
	}
	input := inputFixture()
	input.Facts.Cells = zoneCellFacts()
	input.Facts.CropDefinitions = []string{"Rice"}
	input.ActionIDs = append(input.ActionIDs, "a2")
	_, err := clientFixture(t, response).Interpret(context.Background(), input)
	assertKind(t, err, InvalidCommand)
}

func TestCreateZoneFactsValidatedAndBounded(t *testing.T) {
	i := clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
		t.Fatal("model called")
		return model.Response{}, nil
	})
	input := inputFixture()
	input.Facts.CropDefinitions = []string{"Rice", "Rice"}
	_, err := i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidInput)

	input = inputFixture()
	input.Facts.StockpileDefinitions = []string{"Silver", "Silver"}
	_, err = i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidInput)
}

func TestEditZoneAddRemoveDeleteProposals(t *testing.T) {
	add := func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: `{"command":"edit_zone","zoneId":"Zone_A","operation":"add","cells":[{"x":0,"z":0},{"x":1,"z":0}]}`, FinishReason: model.Stop}, nil
	}
	input := inputFixture()
	input.Facts.Cells = zoneCellFacts()
	input.Facts.ObservedZones = []ZoneEditFact{{ZoneID: "Zone_A", Token: "token-1"}}
	proposal, err := clientFixture(t, add).Interpret(context.Background(), input)
	if err != nil {
		t.Fatal(err)
	}
	actions := proposal.Plan.Actions()
	if len(actions) != 1 || actions[0].ID() != "a1" {
		t.Fatal("incorrect edit_zone action identity")
	}
	edit, ok := actions[0].ZoneEdit()
	if !ok || edit.ZoneID() != "Zone_A" || edit.BeforeToken() != "token-1" || edit.Op() != domain.ZoneEditAdd || len(edit.Cells()) != 2 {
		t.Fatal("incorrect typed edit_zone add proposal", edit)
	}

	remove := func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: `{"command":"edit_zone","zoneId":"Zone_A","operation":"remove","cells":[{"x":0,"z":0}]}`, FinishReason: model.Stop}, nil
	}
	input = inputFixture()
	input.Facts.Cells = zoneCellFacts()
	input.Facts.ObservedZones = []ZoneEditFact{{ZoneID: "Zone_A", Token: "token-1"}}
	proposal, err = clientFixture(t, remove).Interpret(context.Background(), input)
	if err != nil {
		t.Fatal(err)
	}
	edit, ok = proposal.Plan.Actions()[0].ZoneEdit()
	if !ok || edit.Op() != domain.ZoneEditRemove || len(edit.Cells()) != 1 {
		t.Fatal("incorrect typed edit_zone remove proposal", edit)
	}

	del := func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: `{"command":"edit_zone","zoneId":"Zone_A","operation":"delete"}`, FinishReason: model.Stop}, nil
	}
	input = inputFixture()
	input.Facts.ObservedZones = []ZoneEditFact{{ZoneID: "Zone_A", Token: "token-1"}}
	proposal, err = clientFixture(t, del).Interpret(context.Background(), input)
	if err != nil {
		t.Fatal(err)
	}
	edit, ok = proposal.Plan.Actions()[0].ZoneEdit()
	if !ok || edit.Op() != domain.ZoneEditDelete || len(edit.Cells()) != 0 {
		t.Fatal("incorrect typed edit_zone delete proposal", edit)
	}
}

func TestEditZoneRefusesUnknownFactsOrWrongActionCount(t *testing.T) {
	add := `{"command":"edit_zone","zoneId":"Zone_A","operation":"add","cells":[{"x":0,"z":0},{"x":1,"z":0}]}`
	del := `{"command":"edit_zone","zoneId":"Zone_A","operation":"delete"}`
	for _, tc := range []struct {
		name string
		text string
		edit func(*Input)
	}{
		{"unknown zone", add, func(in *Input) { in.Facts.Cells = zoneCellFacts() }},
		{"unobserved cell", add, func(in *Input) { in.Facts.ObservedZones = []ZoneEditFact{{ZoneID: "Zone_A", Token: "token-1"}} }},
		{"unknown zone delete", del, func(in *Input) {}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			response := func(context.Context, model.Request) (model.Response, error) {
				return model.Response{Text: tc.text, FinishReason: model.Stop}, nil
			}
			input := inputFixture()
			tc.edit(&input)
			_, err := clientFixture(t, response).Interpret(context.Background(), input)
			assertKind(t, err, UnknownFacts)
		})
	}
	response := func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: del, FinishReason: model.Stop}, nil
	}
	input := inputFixture()
	input.Facts.ObservedZones = []ZoneEditFact{{ZoneID: "Zone_A", Token: "token-1"}}
	input.ActionIDs = append(input.ActionIDs, "a2")
	_, err := clientFixture(t, response).Interpret(context.Background(), input)
	assertKind(t, err, InvalidCommand)
}

func TestEditZoneFactsValidatedAndBounded(t *testing.T) {
	i := clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
		t.Fatal("model called")
		return model.Response{}, nil
	})
	input := inputFixture()
	input.Facts.ObservedZones = []ZoneEditFact{{ZoneID: "Zone_A", Token: "token-1"}, {ZoneID: "Zone_A", Token: "token-2"}}
	_, err := i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidInput)

	input = inputFixture()
	input.Facts.ObservedZones = []ZoneEditFact{{ZoneID: "", Token: "token-1"}}
	_, err = i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidInput)
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

func TestMoveProposal(t *testing.T) {
	i := clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: `{"command":"move_pawn","pawn":"Thing_A","x":2,"z":3}`, FinishReason: model.Stop}, nil
	})
	input := inputFixture()
	input.Facts.Pawns = []domain.PawnID{"Thing_A"}
	input.ActionIDs = append(input.ActionIDs, "a2")
	proposal, err := i.Interpret(context.Background(), input)
	if err != nil {
		t.Fatal(err)
	}
	actions := proposal.Plan.Actions()
	if len(actions) != 2 || actions[0].ID() != "a1" || actions[1].ID() != "a2" {
		t.Fatal("incorrect move_pawn action identities")
	}
	draft, ok := actions[0].OwnedDraft()
	if !ok || draft.Pawn() != "Thing_A" {
		t.Fatal("incorrect typed implicit draft proposal")
	}
	movement, ok := actions[1].Movement()
	if !ok || movement.Pawn() != "Thing_A" || movement.Destination() != (domain.Cell{X: 2, Z: 3}) || movement.DraftAction() != "a1" {
		t.Fatal("incorrect typed movement proposal")
	}
}

func TestMoveRefusesUnknownFactsOrWrongActionCount(t *testing.T) {
	text := `{"command":"move_pawn","pawn":"Thing_A","x":2,"z":3}`
	response := func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: text, FinishReason: model.Stop}, nil
	}
	for _, tc := range []struct {
		name string
		edit func(*Input)
	}{
		{"unknown pawn", func(in *Input) {}},
		{"unobserved destination", func(in *Input) {
			in.Facts.Pawns = []domain.PawnID{"Thing_A"}
			in.Facts.Cells = []domain.Cell{{X: 5, Z: 5}}
		}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			input := inputFixture()
			input.ActionIDs = append(input.ActionIDs, "a2")
			tc.edit(&input)
			i := clientFixture(t, response)
			_, err := i.Interpret(context.Background(), input)
			assertKind(t, err, UnknownFacts)
		})
	}
	input := inputFixture()
	input.Facts.Pawns = []domain.PawnID{"Thing_A"}
	i := clientFixture(t, response)
	_, err := i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidCommand)
}

func TestSetBuildingTemperatureProposal(t *testing.T) {
	i := clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: `{"command":"set_building_temperature","thing":"Thing_Heater1","celsius":21}`, FinishReason: model.Stop}, nil
	})
	input := inputFixture()
	input.Facts.BuildingTemperatures = []BuildingTemperatureFact{{Thing: "Thing_Heater1", Token: "temp-token-0"}}
	proposal, err := i.Interpret(context.Background(), input)
	if err != nil {
		t.Fatal(err)
	}
	actions := proposal.Plan.Actions()
	if len(actions) != 1 || actions[0].ID() != "a1" {
		t.Fatal("incorrect set_building_temperature action identity")
	}
	temperature, ok := actions[0].BuildingTemperature()
	if !ok || temperature.Thing() != "Thing_Heater1" || temperature.Celsius() != 21 || temperature.BeforeToken() != "temp-token-0" {
		t.Fatal("incorrect typed set_building_temperature proposal")
	}
}

func TestSetBuildingTemperatureRefusesUnknownFactsOrWrongActionCount(t *testing.T) {
	text := `{"command":"set_building_temperature","thing":"Thing_Heater1","celsius":21}`
	response := func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: text, FinishReason: model.Stop}, nil
	}
	input := inputFixture()
	i := clientFixture(t, response)
	_, err := i.Interpret(context.Background(), input)
	assertKind(t, err, UnknownFacts)

	input = inputFixture()
	input.Facts.BuildingTemperatures = []BuildingTemperatureFact{{Thing: "Thing_Heater1", Token: "temp-token-0"}}
	input.ActionIDs = append(input.ActionIDs, "a2")
	i = clientFixture(t, response)
	_, err = i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidCommand)
}

func TestSetBuildingTemperatureRefusesOutOfRange(t *testing.T) {
	i := clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: `{"command":"set_building_temperature","thing":"Thing_Heater1","celsius":5000}`, FinishReason: model.Stop}, nil
	})
	input := inputFixture()
	input.Facts.BuildingTemperatures = []BuildingTemperatureFact{{Thing: "Thing_Heater1", Token: "temp-token-0"}}
	_, err := i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidCommand)
}

func TestBuildingTemperatureFactsValidatedAndBounded(t *testing.T) {
	i := clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
		t.Fatal("model called")
		return model.Response{}, nil
	})
	input := inputFixture()
	input.Facts.BuildingTemperatures = []BuildingTemperatureFact{{Thing: "Thing_Heater1", Token: "temp-token-0"}, {Thing: "Thing_Heater1", Token: "temp-token-1"}}
	_, err := i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidInput)
}

func TestSurgeryProposal(t *testing.T) {
	i := clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: `{"command":"request_surgery","patient":"Thing_A","recipe":"RemoveBodyPart","part":3}`, FinishReason: model.Stop}, nil
	})
	input := inputFixture()
	input.Facts.Pawns = []domain.PawnID{"Thing_A"}
	input.Facts.SurgeryOptions = []SurgeryOption{{Patient: "Thing_A", Recipe: "RemoveBodyPart", Part: 3}}
	proposal, err := i.Interpret(context.Background(), input)
	if err != nil {
		t.Fatal(err)
	}
	actions := proposal.Plan.Actions()
	if len(actions) != 1 || actions[0].ID() != "a1" {
		t.Fatal("incorrect request_surgery action identity")
	}
	surgery, ok := actions[0].Surgery()
	if !ok || surgery.Patient() != "Thing_A" || surgery.Recipe() != "RemoveBodyPart" || surgery.Part() != 3 {
		t.Fatal("incorrect typed surgery proposal")
	}
}

func TestSurgeryRefusesUnknownFactsOrWrongActionCount(t *testing.T) {
	text := `{"command":"request_surgery","patient":"Thing_A","recipe":"RemoveBodyPart","part":3}`
	response := func(context.Context, model.Request) (model.Response, error) {
		return model.Response{Text: text, FinishReason: model.Stop}, nil
	}
	for _, tc := range []struct {
		name string
		edit func(*Input)
	}{
		{"no options at all", func(in *Input) {}},
		{"wrong recipe", func(in *Input) {
			in.Facts.SurgeryOptions = []SurgeryOption{{Patient: "Thing_A", Recipe: "InstallPegLeg", Part: 3}}
		}},
		{"wrong part", func(in *Input) {
			in.Facts.SurgeryOptions = []SurgeryOption{{Patient: "Thing_A", Recipe: "RemoveBodyPart", Part: 2}}
		}},
		{"wrong patient", func(in *Input) {
			in.Facts.SurgeryOptions = []SurgeryOption{{Patient: "Thing_B", Recipe: "RemoveBodyPart", Part: 3}}
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
	input.Facts.SurgeryOptions = []SurgeryOption{{Patient: "Thing_A", Recipe: "RemoveBodyPart", Part: 3}}
	input.ActionIDs = append(input.ActionIDs, "a2")
	i := clientFixture(t, response)
	_, err := i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidCommand)
}

func TestSurgeryOptionsValidatedAndBounded(t *testing.T) {
	i := clientFixture(t, func(context.Context, model.Request) (model.Response, error) {
		t.Fatal("model called")
		return model.Response{}, nil
	})
	input := inputFixture()
	input.Facts.SurgeryOptions = []SurgeryOption{
		{Patient: "Thing_A", Recipe: "RemoveBodyPart", Part: 3},
		{Patient: "Thing_A", Recipe: "RemoveBodyPart", Part: 3},
	}
	_, err := i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidInput)

	input = inputFixture()
	input.Facts.SurgeryOptions = []SurgeryOption{{Patient: "", Recipe: "RemoveBodyPart", Part: 3}}
	_, err = i.Interpret(context.Background(), input)
	assertKind(t, err, InvalidInput)

	input = inputFixture()
	input.Facts.SurgeryOptions = []SurgeryOption{{Patient: "Thing_A", Recipe: "RemoveBodyPart", Part: -2}}
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
