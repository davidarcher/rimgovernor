package movement

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/draft"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// fixture answers the movement boundary with the draft fixture's pawn row,
// served through the combat read (health detail present).
type fixture struct {
	*draft.Fixture
	combatReads int
}

func (f *fixture) ReadCombatPawns(ctx context.Context, id *c.Identity, ids []string) (*n.ListPawnsReply, bridge.Result, error) {
	f.combatReads++
	return f.Fixture.ReadPawns(ctx, id, ids)
}
func (f *fixture) PreviewMovement(ctx context.Context, _ *c.Identity, command *o.MovePawn) (*o.PreviewReply, bridge.Result, error) {
	job := &r.JobEffect{PawnId: proto.String("pawn"), JobDef: proto.String("Goto"), TargetA: &r.JobTarget{Target: &r.JobTarget_Cell{Cell: proto.Clone(command.Destination).(*c.Cell)}}, CanTry: proto.Bool(true)}
	return &o.PreviewReply{Outcome: &o.PreviewReply_Evaluated{Evaluated: &o.PreviewEvaluation{Context: proto.Clone(f.Ctx).(*c.ObservationContext), Accepted: proto.Bool(true), Projected: &r.EffectEvidence{Effect: &r.EffectEvidence_Job{Job: job}}}}}, bridge.Result{}, ctx.Err()
}
func (f *fixture) LookupMovementAttempt(context.Context, bridge.MovementAttempt) (*r.LookupReply, bridge.Result, error) {
	return nil, bridge.Result{}, executor.ErrEvidence
}
func (f *fixture) ObserveMovementProgress(context.Context, bridge.MovementAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error) {
	return nil, bridge.Result{}, executor.ErrEvidence
}
func (f *fixture) MovePawn(context.Context, *a.WritePrecondition, *o.MovePawn) (*o.ExecuteReply, bridge.Result, error) {
	return nil, bridge.Result{}, executor.ErrEvidence
}

// The movement policy needs the pawn's bleeding and tend facts; only the
// combat pawn read carries them, so the inspection must use it (issue #70:
// every hold-the-line move was refused unknown_facts).
func TestInspectMovementReadsCombatHealth(t *testing.T) {
	_, df := draft.NewFixture(t)
	df.Row.Dead, df.Row.Downed, df.Row.FreeColonist = proto.Bool(false), proto.Bool(false), proto.Bool(true)
	df.Row.Health = &n.PawnHealth{SummaryFraction: proto.Float64(1), Bleeding: proto.Bool(false), NeedsTend: proto.Bool(false)}
	f := &fixture{Fixture: df}
	b, err := NewMovementBoundary(f, f, f, boundary.FixedClock{}, "session")
	if err != nil {
		t.Fatal(err)
	}
	movement, err := domain.NewMovement("pawn", domain.Cell{X: 3, Z: 4}, "action")
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewMovementAction("move", movement)
	if err != nil {
		t.Fatal(err)
	}
	claim := draft.KnownClaim(df)
	inspection, err := b.InspectMovement(context.Background(), executor.Target{Action: action, Snapshot: df.P.Snapshot}, claim)
	if err != nil {
		t.Fatal(err)
	}
	if f.combatReads != 1 {
		t.Fatalf("combat pawn reads = %d", f.combatReads)
	}
	facts := inspection.Facts.Pawn
	for name, fact := range map[string]domain.Fact[bool]{"bleeding": facts.Bleeding, "needsTend": facts.NeedsTend, "dead": facts.Dead, "drafted": facts.Drafted} {
		if _, known := fact.Value(); !known {
			t.Fatalf("%s unknown after combat read", name)
		}
	}
	if _, known := inspection.Facts.NativeCanTry.Value(); !known {
		t.Fatal("native canTry unknown")
	}
}
