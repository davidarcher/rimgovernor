package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// pendingHunt is an unissued hunt action of thing at cell.
func pendingHunt(t *testing.T, action domain.ActionID, thing string, cell domain.Cell) domain.Progress {
	t.Helper()
	value, err := domain.NewAcquisition(thing, "Corpse_Alphabeaver", cell)
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewAcquisitionAction(action, value)
	if err != nil {
		t.Fatal(err)
	}
	spec, err := domain.NewPlan("pest-plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	p, err := domain.NewProgress(spec, action)
	if err != nil {
		t.Fatal(err)
	}
	return p
}

// A hunt follows its animal (#321): only an animal the wild census no
// longer lists cancels its unissued hunt; one that wandered off its
// planned cell keeps it.
func TestGonePestHuntsCancelsGoneAnimalsOnly(t *testing.T) {
	t.Parallel()
	here := domain.Cell{X: 5, Z: 5}
	moved := pendingHunt(t, "hunt-moved", "beaver-moved", here)
	still := pendingHunt(t, "hunt-still", "beaver-still", here)
	left := pendingHunt(t, "hunt-gone", "beaver-gone", here)
	busy := pendingHunt(t, "hunt-busy", "beaver-busy", here)
	progress := []domain.Progress{moved, still, left, busy}
	wild := domain.Known([]policy.UpkeepAnimal{{ID: "beaver-moved", Definition: "Alphabeaver"}, {ID: "beaver-still", Definition: "Alphabeaver"}, {ID: "beaver-busy", Definition: "Alphabeaver"}})
	if gone := gonePestHunts(progress, wild); len(gone) != 1 || gone[0] != "hunt-gone" {
		t.Fatal(gone)
	}
	// An unknown census is not evidence the pack is gone.
	if gone := gonePestHunts(progress, domain.Unknown[[]policy.UpkeepAnimal]()); gone != nil {
		t.Fatal(gone)
	}
	// A dispatched hunt is native's to resolve (death or departure), not
	// this check's to cancel.
	dispatched := dispatchedHunt(t, "hunt-dispatched", "beaver-gone", 100)
	if gone := gonePestHunts([]domain.Progress{dispatched}, wild); gone != nil {
		t.Fatal(gone)
	}
}

func TestPestAcquisitionPlannerRequiresAnAcquisitionNeed(t *testing.T) {
	t.Parallel()
	if _, err := NewRoutineAcquisitionPlanner(nil, policy.ClearPests); err == nil {
		t.Fatal("nil reviewer accepted")
	}
}

func TestPestHuntWithdrawalReleasesTargetForReplanning(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	planner, reviewer, db, facts := pestFixture(t)
	first, err := planner.Step(ctx)
	if err != nil || first.Reason != BuildingMethodAdmitted {
		t.Fatal(first, err)
	}
	plan, err := db.LoadPlan(ctx, first.Plan)
	if err != nil {
		t.Fatal(err)
	}
	action := plan.Spec.Actions()[0]
	target, _ := action.Acquisition()
	snapshot := reviewer.player.session.State().Snapshot
	snapshot.Plan, snapshot.Revision = plan.Spec.ID(), plan.Spec.Revision()
	tick := domain.Tick(facts.Context.GetTick())
	if _, err = db.PrepareAcquisition(ctx, first.Plan, action.ID(), store.AcquisitionAdmission{Snapshot: snapshot, Tick: tick, Thing: target.Thing(), SnapshotToken: "cas"}); err != nil {
		t.Fatal(err)
	}
	if _, err = db.Dispatch(ctx, first.Plan, action.ID(), snapshot, tick); err != nil {
		t.Fatal(err)
	}
	if _, err = db.RecordReceipt(ctx, first.Plan, action.ID(), 1, domain.ReceiptAccepted); err != nil {
		t.Fatal(err)
	}
	for _, row := range facts.Acquisition {
		if row.Source.GetId() == target.Thing() {
			row.Designated = proto.Bool(true)
		}
	}
	facts.PendingHunts = proto.Uint32(1)
	retick(facts.ProtoReflect(), int64(tick)+reviewer.policy.HuntStallTicks)
	if _, err = reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	// The stall rule does not cancel a pest hunt (#455); the cancellation
	// comes from elsewhere (an operator, a goal review).
	if _, err = db.Cancel(ctx, first.Plan, action.ID()); err != nil {
		t.Fatal(err)
	}
	if result, err := planner.Step(ctx); err != nil || result.Reason != BuildingMethodUsed {
		t.Fatal(result, err)
	}
	plan, err = db.LoadPlan(ctx, first.Plan)
	if err != nil || plan.Progress[0].View().Stage != domain.Cancelled || !domain.GoalWorkOpen(plan.Progress) {
		t.Fatal("cancelled hunt must retain uncertainty until native withdrawal", plan, err)
	}
	// Reconciliation observes the still-designated hunt, withdraws it, then
	// observes no designation and no kill. No simulation tick is needed.
	observation := domain.Observation{Action: action.ID(), Attempt: 1, Snapshot: snapshot, Tick: domain.Tick(facts.Context.GetTick()), Effect: domain.EffectPending, Causality: domain.AfterDispatch}
	if _, err = db.Observe(ctx, first.Plan, observation, snapshot); err != nil {
		t.Fatal(err)
	}
	if _, err = db.Withdraw(ctx, first.Plan, action.ID(), snapshot, observation.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err = db.RecordReceipt(ctx, first.Plan, action.ID(), 2, domain.ReceiptAccepted); err != nil {
		t.Fatal(err)
	}
	observation.Attempt, observation.Effect, observation.UnsuccessfulReason = 2, domain.EffectUnsuccessful, domain.OutcomeNotAchieved
	if _, err = db.Observe(ctx, first.Plan, observation, snapshot); err != nil {
		t.Fatal(err)
	}
	for _, row := range facts.Acquisition {
		row.Designated = proto.Bool(false)
	}
	facts.PendingHunts = proto.Uint32(0)
	reviewer.census.invalidate()
	if _, err = reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	next, err := planner.Step(ctx)
	if err != nil || next.Reason != BuildingMethodAdmitted || next.Plan == first.Plan {
		t.Fatal("withdrawn pest did not get a fresh method", next, err)
	}
	plan, err = db.LoadPlan(ctx, next.Plan)
	if err != nil {
		t.Fatal(err)
	}
	snapshot.Plan = next.Plan
	if work, _, err := clockSchedulerWork(plan, snapshot); err != nil || !work {
		t.Fatal("replacement hunt left clock at no_work", work, err)
	}
}

// pestFixture stages one wild alphabeaver in the wild-animal census and as a
// pest hunt row, with a hunting budget of two and no colonist restrictions.
func pestFixture(t *testing.T) (*RoutineAcquisitionPlanner, *RoutineReviewer, *store.Store, *o.ColonyFactsSnapshot) {
	t.Helper()
	reviewer, db, _, _, native := routineFixture(t)
	v := native.reply.GetObserved()
	v.ColonistCount, v.WorkerCount = proto.Uint32(1), proto.Uint32(1)
	v.PendingHunts = proto.Uint32(0)
	v.Upkeep = &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: &o.UpkeepFacts{
		Completeness: hospitalCount(1),
		Comfort:      &o.ComfortSection{Outcome: &o.ComfortSection_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_REQUESTED.Enum()}}},
	}}}
	addPest(v, "beaver-1", 7, 7)
	reviewer.methods = domain.Known([]policy.GoalID{policy.ClearPests})
	if _, err := reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoutineAcquisitionPlanner(reviewer, policy.ClearPests)
	if err != nil {
		t.Fatal(err)
	}
	return planner, reviewer, db, v
}

// addPest puts a wild alphabeaver at a cell into both censuses of the
// native reply: the wild-animal census (what the goal counts) and the
// acquisition census (what the planner hunts).
func addPest(v *o.ColonyFactsSnapshot, id string, x, z int32) {
	beaver := &o.EntityRef{Id: proto.String(id), DefName: proto.String("Alphabeaver"), MapId: v.Context.Identity.MapId, Position: &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)}}
	upkeep := v.Upkeep.GetObserved()
	upkeep.WildAnimals = append(upkeep.WildAnimals, &o.AnimalFeed{Pawn: &o.PawnState{Pawn: beaver, Wild: proto.Bool(true), AnimalState: &o.AnimalState{Tameable: proto.Bool(true), Tame: proto.Bool(false), MinimumHandlingSkill: proto.Int32(8)}}, Diet: proto.String("DendrovoreAnimal"), RequiresPen: proto.Bool(false)})
	v.Acquisition = append(v.Acquisition, &o.AcquisitionFacts{Source: &o.EntityRef{Id: proto.String(id), DefName: proto.String("Alphabeaver"), MapId: v.Context.Identity.MapId, Position: &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)}, Snapshot: &o.SnapshotRef{EntityId: proto.String(id), Token: proto.String("cas"), Context: proto.Clone(v.Context).(*c.ObservationContext)}}, RevengeChance: proto.Float64(0.1), HerdSize: proto.Uint32(3), MeleeOnly: proto.Bool(false), Downed: proto.Bool(false), WeaponRange: proto.Float64(30), Resource: proto.String("Corpse_Alphabeaver"), Hunt: proto.Bool(true), Tree: proto.Bool(false), Food: proto.Bool(false), Designated: proto.Bool(false), Yield: proto.Float64(1), NutritionYield: proto.Float64(0)})
}

func TestPestAcquisitionPlannerAdmitsOneHuntPerPest(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	planner, reviewer, db, v := pestFixture(t)
	result, err := planner.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	plan, err := db.LoadPlan(ctx, result.Plan)
	if err != nil || len(plan.Progress) != 1 {
		t.Fatal(plan, err)
	}
	acquisition, ok := plan.Progress[0].Action().Acquisition()
	if !ok || acquisition.Thing() != "beaver-1" {
		t.Fatal(plan.Progress[0])
	}
	// The pack's only animal has its hunt: nothing more to plan.
	if next, err := planner.Step(ctx); err != nil || next.Reason != BuildingMethodUsed {
		t.Fatal(next, err)
	}
	// A second animal is planned beside the open hunt, not behind it:
	// the pack is hunted animal by animal, two hunts outstanding at a
	// time, and one hunt awaiting its kill never holds the next back.
	addPest(v, "beaver-2", 9, 9)
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	second, err := planner.Step(ctx)
	if err != nil || second.Reason != BuildingMethodAdmitted || second.Plan == result.Plan {
		t.Fatal(second, err)
	}
	plan, err = db.LoadPlan(ctx, second.Plan)
	if err != nil || len(plan.Progress) != 1 {
		t.Fatal(plan, err)
	}
	if acquisition, ok := plan.Progress[0].Action().Acquisition(); !ok || acquisition.Thing() != "beaver-2" {
		t.Fatal(plan.Progress[0])
	}
	if next, err := planner.Step(ctx); err != nil || next.Reason != BuildingMethodUsed {
		t.Fatal(next, err)
	}
}

// A hunt follows its animal (#321): beavers wandering off their planned
// cells leave both hunts open and nothing re-planned; a dispatched hunt of
// a dispatched hunt is never stall-cancelled, the kill is its exit.
func TestPestAcquisitionPlannerFollowsStrayedAndDownedAnimals(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	planner, reviewer, db, v := pestFixture(t)
	first, err := planner.Step(ctx)
	if err != nil || first.Reason != BuildingMethodAdmitted {
		t.Fatal(first, err)
	}
	addPest(v, "beaver-2", 9, 9)
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	second, err := planner.Step(ctx)
	if err != nil || second.Reason != BuildingMethodAdmitted {
		t.Fatal(second, err)
	}
	// Both beavers wander off their planned cells.
	for _, row := range v.Acquisition {
		if row.Source.GetDefName() == "Alphabeaver" {
			row.Source.Position = &c.Cell{X: proto.Int32(row.Source.Position.GetX() + 1), Z: proto.Int32(row.Source.Position.GetZ())}
		}
	}
	retick(v.ProtoReflect(), v.Context.GetTick()+2500)
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	if result, err := planner.Step(ctx); err != nil || result.Reason != BuildingMethodUsed {
		t.Fatal(result, err)
	}
	for _, id := range []domain.PlanID{first.Plan, second.Plan} {
		plan, err := db.LoadPlan(ctx, id)
		if err != nil || len(plan.Progress) != 1 || plan.Progress[0].View().Stage != domain.Pending {
			t.Fatal("a strayed hunt must stay open", id, plan, err)
		}
	}
	// The first hunt is dispatched and its beaver still alive on the map:
	// the hunt-stall grace passes and the hunt stays dispatched (#455), and
	// so it does once the beaver is downed.
	plan, err := db.LoadPlan(ctx, first.Plan)
	if err != nil {
		t.Fatal(err)
	}
	action := plan.Spec.Actions()[0]
	target, _ := action.Acquisition()
	snapshot := reviewer.player.session.State().Snapshot
	snapshot.Plan, snapshot.Revision = plan.Spec.ID(), plan.Spec.Revision()
	tick := domain.Tick(v.Context.GetTick())
	if _, err = db.PrepareAcquisition(ctx, first.Plan, action.ID(), store.AcquisitionAdmission{Snapshot: snapshot, Tick: tick, Thing: target.Thing(), SnapshotToken: "cas"}); err != nil {
		t.Fatal(err)
	}
	if _, err = db.Dispatch(ctx, first.Plan, action.ID(), snapshot, tick); err != nil {
		t.Fatal(err)
	}
	if _, err = db.RecordReceipt(ctx, first.Plan, action.ID(), 1, domain.ReceiptAccepted); err != nil {
		t.Fatal(err)
	}
	for _, row := range v.Acquisition {
		if row.Source.GetId() == target.Thing() {
			row.Designated = proto.Bool(true)
		}
	}
	v.PendingHunts = proto.Uint32(1)
	for _, downed := range []bool{false, true} {
		for _, row := range v.Acquisition {
			if row.Source.GetId() == target.Thing() {
				row.Downed = proto.Bool(downed)
			}
		}
		retick(v.ProtoReflect(), v.Context.GetTick()+reviewer.policy.HuntStallTicks)
		reviewer.census.invalidate()
		if _, err = reviewer.Step(ctx); err != nil {
			t.Fatal(err)
		}
		if result, err := planner.Step(ctx); err != nil || result.Reason != BuildingMethodUsed {
			t.Fatal(result, err)
		}
		plan, err = db.LoadPlan(ctx, first.Plan)
		if err != nil || plan.Progress[0].View().Stage != domain.AwaitingObservation {
			t.Fatal("a dispatched pest hunt must not be stall-cancelled", downed, plan, err)
		}
	}
}

// retick moves every observation context nested in a native reply to one
// tick, the way a later read of the same colony would carry it.
func retick(m protoreflect.Message, tick int64) {
	if context, ok := m.Interface().(*c.ObservationContext); ok {
		context.Tick = proto.Int64(tick)
		return
	}
	m.Range(func(field protoreflect.FieldDescriptor, value protoreflect.Value) bool {
		switch {
		case field.IsList() && field.Message() != nil:
			list := value.List()
			for i := 0; i < list.Len(); i++ {
				retick(list.Get(i).Message(), tick)
			}
		case field.IsMap():
		case field.Message() != nil:
			retick(value.Message(), tick)
		}
		return true
	})
}

// A hunt needs a hunter (#447): with the roster known and nobody holding a
// ranged weapon, the hunting budget is zero and the pest goes unplanned;
// arming the colonist with a bow admits the hunt.
func TestPestAcquisitionPlannerNeedsARangedHunter(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	planner, reviewer, _, v := pestFixture(t)
	n := reviewer.native.(*routineNative)
	// The emergency census names the colonist so the roster is known.
	reviewer.native = &healthyWorkNative{routineMedicalNative: &routineMedicalNative{routineNative: n}}
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	row := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("patient"), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(false)}, Biography: &o.PawnBiography{Skills: []*o.Skill{{Definition: &o.DefinitionRef{DefName: proto.String("Shooting")}, Level: proto.Int32(6), Disabled: proto.Bool(false), Passion: proto.String("None")}}}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true)}, Issues: []*o.ReadIssue{missing("pawn.snapshot"), missing("mental_state")}}
	n.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: []*o.PawnState{row}, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(1), Returned: proto.Uint64(1), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}}}
	reviewer.census.invalidate()
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	if result, err := planner.Step(ctx); err != nil || result.Reason != BuildingMethodUsed {
		t.Fatal("an unarmed roster must not be handed a hunt", result, err)
	}
	row.Equipment = &o.PawnEquipment{Armed: proto.Bool(true), PrimaryId: proto.String("bow"), Equipped: []*o.GearItem{{Thing: &o.EntityRef{Id: proto.String("bow"), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Ranged: proto.Bool(true)}}}
	reviewer.census.invalidate()
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	if result, err := planner.Step(ctx); err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal("a bow-armed shooter admits the hunt", result, err)
	}
}
