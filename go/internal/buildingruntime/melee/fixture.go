package melee

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/draft"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// Fixture is a draft fixture plus an Actions/Apply writer that applies
// every intent, or refuses it when Refuse is set.
type Fixture struct {
	*draft.Fixture
	Applies int
	Last    *o.Action
	Refuse  bool
}

func NewFixture(t *testing.T) (*MeleeBoundary, *Fixture, executor.Placement) {
	t.Helper()
	_, f := draft.NewFixture(t)
	attack, err := domain.NewSubdue("pawn", "target", "action")
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewMeleeAttackAction("attack", attack)
	if err != nil {
		t.Fatal(err)
	}
	f.P.Action = action
	fixture := &Fixture{Fixture: f}
	b, err := NewMeleeBoundary(&boundary.Boundary{Leases: fixture}, fixture)
	if err != nil {
		t.Fatal(err)
	}
	return b, fixture, f.P
}

func (f *Fixture) Apply(ctx context.Context, identity *c.Identity, actions []*o.Action) (*o.ApplyReply, bridge.Result, error) {
	f.Applies++
	f.Last = proto.Clone(actions[0]).(*o.Action)
	result := &o.ActionResult{Key: actions[0].Key}
	if f.Refuse {
		result.Outcome = &o.ActionResult_Refused{Refused: &o.Refusal{Code: c.FailureCode_FAILURE_CODE_INVALID_REQUEST.Enum(), Reason: proto.String("refused")}}
	} else {
		result.Outcome = &o.ActionResult_Applied{Applied: &r.Receipt{AdmittedContext: proto.Clone(f.Ctx).(*c.ObservationContext), Outcome: &r.Receipt_Applied{Applied: &r.Applied{Observed: &r.EffectEvidence{}}}}}
	}
	return &o.ApplyReply{Results: []*o.ActionResult{result}}, bridge.Result{}, ctx.Err()
}
