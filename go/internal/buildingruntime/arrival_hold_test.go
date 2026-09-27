package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

type arrivalPawnsFake struct {
	at      domain.Cell
	drafted bool
	job     string
}

func (f arrivalPawnsFake) ReadCombatPawns(_ context.Context, identity *c.Identity, ids []string) (*n.ListPawnsReply, bridge.Result, error) {
	context := &c.ObservationContext{Identity: identity, Tick: proto.Int64(500), NativeGeneration: proto.Uint64(3)}
	row := &n.PawnState{Pawn: &n.EntityRef{Id: proto.String(ids[0]), Position: &c.Cell{X: proto.Int32(f.at.X), Z: proto.Int32(f.at.Z)}}, Drafted: proto.Bool(f.drafted), Job: &n.JobEvidence{DefName: proto.String(f.job)}}
	return &n.ListPawnsReply{Outcome: &n.ListPawnsReply_Observed{Observed: &n.PawnSnapshot{Context: context, Pawns: []*n.PawnState{row}}}}, bridge.Result{}, nil
}

type arrivalMovesFake struct{ sent []*o.Action }

func (f *arrivalMovesFake) Apply(_ context.Context, _ *c.Identity, actions []*o.Action) (*o.ApplyReply, bridge.Result, error) {
	f.sent = append(f.sent, actions...)
	return &o.ApplyReply{}, bridge.Result{}, nil
}

var _ boundary.ActionsWriter = (*arrivalMovesFake)(nil)

// arrivalPlan is a shrine-shaped plan: a draft, a completed move to (5,5),
// and a pending breach that requires both.
func arrivalPlan(t *testing.T) (store.PlanState, domain.GenerationSnapshot) {
	t.Helper()
	draft, _ := domain.NewOwnedDraft("pawn")
	d, _ := domain.NewOwnedDraftAction("draft", draft)
	movement, _ := domain.NewMovement("pawn", domain.Cell{X: 5, Z: 5}, d.ID())
	move, _ := domain.NewMovementAction("move", movement)
	wall, _ := domain.NewDeconstruction("Thing_Wall1", "Wall", domain.Cell{X: 6, Z: 6})
	breach, _ := domain.NewDeconstructionAction("breach", wall)
	spec, err := domain.NewPlan("routine-shrine-test", 1, []domain.Action{d, move, breach},
		domain.ActionDependency{Action: "breach", Requires: "draft"}, domain.ActionDependency{Action: "breach", Requires: "move"})
	if err != nil {
		t.Fatal(err)
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Plan: spec.ID(), Revision: 1, Native: 3}
	state := store.PlanState{Spec: spec}
	for _, a := range spec.Actions() {
		p, err := domain.NewProgress(spec, a.ID())
		if err != nil {
			t.Fatal(err)
		}
		if a.ID() == "move" {
			if p, err = p.Prepare(snapshot, 10); err == nil {
				if p, err = p.MarkDispatched(snapshot, 10); err == nil {
					p, err = p.RecordReceipt(p.View().Attempt, domain.ReceiptAccepted)
				}
			}
			if err != nil || p.View().Stage != domain.Completed {
				t.Fatal(p.View(), err)
			}
		}
		state.Progress = append(state.Progress, p)
	}
	return state, snapshot
}

func TestArrivalHoldGatesFinalActionUntilMoversArrive(t *testing.T) {
	plan, snapshot := arrivalPlan(t)
	cases := []struct {
		name   string
		pawns  arrivalPawnsFake
		held   bool
		resent int
	}{
		{"arrived", arrivalPawnsFake{at: domain.Cell{X: 5, Z: 5}, drafted: true, job: "Wait_Combat"}, false, 0},
		{"walking", arrivalPawnsFake{at: domain.Cell{X: 1, Z: 1}, drafted: true, job: "Goto"}, true, 0},
		{"idle away", arrivalPawnsFake{at: domain.Cell{X: 1, Z: 1}, drafted: true, job: "Wait_Combat"}, true, 1},
		{"undrafted away", arrivalPawnsFake{at: domain.Cell{X: 1, Z: 1}, job: "Wait_Combat"}, true, 0},
	}
	for _, tc := range cases {
		moves := &arrivalMovesFake{}
		w := &Worker{config: WorkerConfig{Pawns: tc.pawns, Moves: moves}}
		held := w.arrivalHolds(context.Background(), snapshot, []store.PlanState{plan})
		if held["breach"] != tc.held || held["draft"] || held["move"] || len(moves.sent) != tc.resent {
			t.Fatalf("%s: held %v, resent %d", tc.name, held, len(moves.sent))
		}
		if tc.resent > 0 {
			sent := moves.sent[0].GetMove()
			if sent.GetPawnId() != "pawn" || sent.GetDestination().GetX() != 5 || sent.GetDestination().GetZ() != 5 || moves.sent[0].GetKey() != "routine-shrine-test/move/500" {
				t.Fatalf("%s: resent %v", tc.name, moves.sent[0])
			}
		}
	}
}

// The shrine plan's pending breach keeps the mover's draft held after its
// move was applied: releasing it would cancel the walk.
func TestShrineDraftHeldPastAppliedMove(t *testing.T) {
	plan, s := arrivalPlan(t)
	d := plan.Progress[0]
	var err error
	if d, err = d.Prepare(s, 5); err == nil {
		d, err = d.MarkDispatched(s, 5)
	}
	claim := domain.DraftClaim{Action: "draft", Attempt: 1, Pawn: "pawn", Claim: "claim", Session: "session", Origin: s}
	if err == nil {
		d, err = d.RecordDraftReceipt(1, domain.ReceiptAccepted, domain.Known(claim))
	}
	if err == nil {
		d, err = d.ObserveDraft(domain.Observation{Action: "draft", Attempt: 1, Snapshot: s, Tick: 6, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch}, s, domain.Known(claim))
	}
	if err != nil {
		t.Fatal(err)
	}
	plan.Progress[0] = d
	if !workerPlanHoldsDraft(plan, d.View()) {
		t.Fatal("an applied move released its draft before the breach")
	}
}
