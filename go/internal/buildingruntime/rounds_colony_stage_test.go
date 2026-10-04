package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// stampContextTicks sets the tick of every ObservationContext inside a
// fake native reply, so a reply advanced a tick stays one census.
func stampContextTicks(m protoreflect.Message, tick int64) {
	if ctx, ok := m.Interface().(*c.ObservationContext); ok {
		ctx.Tick = proto.Int64(tick)
		return
	}
	m.Range(func(fd protoreflect.FieldDescriptor, v protoreflect.Value) bool {
		switch {
		case fd.IsList() && fd.Kind() == protoreflect.MessageKind:
			for i := 0; i < v.List().Len(); i++ {
				stampContextTicks(v.List().Get(i).Message(), tick)
			}
		case fd.IsMap():
		case fd.Kind() == protoreflect.MessageKind:
			stampContextTicks(v.Message(), tick)
		}
		return true
	})
}

// The colony stage decides whether the stone shell is raised at all: at
// Foothold with the shelter unmet the review raises no MaintainStoneShell
// goal, and at Stable (a floor here; the ladder's climb is
// policy.TestColonyStageTransitions) the goal ranks and its proposal is
// admitted on the same fake native.
func TestRoundsStoneShellFollowsColonyStage(t *testing.T) {
	if testing.Short() {
		t.Skip("slow: runs under cmd/test -full and nightly")
	}
	t.Parallel()
	ctx := context.Background()
	p, db, n := stoneShellFixture(t)
	v := n.reply.GetObserved()
	stage := func() policy.ColonyStageRecord {
		t.Helper()
		review, err := db.LoadRounds(ctx)
		if err != nil || review.Stage == nil {
			t.Fatal(review.Revision, err)
		}
		return *review.Stage
	}
	row := func() (store.RoundsDevelopmentRow, bool) {
		t.Helper()
		review, err := db.LoadRounds(ctx)
		if err != nil {
			t.Fatal(err)
		}
		for _, row := range review.Development.Rows {
			if row.Goal == policy.MaintainStoneShell {
				return row, true
			}
		}
		return store.RoundsDevelopmentRow{}, false
	}
	step := func() {
		t.Helper()
		tick := v.Context.GetTick() + 1
		for _, m := range []proto.Message{n.reply, n.pawnReply, n.buildings, n.sites.Context} {
			stampContextTicks(m.ProtoReflect(), tick)
		}
		if _, err := p.reviewer.Step(ctx); err != nil {
			t.Fatal(err)
		}
	}
	// No indoor sleeping slot for the one colonist: the shelter gate is
	// unmet and the colony sits at Foothold.
	indoor := v.IndoorSleepingCapacity
	v.IndoorSleepingCapacity = proto.Uint32(0)
	p.reviewer.policy.Stage.Floor = policy.StageFoothold
	step()
	if s := stage(); s.Stage != policy.StageFoothold || !s.Held || s.Blocker != policy.StageBlockerShelter {
		t.Fatalf("foothold stage %+v", s)
	}
	if r, ok := row(); ok && (r.Selected || r.Reason != policy.DevelopmentStage) {
		t.Fatalf("stone shell raised at Foothold: %+v", r)
	}
	v.IndoorSleepingCapacity = indoor
	p.reviewer.policy.Stage.Floor = policy.StageStable
	step()
	if r, ok := row(); !ok || !r.Selected || r.Reason != "" {
		t.Fatalf("stone shell row at Stable %+v", r)
	}
	result, err := p.Step(ctx)
	if err != nil || result.Verdict != BuildingReasonAdmitted || n.previews != 1 {
		t.Fatal(result, err, n.previews)
	}
	plan, err := db.LoadPlan(ctx, result.Plan)
	if err != nil || len(plan.Progress) != 2 {
		t.Fatal(plan, err)
	}
	if replacement, ok := plan.Spec.Actions()[1].Building(); !ok || replacement.Cell() != (domain.Cell{X: 4, Z: 4}) || replacement.Stuff() != "BlocksGranite" {
		t.Fatal(plan.Spec.Actions())
	}
}
