package buildingruntime

import (
	"context"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// raidTestNative is an equipTestNative whose colonists carry rifles and
// whose census lists one walk-in raider at a settable cell.
type raidTestNative struct {
	*equipTestNative
	raider domain.Cell
	toil   string
}

func (n *raidTestNative) ReadEmergency(ctx context.Context, id *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	v, r, err := n.equipTestNative.ReadEmergency(ctx, id)
	v.Facts.Threats = append(v.Facts.Threats, policy.EmergencyThreat{ID: "raider", Kind: policy.Hostile, Dead: domain.Known(false), Downed: domain.Known(false), Animal: domain.Known(false), Distance: domain.Known(40.0), SnapshotToken: "cas"})
	return v, r, err
}
func (n *raidTestNative) ReadCombatPawns(ctx context.Context, id *c.Identity, ids []string) (*o.ListPawnsReply, bridge.Result, error) {
	reply, r, err := n.equipTestNative.ReadCombatPawns(ctx, id, ids)
	observed := reply.GetObserved()
	for _, row := range observed.GetPawns() {
		rifle := &o.EntityRef{Id: proto.String("rifle-" + row.Pawn.GetId())}
		row.Equipment = &o.PawnEquipment{Armed: proto.Bool(true), PrimaryId: rifle.Id, Equipped: []*o.GearItem{{Thing: rifle, Weapon: proto.Bool(true), Ranged: proto.Bool(true), Range: proto.Float64(30)}}}
		row.Health = &o.PawnHealth{NeedsTend: proto.Bool(false), SummaryFraction: proto.Float64(1)}
		row.Job = &o.JobEvidence{PlayerForced: proto.Bool(false), QueuedJobs: proto.Uint32(0)}
	}
	raider := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("raider"), MapId: proto.Int32(observed.Context.Identity.GetMapId()), Position: &c.Cell{X: proto.Int32(n.raider.X), Z: proto.Int32(n.raider.Z)}},
		Dead: proto.Bool(false), Downed: proto.Bool(false), Humanlike: proto.Bool(true), Animal: proto.Bool(false), Hostile: proto.Bool(true), LordJobClass: proto.String("LordJob_AssaultColony"), NearestColonistDistance: proto.Float64(40),
		Equipment: &o.PawnEquipment{Armed: proto.Bool(false)}, Issues: []*o.ReadIssue{{Field: proto.String("pawn.snapshot"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}, {Field: proto.String("mental_state"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}}}
	if n.toil != "" {
		raider.LordToilClass = proto.String(n.toil)
	}
	observed.Pawns = append(observed.Pawns, raider)
	count := uint64(len(observed.Pawns))
	observed.Completeness.Matched, observed.Completeness.Returned = proto.Uint64(count), proto.Uint64(count)
	return reply, r, err
}

// A hold-the-line method stands while the raid is in front of the line;
// once a live raider is past the cover row the hold is cancelled and the
// next step answers with squad defense (#118 breach fallback).
func TestRoutineDefenseAbandonsACrossedHoldForSquadDefense(t *testing.T) {
	t.Parallel()
	r, db, session, _, n := routineFixture(t)
	ctx := context.Background()
	// The corridor runs north: firing cells at z=23 behind sandbags at
	// z=22, the raider walking in from the south.
	native := &raidTestNative{equipTestNative: &equipTestNative{routineNative: n, ids: []string{"a", "b"}}, raider: domain.Cell{X: 9, Z: 5}, toil: "LordToil_AssaultColony"}
	planner, err := NewRoutineDefensePlanner(r, native)
	if err != nil {
		t.Fatal(err)
	}
	snapshot := session.State().Snapshot
	world := store.World{Colony: snapshot.Colony, Load: snapshot.Load, Map: snapshot.Map}
	layout := store.DefenseLayoutRecord{World: world, Goal: "layout-goal", Complete: true, Toward: domain.North, Chokepoint: domain.Cell{X: 9, Z: 10}, Entry: domain.Cell{X: 9, Z: 10},
		Firing: []domain.Cell{{X: 9, Z: 23}, {X: 8, Z: 23}}, Tiers: []store.DefenseTierRecord{{Name: policy.TierFiringLine, Built: true}}}
	if err := db.SaveDefenseLayout(ctx, layout); err != nil {
		t.Fatal(err)
	}
	current, err := db.LoadRoutineReview(ctx)
	if err != nil {
		t.Fatal(err)
	}
	facts := policy.RoutineFacts{Workers: domain.Known(2), Wood: domain.Known(int64(100)), Hostiles: domain.Known(int64(1)), CriticalPatients: domain.Known(int64(0)), CleanupPawns: domain.Known(false), ColonyNaming: domain.Known(false), ChoiceDialog: domain.Known(false)}
	if _, err = db.ReviewRoutine(ctx, store.RoutineReviewRequest{Revision: current.Revision, Current: snapshot, Tick: 7, Enabled: true, Policy: policy.DefaultRoutinePolicy(), Facts: facts}); err != nil {
		t.Fatal(err)
	}
	got, err := planner.Step(ctx)
	if err != nil || got.Reason != BuildingMethodAdmitted {
		t.Fatal(got, err)
	}
	hold := got.Plan
	combat := func() store.GoalState {
		t.Helper()
		review, err := db.LoadRoutineReview(ctx)
		if err != nil {
			t.Fatal(err)
		}
		for _, binding := range review.Goals {
			if binding.Need == policy.ActiveCombat {
				goal, err := db.LoadGoal(ctx, binding.Goal)
				if err != nil {
					t.Fatal(err)
				}
				return goal
			}
		}
		t.Fatal(review.Goals)
		return store.GoalState{}
	}
	if methods := combat().Methods; len(methods) != 1 || !strings.HasPrefix(string(methods[0].Method), "hold-") {
		t.Fatalf("%+v", methods)
	}
	// The raider at the sandbags' outer face is still in front: the hold
	// is existing work.
	native.raider = domain.Cell{X: 9, Z: 21}
	if got, err = planner.Step(ctx); err != nil || got.Reason != BuildingMethodExistingWork {
		t.Fatal(got, err)
	}
	// Past the cover row the hold is cancelled outright.
	native.raider = domain.Cell{X: 12, Z: 22}
	if got, err = planner.Step(ctx); err != nil || got.Reason != BuildingMethodHoldFallback {
		t.Fatal(got, err)
	}
	state, err := db.LoadPlan(ctx, hold)
	if err != nil {
		t.Fatal(err)
	}
	for _, p := range state.Progress {
		if v := p.View(); v.Stage != domain.Cancelled {
			t.Fatalf("%s is %s after the fallback, want cancelled", v.Action, v.Stage)
		}
	}
	// With the hold settled, the intruder is answered by squad defense at
	// the threat, never a second line position.
	if got, err = planner.Step(ctx); err != nil || got.Reason != BuildingMethodAdmitted {
		t.Fatal(got, err)
	}
	methods := combat().Methods
	if len(methods) != 2 || !strings.HasPrefix(string(methods[1].Method), "squad-") {
		t.Fatalf("%+v", methods)
	}
	squad, err := db.LoadPlan(ctx, methods[1].Plan)
	if err != nil {
		t.Fatal(err)
	}
	attacks := 0
	for _, action := range squad.Spec.Actions() {
		if _, ok := action.Movement(); ok {
			t.Fatal("squad defense positioned a defender")
		}
		if a, ok := action.RangedAttack(); ok && a.Target() == "raider" {
			attacks++
		} else if a, ok := action.MeleeAttack(); ok && a.Target() == "raider" {
			attacks++
		}
	}
	if attacks != 2 {
		t.Fatalf("%d attacks on the intruder", attacks)
	}
}

// A raid that switches to a breach toil mid-hold is the same fallback; a
// raider whose evidence is unknown never abandons the line.
func TestRoutineDefenseHoldFallbackNeedsProof(t *testing.T) {
	t.Parallel()
	r, db, session, _, n := routineFixture(t)
	ctx := context.Background()
	native := &raidTestNative{equipTestNative: &equipTestNative{routineNative: n, ids: []string{"a"}}, raider: domain.Cell{X: 9, Z: 5}, toil: "LordToil_AssaultColony"}
	planner, err := NewRoutineDefensePlanner(r, native)
	if err != nil {
		t.Fatal(err)
	}
	snapshot := session.State().Snapshot
	layout := store.DefenseLayoutRecord{World: store.World{Colony: snapshot.Colony, Load: snapshot.Load, Map: snapshot.Map}, Goal: "layout-goal", Complete: true, Toward: domain.North,
		Chokepoint: domain.Cell{X: 9, Z: 10}, Entry: domain.Cell{X: 9, Z: 10}, Firing: []domain.Cell{{X: 9, Z: 23}}, Tiers: []store.DefenseTierRecord{{Name: policy.TierFiringLine, Built: true}}}
	if err := db.SaveDefenseLayout(ctx, layout); err != nil {
		t.Fatal(err)
	}
	current, err := db.LoadRoutineReview(ctx)
	if err != nil {
		t.Fatal(err)
	}
	facts := policy.RoutineFacts{Workers: domain.Known(1), Wood: domain.Known(int64(100)), Hostiles: domain.Known(int64(1)), CriticalPatients: domain.Known(int64(0)), CleanupPawns: domain.Known(false), ColonyNaming: domain.Known(false), ChoiceDialog: domain.Known(false)}
	if _, err = db.ReviewRoutine(ctx, store.RoutineReviewRequest{Revision: current.Revision, Current: snapshot, Tick: 7, Enabled: true, Policy: policy.DefaultRoutinePolicy(), Facts: facts}); err != nil {
		t.Fatal(err)
	}
	if got, err := planner.Step(ctx); err != nil || got.Reason != BuildingMethodAdmitted {
		t.Fatal(got, err)
	}
	native.toil = ""
	if got, err := planner.Step(ctx); err != nil || got.Reason != BuildingMethodExistingWork {
		t.Fatal("unknown toil abandoned the hold:", got, err)
	}
	native.toil = "LordToil_AssaultColonyBreaching"
	if got, err := planner.Step(ctx); err != nil || got.Reason != BuildingMethodHoldFallback {
		t.Fatal(got, err)
	}
}
