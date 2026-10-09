package buildingruntime

import (
	"context"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// The due queue must renew through Worker after a census invalidation. The
// old colony-only fallback discarded the healthy pawn roster and returned
// hunt_census unavailable until another full Round rebuilt the census.
func TestRulesDueRenewalAfterCensusInvalidation(t *testing.T) {
	ctx := context.Background()
	_, r, db, v, n := pestFixture(t)
	foodPlanFixture(v)
	v.Acquisition[len(v.Acquisition)-1].Designated = proto.Bool(true)
	v.Acquisition[len(v.Acquisition)-1].DesignatedTick = proto.Int64(v.Context.GetTick())
	r.methods = domain.Known([]policy.ConcernID{policy.EnsureFoodSupply})
	r.census.invalidate()
	if _, err := r.Step(ctx); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoundsRulesPlanner(r)
	if err != nil {
		t.Fatal(err)
	}
	base := r.player.session.(*playerFakeSession)
	fake := &workerFake{playerFakeSession: base}
	r.player.session = fake
	w := &Worker{player: r.player, session: fake, config: WorkerConfig{RoundsMethods: true, StepTimeout: time.Minute, StepInterval: time.Millisecond, MaxBackoff: time.Second}, waits: map[domain.ActionID]workerWait{}}
	// Remove unrelated player guidance so every dispatched action is a rule.
	guidance := playerPlan(t, db)
	for _, a := range guidance.Spec.Actions() {
		if _, err := db.Cancel(ctx, guidance.Spec.ID(), a.ID()); err != nil {
			t.Fatal(err)
		}
	}
	var entry plannerEntry
	for _, e := range plannerCatalog {
		if e.name == "rules" {
			entry = e
		}
	}
	q := newPlannerQueue()
	q.catalog = func() []plannerEntry { return []plannerEntry{entry} }
	tick := v.Context.GetTick()
	firstTick := tick
	expires := int64(0)
	writes := 0
	fake.run = func(ctx context.Context, plan domain.PlanID, action domain.ActionID) (executor.Result, error) {
		// The worker reaches Hands only after the planner's method is durable.
		attach := committedRules(t, db, plan)
		if len(attach.Rules()) != 1 {
			t.Fatal("renewal cleared standing hunt")
		}
		scope := base.State().Snapshot
		scope.Plan = plan
		scope.Revision = 1
		if _, err := db.Prepare(ctx, plan, action, scope, domain.Tick(tick)); err != nil {
			return executor.Result{}, err
		}
		progress, err := db.Dispatch(ctx, plan, action, scope, domain.Tick(tick))
		if err != nil {
			return executor.Result{}, err
		}
		if expires != 0 && tick >= expires {
			t.Fatalf("renewal at %d missed expiry %d", tick, expires)
		}
		expires = tick + attach.LeaseTicks()
		writes++
		progress, err = db.RecordReceipt(ctx, plan, action, progress.View().Attempt, domain.ReceiptAccepted)
		return executor.Result{Progress: progress}, err
	}
	step := func() RoundsRulesResult {
		t.Helper()
		call, epoch, done, err := r.player.enter(ctx, "rules-renewal-test", false)
		if err != nil {
			t.Fatal(err)
		}
		defer done()
		got, err := planner.step(call, epoch, newStepArbiter())
		if err != nil {
			t.Fatal(err)
		}
		return got
	}
	for i := 0; i < 4; i++ {
		tick = firstTick + int64(i)*int64(entry.reviewEvery())
		v.Context.Tick = proto.Int64(tick)
		for _, source := range v.Acquisition {
			source.SourceSnapshot.Context.Tick = proto.Int64(tick)
		}
		n.pawnReply.GetObserved().Context.Tick = proto.Int64(tick)
		if i > 0 {
			r.census.invalidate()
		}
		sel := q.selection(tick, i == 0, false, nil)
		if !sel.planners || sel.pick != nil && !sel.pick(entry) {
			t.Fatal("rules renewal was not due")
		}
		got := step()
		if got.Verdict != BuildingReasonAdmitted {
			t.Fatalf("tick %d: %+v", tick, got)
		}
		q.ran(sel, []string{"rules"}, func(string) (Verdict, bool) { return got.Verdict, true }, tick, nil)
		if err := w.step(ctx, time.Now()); err != nil {
			t.Fatal(err)
		}
		if writes != i+1 {
			t.Fatalf("tick %d: %d dispatches", tick, writes)
		}
	}
	if tick <= firstTick+policy.RuleLeaseTicks {
		t.Fatal("test did not cross original expiry")
	}
	// A genuine unavailable source must not extend the last lease.
	oldExpiry := expires
	tick += int64(entry.reviewEvery())
	v.Context.Tick = proto.Int64(tick)
	for _, source := range v.Acquisition {
		source.SourceSnapshot.Context.Tick = proto.Int64(tick)
	}
	n.pawnReply.GetObserved().Context.Tick = proto.Int64(tick)
	v.Acquisition = nil
	v.Issues = append(v.Issues, &o.ReadIssue{Field: proto.String("acquisition"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_REQUESTED.Enum()}})
	r.census.invalidate()
	if got := step(); got.Verdict != fieldUnavailable("hunt_census") {
		t.Fatalf("unknown census: %+v", got)
	}
	if err := w.step(ctx, time.Now()); err != nil {
		t.Fatal(err)
	}
	if writes != 4 || expires != oldExpiry {
		t.Fatal("unknown census renewed lease")
	}
	if err := base.Disable(); err != nil {
		t.Fatal(err)
	}
	if got := step(); got.Verdict != BuildingReasonDisabled {
		t.Fatalf("disabled: %+v", got)
	}
	if err := w.step(ctx, time.Now()); err != nil {
		t.Fatal(err)
	}
	if writes != 4 || expires != oldExpiry {
		t.Fatal("control loss renewed lease")
	}
}
