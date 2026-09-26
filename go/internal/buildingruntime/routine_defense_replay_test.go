package buildingruntime

import (
	"context"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/snapshot"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
)

// defenseReplayNative serves one recorded defense step's native replies,
// re-addressed to the fixture's world.
type defenseReplayNative struct {
	t        *testing.T
	identity *c.Identity
	native   uint64
	step     snapshot.Defense
}

func (n *defenseReplayNative) context(raw []byte) *c.ObservationContext {
	n.t.Helper()
	ctx := &c.ObservationContext{}
	if err := protojson.Unmarshal(raw, ctx); err != nil {
		n.t.Fatal(err)
	}
	ctx.Identity, ctx.NativeGeneration = n.identity, &n.native
	return ctx
}

func (n *defenseReplayNative) ReadEmergency(ctx context.Context, _ *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	return bridge.EmergencyObservation{Context: n.context(n.step.EmergencyContext), Facts: n.step.Emergency}, bridge.Result{}, ctx.Err()
}

func (n *defenseReplayNative) ReadCombatPawns(ctx context.Context, _ *c.Identity, _ []string) (*o.ListPawnsReply, bridge.Result, error) {
	n.t.Helper()
	if n.step.CombatPawns == nil {
		n.t.Fatal("the recorded step never read the combat pawns")
	}
	reply := &o.ListPawnsReply{}
	if err := protojson.Unmarshal(n.step.CombatPawns, reply); err != nil {
		n.t.Fatal(err)
	}
	if observed := reply.GetObserved(); observed != nil && observed.Context != nil {
		observed.Context.Identity, observed.Context.NativeGeneration = n.identity, &n.native
	}
	return reply, bridge.Result{}, ctx.Err()
}

func (n *defenseReplayNative) ReadLinesOfFire(ctx context.Context, _ *c.Identity, _, _ []domain.Cell) (bridge.LinesOfFire, bridge.Result, error) {
	n.t.Helper()
	if n.step.LinesContext == nil {
		n.t.Fatal("the recorded step never read the lines of fire")
	}
	return bridge.LinesOfFire{Context: n.context(n.step.LinesContext), Lines: n.step.Lines}, bridge.Result{}, ctx.Err()
}

// replayDefense runs the RoutineDefensePlanner over recorded steps in
// order, on one journal: the first step's layout is stored and a review
// opens ActiveCombat, then each step is served its own replies. It returns
// each step's result, the goal method it admitted, if any, and the journal.
func replayDefense(t *testing.T, paths ...string) ([]RoutineDefenseResult, []domain.MethodID, *store.Store) {
	t.Helper()
	r, db, session, _, _ := routineFixture(t)
	ctx := context.Background()
	current := session.State().Snapshot
	world := store.World{Colony: current.Colony, Load: current.Load, Map: current.Map}
	native := &defenseReplayNative{t: t, identity: boundary.Identity(current), native: uint64(current.Native)}
	planner, err := NewRoutineDefensePlanner(r, native)
	if err != nil {
		t.Fatal(err)
	}
	var results []RoutineDefenseResult
	var methods []domain.MethodID
	for i, path := range paths {
		step, err := snapshot.LoadDefense(path)
		if err != nil {
			t.Fatal(err)
		}
		native.step = step
		if i == 0 {
			if step.Layout != nil {
				layout := *step.Layout
				layout.World = world
				if err = db.SaveDefenseLayout(ctx, layout); err != nil {
					t.Fatal(err)
				}
			}
			review, err := db.LoadRoutineReview(ctx)
			if err != nil {
				t.Fatal(err)
			}
			facts := policy.RoutineFacts{Workers: domain.Known(len(step.Emergency.Colonists)), Wood: domain.Known(int64(100)), Hostiles: domain.Known(int64(1)), CriticalPatients: domain.Known(int64(0)), CleanupPawns: domain.Known(false), ColonyNaming: domain.Known(false), ChoiceDialog: domain.Known(false)}
			if _, err = db.ReviewRoutine(ctx, store.RoutineReviewRequest{Revision: review.Revision, Current: current, Tick: 7, Enabled: true, Policy: policy.DefaultRoutinePolicy(), Facts: facts}); err != nil {
				t.Fatal(err)
			}
		}
		got, err := planner.Step(ctx)
		if err != nil {
			t.Fatalf("%s: %v", path, err)
		}
		if string(got.Reason) != step.Reason {
			t.Fatalf("%s: replay %s, recorded %s", path, got.Reason, step.Reason)
		}
		results = append(results, got)
		methods = append(methods, admittedMethod(t, db, got.Plan))
	}
	return results, methods, db
}

// admittedMethod is the ActiveCombat goal method plan was committed under.
func admittedMethod(t *testing.T, db *store.Store, plan domain.PlanID) domain.MethodID {
	t.Helper()
	if plan == "" {
		return ""
	}
	ctx := context.Background()
	review, err := db.LoadRoutineReview(ctx)
	if err != nil {
		t.Fatal(err)
	}
	for _, binding := range review.Goals {
		if binding.Need != policy.ActiveCombat {
			continue
		}
		goal, err := db.LoadGoal(ctx, binding.Goal)
		if err != nil {
			t.Fatal(err)
		}
		for _, m := range goal.Methods {
			if m.Plan == plan {
				return m.Method
			}
		}
	}
	t.Fatal("no ActiveCombat method admitted", plan)
	return ""
}

// squadAttacks is the admitted squad plan's attacks by mode.
func squadAttacks(t *testing.T, db *store.Store, plan domain.PlanID) (melee, ranged map[domain.PawnID]int) {
	t.Helper()
	state, err := db.LoadPlan(context.Background(), plan)
	if err != nil {
		t.Fatal(err)
	}
	melee, ranged = map[domain.PawnID]int{}, map[domain.PawnID]int{}
	for _, action := range state.Spec.Actions() {
		if _, ok := action.Movement(); ok {
			t.Fatal("squad defense positioned a defender")
		}
		if a, ok := action.RangedAttack(); ok {
			ranged[a.Target()]++
		} else if a, ok := action.MeleeAttack(); ok {
			melee[a.Target()]++
		}
	}
	return melee, ranged
}

func wantMethod(t *testing.T, got domain.MethodID, prefix string) {
	t.Helper()
	if !strings.HasPrefix(string(got), prefix) {
		t.Fatalf("method %q, want %s…", got, prefix)
	}
}
