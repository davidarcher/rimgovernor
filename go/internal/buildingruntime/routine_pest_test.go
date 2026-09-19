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

func TestMovedPestHuntsCancelsMovedAndGoneAnimalsOnly(t *testing.T) {
	t.Parallel()
	here := domain.Cell{X: 5, Z: 5}
	moved := pendingHunt(t, "hunt-moved", "beaver-moved", here)
	still := pendingHunt(t, "hunt-still", "beaver-still", here)
	left := pendingHunt(t, "hunt-gone", "beaver-gone", here)
	busy := pendingHunt(t, "hunt-busy", "beaver-busy", here)
	progress := []domain.Progress{moved, still, left, busy}
	sources := domain.Known([]policy.AcquisitionSource{
		{ID: "beaver-moved", Definition: "Alphabeaver", Hunt: true, Cell: domain.Cell{X: 9, Z: 5}},
		{ID: "beaver-still", Definition: "Alphabeaver", Hunt: true, Cell: here},
	})
	wild := domain.Known([]policy.UpkeepAnimal{{ID: "beaver-moved", Definition: "Alphabeaver"}, {ID: "beaver-still", Definition: "Alphabeaver"}, {ID: "beaver-busy", Definition: "Alphabeaver"}})
	gone, strayed := movedPestHunts(progress, sources, wild)
	if len(gone) != 1 || gone[0] != "hunt-gone" || len(strayed) != 1 || strayed[0] != "hunt-moved" {
		t.Fatal(gone, strayed)
	}
	// An unknown census is not evidence the pack moved.
	if gone, strayed := movedPestHunts(progress, domain.Unknown[[]policy.AcquisitionSource](), wild); gone != nil || strayed != nil {
		t.Fatal(gone, strayed)
	}
	if gone, strayed := movedPestHunts(progress, sources, domain.Unknown[[]policy.UpkeepAnimal]()); gone != nil || strayed != nil {
		t.Fatal(gone, strayed)
	}
	// A dispatched hunt is native's to resolve (stalledHuntActions), not
	// this check's to cancel.
	dispatched := dispatchedHunt(t, "hunt-dispatched", "beaver-gone", 100)
	if gone, strayed := movedPestHunts([]domain.Progress{dispatched}, sources, wild); gone != nil || strayed != nil {
		t.Fatal(gone, strayed)
	}
}

func TestPestAcquisitionPlannerRequiresAnAcquisitionNeed(t *testing.T) {
	t.Parallel()
	if _, err := NewRoutineAcquisitionPlanner(nil, policy.ClearPests); err == nil {
		t.Fatal("nil reviewer accepted")
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
	v.Acquisition = append(v.Acquisition, &o.AcquisitionFacts{Source: &o.EntityRef{Id: proto.String(id), DefName: proto.String("Alphabeaver"), MapId: v.Context.Identity.MapId, Position: &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)}, Snapshot: &o.SnapshotRef{EntityId: proto.String(id), Token: proto.String("cas"), Context: proto.Clone(v.Context).(*c.ObservationContext)}}, Resource: proto.String("Corpse_Alphabeaver"), Hunt: proto.Bool(true), Tree: proto.Bool(false), Food: proto.Bool(false), Designated: proto.Bool(false), Yield: proto.Float64(1), NutritionYield: proto.Float64(0)})
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

// A strayed hunt's grace clock runs per action, whichever plan holds it:
// with two plans open, the second plan's pass must not forget the
// first's clocks (run 10 of #247 never re-planned either beaver).
func TestPestAcquisitionPlannerReplansStrayedHuntsAcrossPlans(t *testing.T) {
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
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	// The first pass starts both clocks and re-plans nothing.
	if result, err := planner.Step(ctx); err != nil || result.Reason != BuildingMethodUsed {
		t.Fatal(result, err)
	}
	if len(planner.strayed) != 2 {
		t.Fatal(planner.strayed)
	}
	retick(v.ProtoReflect(), v.Context.GetTick()+pestStrayGraceTicks)
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	for _, id := range []domain.PlanID{first.Plan, second.Plan} {
		plan, err := db.LoadPlan(ctx, id)
		if err != nil || len(plan.Progress) != 1 || plan.Progress[0].View().Stage != domain.Cancelled {
			t.Fatal(id, plan, err)
		}
	}
	plan, err := db.LoadPlan(ctx, result.Plan)
	if err != nil || len(plan.Progress) != 2 {
		t.Fatal(plan, err)
	}
	if len(planner.strayed) != 0 {
		t.Fatal(planner.strayed)
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
