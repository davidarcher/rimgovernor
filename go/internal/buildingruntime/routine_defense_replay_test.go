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
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
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
	orders   combatOrdersFake
	frame    replayFrame
	weapons  recordedWeapons
}

func (n *defenseReplayNative) combatMirror() []*mp.CombatPawn       { return n.frame.mirror }
func (n *defenseReplayNative) combatMortars() []*mp.CombatMortarRow { return n.frame.mortars }

func (n *defenseReplayNative) recordedWeaponDefs() []bridge.FixtureDef { return n.weapons.fixtures() }

func (n *defenseReplayNative) FrameThings(context.Context, *c.Identity) (bridge.Things, error) {
	return n.weapons.things(), nil
}

func (n *defenseReplayNative) DefinitionCatalog(context.Context, *c.Identity) (*bridge.DefinitionCatalog, error) {
	catalog := bridge.FixtureCatalog("load", append(bridge.CoreWeaponFixtures(), n.weapons.fixtures()...)...)
	recorded, err := fullCatalogRows()
	if err != nil {
		return nil, err
	}
	for class, rows := range recorded {
		catalog.Defs[class] = rows
	}
	return catalog, nil
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
	raw, err := n.weapons.upgrade(n.step.CombatPawns)
	if err != nil {
		n.t.Fatal(err)
	}
	if err := protojson.Unmarshal(raw, reply); err != nil {
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
	steps := make([]snapshot.Defense, 0, len(paths))
	for _, path := range paths {
		step, err := snapshot.LoadDefense(path)
		if err != nil {
			t.Fatal(err)
		}
		steps = append(steps, step)
	}
	results, methods, db := replayDefenseSteps(t, replayFrame{}, steps...)
	if len(steps) == 1 && results[0].Verdict.String() != steps[0].Reason {
		t.Fatalf("%s: replay %s, recorded %s", paths[0], results[0].Verdict, steps[0].Reason)
	}
	return results, methods, db
}

// replayFrame is what a test adds to every replayed frame: its combat pawn
// rows (#969) and colony mortars (#931); the recordings carry neither.
type replayFrame struct {
	mirror  []*mp.CombatPawn
	mortars []*mp.CombatMortarRow
}

// replayDefenseSteps is replayDefense over loaded, possibly edited, steps,
// with frame's rows in every frame.
func replayDefenseSteps(t *testing.T, frame replayFrame, steps ...snapshot.Defense) ([]RoutineDefenseResult, []domain.MethodID, *store.Store) {
	t.Helper()
	r, db, session, _, _ := routineFixture(t)
	ctx := context.Background()
	current := session.State().Snapshot
	world := store.World{Colony: current.Colony, Load: current.Load, Map: current.Map}
	native := &defenseReplayNative{t: t, identity: boundary.Identity(current), native: uint64(current.Native), frame: frame}
	planner, err := NewRoutineDefensePlanner(r, framed{native})
	if err != nil {
		t.Fatal(err)
	}
	var results []RoutineDefenseResult
	var methods []domain.MethodID
	for i, step := range steps {
		native.step = step
		if i == 0 {
			if step.Layout != nil {
				layout := *step.Layout
				layout.World = world
				if err = db.SaveDefenseLayout(ctx, layout); err != nil {
					t.Fatal(err)
				}
			}
			review, err := db.LoadRounds(ctx)
			if err != nil {
				t.Fatal(err)
			}
			facts := policy.RoutineFacts{Workers: domain.Known(len(step.Emergency.Colonists)), Wood: domain.Known(int64(100)), Hostiles: domain.Known(int64(1)), CriticalPatients: domain.Known(int64(0)), CleanupPawns: domain.Known(false), ColonyNaming: domain.Known(false), ChoiceDialog: domain.Known(false)}
			if _, err = db.ReviewRoutine(ctx, store.RoundsRequest{Revision: review.Revision, Current: current, Tick: 7, Enabled: true, Policy: policy.DefaultRoutinePolicy(), Facts: facts}); err != nil {
				t.Fatal(err)
			}
		}
		got, err := planner.Step(ctx)
		if err != nil {
			t.Fatalf("step %d: %v", i, err)
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
	review, err := db.LoadRounds(ctx)
	if err != nil {
		t.Fatal(err)
	}
	incident, _, _, err := routineIncident(ctx, db, review, policy.ActiveCombat)
	if err != nil {
		t.Fatal(err)
	}
	for _, m := range incident.Methods {
		if m.Plan == plan {
			return m.Method
		}
	}
	t.Fatal("no ActiveCombat method admitted", plan)
	return ""
}

// squadAttacks is the fight's squad formation: its roles' targets by mode.
func squadAttacks(t *testing.T, db *store.Store, plan domain.PlanID) (melee, ranged map[domain.PawnID]int) {
	t.Helper()
	fight, ok, err := db.LoadCombatFight(context.Background(), plan)
	if err != nil || !ok {
		t.Fatal("no fight for", plan, err)
	}
	melee, ranged = map[domain.PawnID]int{}, map[domain.PawnID]int{}
	for _, role := range fight.Memory.Roles {
		if role.Cell != nil {
			t.Fatal("squad defense positioned a defender")
		}
		if role.Ranged {
			ranged[role.Target]++
		} else {
			melee[role.Target]++
		}
	}
	return melee, ranged
}

// wantTactic checks that the step admitted the fight's combat method with
// the formation's tactic.
func wantTactic(t *testing.T, db *store.Store, method domain.MethodID, plan domain.PlanID, tactic policy.CombatTactic) {
	t.Helper()
	if !strings.HasPrefix(string(method), combatMethodPrefix) {
		t.Fatalf("method %q, want %s…", method, combatMethodPrefix)
	}
	fight, ok, err := db.LoadCombatFight(context.Background(), plan)
	if err != nil || !ok || fight.Memory.Tactic != tactic {
		t.Fatalf("fight %+v, want %s (%v)", fight, tactic, err)
	}
}
