package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

type productionPolicyNative struct {
	read   bridge.ProductionPolicyRead
	onRead func()
}

func (n *productionPolicyNative) ReadProductionPolicy(context.Context, *c.Identity) (bridge.ProductionPolicyRead, bridge.Result, error) {
	if n.onRead != nil {
		n.onRead()
	}
	return n.read, bridge.Result{}, nil
}

func TestProductionPolicyReconcilesEmptyAndRepeatedDrift(t *testing.T) {
	for _, scenario := range []string{"config removed", "saved stale stop", "manual drift"} {
		t.Run(scenario, func(t *testing.T) {
			r, db, _, request, base := routineFixture(t)
			ctx := context.Background()
			r.methods = domain.Known([]policy.GoalID{policy.ProductionPolicy})
			n := &productionPolicyNative{read: bridge.ProductionPolicyRead{Context: proto.Clone(base.reply.GetObserved().Context).(*c.ObservationContext), SnapshotToken: "same-token"}}
			planner, err := NewRoutineProductionPolicyPlanner(r, n)
			if err != nil {
				t.Fatal(err)
			}
			step := func() RoutineProductionPolicyResult {
				t.Helper()
				if _, err := r.Step(ctx); err != nil {
					t.Fatal(err)
				}
				out, err := planner.Step(ctx)
				if err != nil {
					t.Fatal(err)
				}
				return out
			}
			complete := func(out RoutineProductionPolicyResult) domain.ProductionPolicy {
				t.Helper()
				if out.Reason != BuildingMethodAdmitted {
					t.Fatal(out)
				}
				plan, err := db.LoadPlan(ctx, out.Plan)
				if err != nil {
					t.Fatal(err)
				}
				action := plan.Spec.Actions()[0]
				value, _ := action.ProductionPolicy()
				snapshot := r.player.State().Snapshot
				snapshot.Plan, snapshot.Revision = out.Plan, 1
				tick := domain.Tick(n.read.Context.GetTick())
				if _, err := db.PrepareProductionPolicy(ctx, out.Plan, action.ID(), store.ProductionPolicyAdmission{Snapshot: snapshot, Tick: tick, Floors: value.Floors(), Stopped: value.Stopped(), SnapshotToken: "same-token"}); err != nil {
					t.Fatal(err)
				}
				if _, err := db.Dispatch(ctx, out.Plan, action.ID(), snapshot, tick); err != nil {
					t.Fatal(err)
				}
				if _, err := db.RecordReceipt(ctx, out.Plan, action.ID(), 1, domain.ReceiptAccepted); err != nil {
					t.Fatal(err)
				}
				if _, err := db.Observe(ctx, out.Plan, domain.Observation{Action: action.ID(), Attempt: 1, Snapshot: snapshot, Tick: tick, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch}, snapshot); err != nil {
					t.Fatal(err)
				}
				n.read.Floors = map[policy.Resource]int64{}
				for _, row := range value.Floors() {
					n.read.Floors[policy.Resource(row.Resource)] = row.Floor
				}
				n.read.Stopped = nil
				for _, name := range value.Stopped() {
					n.read.Stopped = append(n.read.Stopped, policy.Resource(name))
				}
				return value
			}
			if scenario == "saved stale stop" {
				// A new controller/review sees saved restrictions without any
				// corresponding current configuration or directive.
				n.read.Floors = map[policy.Resource]int64{"Steel": 100}
				n.read.Stopped = []policy.Resource{"Steel"}
			} else {
				r.policy.ResourceReserves = map[policy.Resource]int64{"Steel": 100}
				r.policy.StoppedResources = []policy.Resource{"Steel"}
				first := step()
				if out := step(); out.Reason != BuildingMethodExistingWork {
					t.Fatal(out)
				}
				complete(first)
				if out := step(); out.Reason != BuildingMethodUsed {
					t.Fatal(out)
				}
				if scenario == "config removed" {
					r.policy.ResourceReserves, r.policy.StoppedResources = nil, nil
				} else {
					request.Kind, request.RequestID = store.PauseControl, "manual-production"
					if _, err := r.player.Pause(ctx, request); err != nil {
						t.Fatal(err)
					}
					n.read.Floors, n.read.Stopped = nil, nil
					if out, err := planner.Step(ctx); err != nil || out.Reason != BuildingMethodDisabled {
						t.Fatal(out, err)
					}
					request.Kind, request.RequestID = store.ResumeControl, "resume-production"
					if _, err := r.player.Resume(ctx, request); err != nil {
						t.Fatal(err)
					}
				}
			}
			value := complete(step())
			if scenario != "manual drift" && (len(value.Floors()) != 0 || len(value.Stopped()) != 0) {
				t.Fatal("stale restriction retained", value)
			}
			if scenario == "manual drift" {
				if len(value.Floors()) != 1 || len(value.Stopped()) != 1 {
					t.Fatal("desired policy lost", value)
				}
				// Identical drift can recur without a tick or token change.
				n.read.Floors, n.read.Stopped = nil, nil
				complete(step())
			}
			if out := step(); out.Reason != BuildingMethodUsed {
				t.Fatal(out)
			}
		})
	}
}

func TestProductionPolicyFreshWorldAndAuthorityGuards(t *testing.T) {
	for _, guard := range []string{"load", "tick", "manual"} {
		t.Run(guard, func(t *testing.T) {
			r, db, session, _, base := routineFixture(t)
			r.methods = domain.Known([]policy.GoalID{policy.ProductionPolicy})
			ctx := context.Background()
			if _, err := r.Step(ctx); err != nil {
				t.Fatal(err)
			}
			n := &productionPolicyNative{read: bridge.ProductionPolicyRead{Context: proto.Clone(base.reply.GetObserved().Context).(*c.ObservationContext), Stopped: []policy.Resource{"Steel"}, SnapshotToken: "token"}}
			switch guard {
			case "load":
				n.read.Context.Identity.LoadToken = proto.String("other-load")
			case "tick":
				n.read.Context.Tick = proto.Int64(n.read.Context.GetTick() - 1)
			case "manual":
				n.onRead = func() { _ = session.Disable() }
			}
			planner, err := NewRoutineProductionPolicyPlanner(r, n)
			if err != nil {
				t.Fatal(err)
			}
			if out, err := planner.Step(ctx); err == nil {
				t.Fatal("guard accepted", out)
			}
			plans, err := db.PlanHistoryWithPrefix(ctx, "routine-production-policy-", 100)
			if err != nil || len(plans) != 0 {
				t.Fatal(plans, err)
			}
		})
	}
}

func TestProductionPolicyExplicitDirectiveOverridesConfigAcrossResume(t *testing.T) {
	r, db, _, request, base := routineFixture(t)
	ctx := context.Background()
	r.methods = domain.Known([]policy.GoalID{policy.ProductionPolicy})
	r.policy.ResourceReserves = map[policy.Resource]int64{"Steel": 100, "WoodLog": 50}
	r.policy.StoppedResources = []policy.Resource{"Steel"}
	defaults, err := domain.NewProductionPolicy([]domain.ResourceFloor{{Resource: "Steel", Floor: 100}, {Resource: "WoodLog", Floor: 50}}, []string{"Steel"})
	if err != nil {
		t.Fatal(err)
	}
	r.player.config.ProductionDefaults = defaults
	q := playerResourcePolicyRequest("normal-steel", "Steel", 0)
	q.World = store.World{Colony: r.player.State().Snapshot.Colony, Load: r.player.State().Snapshot.Load, Map: r.player.State().Snapshot.Map}
	submission, _, err := r.player.SubmitResourcePolicy(ctx, q)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := db.LoadPlan(ctx, submission.Plan)
	if err != nil {
		t.Fatal(err)
	}
	want, _ := plan.Spec.Actions()[0].ProductionPolicy()
	if rows := want.Floors(); len(rows) != 1 || rows[0].Resource != "WoodLog" || len(want.Stopped()) != 0 {
		t.Fatal("explicit clear lost precedence or unrelated default", want)
	}
	request.Kind, request.RequestID = store.PauseControl, "directive-manual"
	if _, err := r.player.Pause(ctx, request); err != nil {
		t.Fatal(err)
	}
	request.Kind, request.RequestID = store.ResumeControl, "directive-resume"
	if _, err := r.player.Resume(ctx, request); err != nil {
		t.Fatal(err)
	}
	if _, err := r.Step(ctx); err != nil {
		t.Fatal(err)
	}
	n := &productionPolicyNative{read: bridge.ProductionPolicyRead{Context: proto.Clone(base.reply.GetObserved().Context).(*c.ObservationContext), SnapshotToken: "token", Stopped: []policy.Resource{"Steel"}}}
	planner, err := NewRoutineProductionPolicyPlanner(r, n)
	if err != nil {
		t.Fatal(err)
	}
	out, err := planner.Step(ctx)
	if err != nil || out.Reason != BuildingMethodAdmitted {
		t.Fatal(out, err)
	}
	plan, err = db.LoadPlan(ctx, out.Plan)
	if err != nil {
		t.Fatal(err)
	}
	got, _ := plan.Spec.Actions()[0].ProductionPolicy()
	if got != want {
		t.Fatal("routine and explicit submission disagree after resume", got, want)
	}
}
