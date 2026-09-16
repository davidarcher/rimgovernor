package httpapi

import (
	"context"
	"encoding/json"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/interpreter"
	"github.com/davidarcher/RimGovernor/go/internal/model"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

const chatJSON = `{"requestId":"chat-request","expected":{"colonyId":"colony","loadToken":"load","mapId":0},"message":"what are you doing about food?"}`

// chatNative is a fixed native census: one named colonist, one downed
// stranger, stocked steel and a native policy resource.
type chatNative struct{}

func chatContext() *c.ObservationContext {
	return &c.ObservationContext{Identity: &c.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String("load"), MapId: proto.Int32(0)}, Tick: proto.Int64(500), NativeGeneration: proto.Uint64(9)}
}
func (chatNative) ReadColonyFacts(context.Context, *c.Identity, bool, []string) (*o.ColonyFactsReply, bridge.Result, error) {
	snapshot := &o.ColonyFactsSnapshot{Context: chatContext(), ColonistCount: proto.Uint32(1), FoodRunwayDays: proto.Float64(4), Resources: []*o.Quantity{{DefName: proto.String("Steel"), Units: proto.Int64(120)}}, PolicyResources: []*o.DefinitionRef{{DefName: proto.String("Silver")}}}
	return &o.ColonyFactsReply{Outcome: &o.ColonyFactsReply_Observed{Observed: snapshot}}, bridge.Result{}, nil
}
func (chatNative) ReadHomeColonists(context.Context, *c.Identity) (*o.ListPawnsReply, bridge.Result, error) {
	return &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: chatContext(), Pawns: []*o.PawnState{{Pawn: &o.EntityRef{Id: proto.String("Thing_Human1"), Label: proto.String("Bob")}}}}}}, bridge.Result{}, nil
}
func (chatNative) ReadRoutinePopulation(context.Context, *c.Identity) (bridge.PrisonerCensus, bridge.Result, error) {
	return bridge.PrisonerCensus{Context: chatContext(), Custody: domain.Known([]policy.CustodyFacts{{Pawn: "Thing_Human1", Downed: domain.Known(false)}, {Pawn: "Thing_Human9", Downed: domain.Known(true)}})}, bridge.Result{}, nil
}

type chatCompleter struct {
	reply string
	seen  []model.Request
}

func (m *chatCompleter) Complete(_ context.Context, r model.Request) (model.Response, error) {
	m.seen = append(m.seen, r)
	return model.Response{Text: m.reply, FinishReason: model.Stop}, nil
}

func chatAPI(t *testing.T, reply string) (*Server, *playerFixture, *chatCompleter) {
	t.Helper()
	s, f := playerAPI(t)
	completer := &chatCompleter{reply: reply}
	interp, err := interpreter.New(interpreter.Config{ContextTokens: 16384, MaxOutputTokens: 1024}, completer)
	if err != nil {
		t.Fatal(err)
	}
	s.EnableChat(interp, chatNative{}, f.journal)
	return s, f, completer
}

func TestChatHTTPDisabledReturns501(t *testing.T) {
	s, f := playerAPI(t)
	w := playerCall(s, "POST", "/api/chat", chatJSON, s.playerToken)
	if w.Code != 501 {
		t.Fatal(w.Code, w.Body.String())
	}
	if f.calls != 0 {
		t.Fatal("disabled chat reached player", f.calls)
	}
}

// TestChatHTTPRejectsUnauthenticatedAndDisabled exercises the disabled-chat
// path. The generic mutation guard (body size, query string) runs ahead of
// any per-route dispatch and reports 400 regardless of enablement; anything
// that clears that guard hits chat's own enablement check before its body is
// ever decoded, so a merely malformed-but-bounded body reports 501 just like
// a well-formed one when chat is off.
func TestChatHTTPRejectsUnauthenticatedAndDisabled(t *testing.T) {
	s, _ := playerAPI(t)
	for _, body := range []string{`null`, chatJSON + `{}`, strings.Replace(chatJSON, `"message":"what are you doing about food?"`, `"message":null`, 1)} {
		w := playerCall(s, "POST", "/api/chat", body, s.playerToken)
		if w.Code != 501 {
			t.Fatal(w.Code, w.Body.String())
		}
	}
	for _, tc := range []struct {
		method, path, body, token string
		status                    int
	}{
		{"POST", "/api/chat", chatJSON, "", 403},
		{"GET", "/api/chat", "", "", 405},
		{"POST", "/api/chats/plans", chatJSON, s.playerToken, 501},
		{"POST", "/api/chat?x=1", chatJSON, s.playerToken, 400},
		{"POST", "/api/chat", strings.Repeat(" ", 8193) + chatJSON, s.playerToken, 400},
	} {
		w := playerCall(s, tc.method, tc.path, tc.body, tc.token)
		if w.Code != tc.status {
			t.Fatal(tc.path, w.Code, w.Body.String())
		}
	}
}
func TestChatFailureStatus(t *testing.T) {
	for _, tc := range []struct {
		kind interpreter.FailureKind
		want int
	}{
		{interpreter.StaleFacts, 409},
		{interpreter.ModelFailure, 502},
		{interpreter.InvalidGuidance, 422},
		{interpreter.UnknownFacts, 422},
		{interpreter.InvalidInput, 400},
		{interpreter.BudgetExceeded, 400},
	} {
		if got := chatFailureStatus(tc.kind); got != tc.want {
			t.Fatalf("%s: got %d want %d", tc.kind, got, tc.want)
		}
	}
}
func TestDecodeChatRequest(t *testing.T) {
	requestID, world, message, err := decodeChatRequest(strings.NewReader(chatJSON))
	if err != nil {
		t.Fatal(err)
	}
	if requestID != "chat-request" || world.Colony != "colony" || world.Load != "load" || world.Map != 0 || message != "what are you doing about food?" {
		t.Fatal(requestID, world, message)
	}
}

func TestChatHTTPExplainOnlyWritesNothing(t *testing.T) {
	s, f, completer := chatAPI(t, `{"explanation":"Rice is planted; runway is four days.","guidance":null}`)
	w := playerCall(s, "POST", "/api/chat", chatJSON, s.playerToken)
	var resp chatResponseDTO
	if err := json.Unmarshal(w.Body.Bytes(), &resp); err != nil || w.Code != 201 || resp.RequestID != "chat-request" || resp.Guidance != nil || !strings.Contains(resp.Explanation, "Rice") {
		t.Fatal(w.Code, w.Body.String(), err)
	}
	if f.calls != 0 {
		t.Fatal("explain-only chat reached a policy input", f.calls)
	}
	facts := completer.seen[0].Messages[1].Content
	for _, want := range []string{`"Bob"`, `"Thing_Human9"`, `"downed":true`, `"Steel"`, `"Silver"`, `"foodRunwayDays":4`} {
		if !strings.Contains(facts, want) {
			t.Fatal("facts missing", want, facts)
		}
	}
}

func TestChatHTTPAppliesGuidanceThroughPolicyInputs(t *testing.T) {
	s, f, completer := chatAPI(t, `{"explanation":"Capping the colony at eight.","guidance":{"kind":"set_population_policy","maximum":8,"foodDays":20}}`)
	w := playerCall(s, "POST", "/api/chat", chatJSON, s.playerToken)
	var resp chatResponseDTO
	if err := json.Unmarshal(w.Body.Bytes(), &resp); err != nil || w.Code != 201 || resp.Guidance == nil || resp.Guidance.Kind != "set_population_policy" || resp.Guidance.PopulationPolicy == nil || resp.Guidance.PopulationPolicy.Maximum != 8 || resp.Guidance.PopulationPolicy.FoodDays != 20 {
		t.Fatal(w.Code, w.Body.String(), err)
	}
	if f.calls != 1 {
		t.Fatal("policy input calls", f.calls)
	}
	stored, err := f.journal.CurrentPopulationPolicy(context.Background(), chatWorld())
	if err != nil || stored.Maximum() != 8 {
		t.Fatal(stored, err)
	}

	// The next message sees the policy it just set, and activating a goal
	// records a player goal bound to the world's root plan at the observed tick.
	completer.reply = `{"explanation":"Working on food now.","guidance":{"kind":"activate_goal","goal":"EnsureFoodSupply"}}`
	w = playerCall(s, "POST", "/api/chat", strings.Replace(chatJSON, "chat-request", "chat-2", 1), s.playerToken)
	if err := json.Unmarshal(w.Body.Bytes(), &resp); err != nil || w.Code != 201 || resp.Guidance == nil || resp.Guidance.Kind != "activate_goal" || resp.Guidance.Goal == nil || resp.Guidance.Goal.Source != "player" || resp.Guidance.Goal.Need != "deficit" || resp.Guidance.Goal.Tick != 500 {
		t.Fatal(w.Code, w.Body.String(), err)
	}
	if !strings.Contains(completer.seen[1].Messages[1].Content, `"populationPolicy":{"maximum":8,"foodDays":20}`) {
		t.Fatal(completer.seen[1].Messages[1].Content)
	}
	goalID := resp.Guidance.Goal.GoalID
	goal, err := f.journal.LoadGoal(context.Background(), goalID)
	if err != nil || goal.Goal.Snapshot.Plan != "root/colony/load/0" {
		t.Fatal(goal, err)
	}

	// Cancelling names the exact identity the facts listed; the store's own
	// revision is presented, not one the model invented.
	completer.reply = `{"explanation":"Stopping that.","guidance":{"kind":"cancel_goal","goalId":"` + string(goalID) + `"}}`
	w = playerCall(s, "POST", "/api/chat", strings.Replace(chatJSON, "chat-request", "chat-3", 1), s.playerToken)
	if err := json.Unmarshal(w.Body.Bytes(), &resp); err != nil || w.Code != 201 || resp.Guidance == nil || resp.Guidance.Kind != "cancel_goal" || resp.Guidance.Goal == nil || resp.Guidance.Goal.Status != "cancelled" {
		t.Fatal(w.Code, w.Body.String(), err)
	}
	if !strings.Contains(completer.seen[2].Messages[1].Content, string(goalID)) {
		t.Fatal("cancellable goal absent from facts")
	}
	if f.calls != 3 {
		t.Fatal("policy input calls", f.calls)
	}
}

func TestChatHTTPRefusesGuidanceOutsideFacts(t *testing.T) {
	for _, tc := range []struct {
		name, reply string
		status      int
		code        string
	}{
		{"unknown pawn", `{"explanation":"ok","guidance":{"kind":"set_population_decision","pawn":"Thing_Human77","decision":"rescue"}}`, 422, "unknown_facts"},
		{"unknown goal", `{"explanation":"ok","guidance":{"kind":"cancel_goal","goalId":"routine-nope"}}`, 422, "unknown_facts"},
		{"order-shaped", `{"explanation":"ok","guidance":{"kind":"build","defName":"Wall"}}`, 422, "invalid_guidance"},
		{"malformed", `not json`, 422, "invalid_guidance"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			s, f, _ := chatAPI(t, tc.reply)
			w := playerCall(s, "POST", "/api/chat", chatJSON, s.playerToken)
			var failure Failure
			if err := json.Unmarshal(w.Body.Bytes(), &failure); err != nil || w.Code != tc.status || failure.Code != tc.code {
				t.Fatal(w.Code, w.Body.String(), err)
			}
			if f.calls != 0 {
				t.Fatal("refused guidance reached a policy input")
			}
		})
	}
}

func chatWorld() store.World { return store.World{Colony: "colony", Load: "load", Map: 0} }
