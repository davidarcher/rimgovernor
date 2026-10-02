package buildingruntime

import (
	"context"
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
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
	return reply, r, err
}

// batchOrders is one combat.orders batch as pawn -> "move x,z" or
// "attack target".
func batchOrders(batch *op.CombatOrders) map[string]string {
	out := map[string]string{}
	for _, order := range batch.Orders {
		switch v := order.Order.(type) {
		case *op.CombatOrder_Move:
			out[order.GetPawn().GetEntityId()] = fmt.Sprintf("move %d,%d", v.Move.GetX(), v.Move.GetZ())
		case *op.CombatOrder_Attack:
			out[order.GetPawn().GetEntityId()] = "attack " + v.Attack.GetEntityId()
		}
	}
	return out
}

// One ActiveCombat plan owns the fight (#852): it holds the defenders'
// drafts, and each stop sends only the changed orders, recorded as its
// evidence. The hold-the-line formation moves the riflemen to the firing
// cells; a steady stop orders and records nothing; once a live raider is
// past the cover row the formation re-forms as squad defense on the
// intruder (#118 breach fallback) in the same plan.
func TestRoutineDefenseAbandonsACrossedHoldForSquadDefense(t *testing.T) {
	t.Parallel()
	crossedHoldFight(t)
}

// crossedHoldFight plays the crossed-hold fight; the combat replay harness
// records it (#853).
func crossedHoldFight(t *testing.T) {
	r, db, session, _, n := routineFixture(t)
	ctx := context.Background()
	// The corridor runs north: firing cells at z=23 behind sandbags at
	// z=22, the raider walking in from the south.
	native := &raidTestNative{equipTestNative: &equipTestNative{routineNative: n, ids: []string{"a", "b"}}, raider: domain.Cell{X: 9, Z: 5}, toil: "LordToil_AssaultColony"}
	planner, err := NewRoutineDefensePlanner(r, framed{native})
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
	plan := got.Plan
	fight, ok, err := db.LoadCombatFight(ctx, plan)
	if err != nil || !ok || !fight.Open || fight.Memory.Tactic != policy.TacticHold || len(fight.Memory.Roles) != 2 {
		t.Fatalf("%+v %v %v", fight, ok, err)
	}
	// The fight's plan has no actions (#910): the admission stop's one
	// batch drafts both defenders and moves them to the firing cells, and
	// the fight holds the claims the drafts made.
	if state, err := db.LoadPlan(ctx, plan); err != nil || len(state.Spec.Actions()) != 0 {
		t.Fatal("the fight's plan carries actions", err)
	}
	if len(native.orders.batches) != 1 {
		t.Fatal(native.orders.batches)
	}
	admission := native.orders.batches[0].Orders
	if len(admission) != 4 || admission[0].GetDraft() == nil || admission[1].GetDraft() == nil {
		t.Fatal("the admission batch does not draft first", admission)
	}
	if orders := batchOrders(native.orders.batches[0]); orders["a"] != "move 9,23" || orders["b"] != "move 8,23" {
		t.Fatal(orders)
	}
	if fight, _, _ = db.LoadCombatFight(ctx, plan); !fight.Roster["a"] || !fight.Roster["b"] {
		t.Fatal("the fight rosters no drafts", fight.Roster)
	}
	// Nothing changed: no orders, no evidence.
	native.raider = domain.Cell{X: 9, Z: 21}
	if got, err = planner.Step(ctx); err != nil || got.Reason != BuildingMethodExistingWork || len(native.orders.batches) != 1 {
		t.Fatal(got, err)
	}
	if evidence, err := db.CombatEvidence(ctx, plan); err != nil || len(evidence) != 1 || len(evidence[0].Orders) != 2 || !evidence[0].Orders[0].Applied {
		t.Fatalf("%+v %v", evidence, err)
	}
	// Past the cover row the same fight re-forms as squad defense on the
	// intruder, never a second plan.
	native.raider = domain.Cell{X: 12, Z: 22}
	if got, err = planner.Step(ctx); err != nil || got.Reason != BuildingMethodCombatOrders || got.Plan != plan || len(native.orders.batches) != 2 {
		t.Fatal(got, err)
	}
	if orders := batchOrders(native.orders.batches[1]); orders["a"] != "attack raider" || orders["b"] != "attack raider" {
		t.Fatal(orders)
	}
	if fight, _, _ = db.LoadCombatFight(ctx, plan); fight.Memory.Tactic != policy.TacticSquad {
		t.Fatal(fight.Memory)
	}
	// The raid over, the fight closes and its drafts are no longer held.
	review, err := db.LoadRoutineReview(ctx)
	if err != nil {
		t.Fatal(err)
	}
	facts.Hostiles = domain.Known(int64(0))
	if _, err = db.ReviewRoutine(ctx, store.RoutineReviewRequest{Revision: review.Revision, Current: snapshot, Tick: 9, Enabled: true, Policy: policy.DefaultRoutinePolicy(), Facts: facts}); err != nil {
		t.Fatal(err)
	}
	if got, err = planner.Step(ctx); err != nil || got.Reason != BuildingMethodNoDeficit {
		t.Fatal(got, err)
	}
	if fight, _, _ = db.LoadCombatFight(ctx, plan); fight.Open || len(fight.Roster) != 2 {
		t.Fatal("the fight stayed open after recovery, or dropped its roster", fight)
	}
}

// A raid that switches to a breach toil mid-hold is the same fallback; a
// raider whose evidence is unknown never abandons the line.
func TestRoutineDefenseHoldFallbackNeedsProof(t *testing.T) {
	t.Parallel()
	r, db, session, _, n := routineFixture(t)
	ctx := context.Background()
	native := &raidTestNative{equipTestNative: &equipTestNative{routineNative: n, ids: []string{"a"}}, raider: domain.Cell{X: 9, Z: 5}, toil: "LordToil_AssaultColony"}
	planner, err := NewRoutineDefensePlanner(r, framed{native})
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

func (n *raidTestNative) ReadRoutineFrame(ctx context.Context, id *c.Identity) (bridge.RoutineFrame, error) {
	return fakeFrame(ctx, n, id)
}
