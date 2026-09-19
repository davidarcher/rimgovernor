package buildingruntime

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// resourceNative is the workshop fixture plus the bill path's reads: the
// ingredient stock and an accepting bill preview. The mine/harvest fallback
// is never reached with a producing bench standing, so those reads fail.
type resourceNative struct {
	*workshopNative
	stock    []policy.Stock
	previews []domain.ProductionBill
}

// Two live colonists so the review knows its workers and ranks
// MaintainResource into a development slot (see routine_waste_test.go).
func (n *resourceNative) ReadEmergency(ctx context.Context, identity *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	v, receipt, err := n.routineNative.ReadEmergency(ctx, identity)
	v.Facts.Colonists = []policy.EmergencyPawn{
		{ID: "crafter", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)},
		{ID: "builder", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)},
	}
	return v, receipt, err
}

func (n *resourceNative) ReadSupplyStock(context.Context, *c.Identity, []string) ([]policy.Stock, bridge.Result, error) {
	return n.stock, bridge.Result{}, nil
}

func (n *resourceNative) PreviewBill(_ context.Context, _ *c.Identity, bill domain.ProductionBill) (*op.PreviewReply, bridge.Result, error) {
	n.previews = append(n.previews, bill)
	return &op.PreviewReply{Outcome: &op.PreviewReply_Evaluated{Evaluated: &op.PreviewEvaluation{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Accepted: proto.Bool(true)}}}, bridge.Result{}, nil
}

func (n *resourceNative) ReadResourceSources(context.Context, *c.Identity, string) ([]bridge.ResourceSourceRow, policy.ResourceStorage, bridge.Result, error) {
	return nil, policy.ResourceStorage{}, bridge.Result{}, errors.New("no sources in this fixture")
}

func (n *resourceNative) PreviewAcquisition(context.Context, *c.Identity, bridge.AcquisitionTarget) (*op.PreviewReply, bridge.Result, error) {
	return nil, bridge.Result{}, errors.New("no acquisition in this fixture")
}

func (n *resourceNative) PreviewZone(context.Context, *c.Identity, bridge.ZoneTarget) (*op.PreviewReply, bridge.Result, error) {
	return nil, bridge.Result{}, errors.New("no zone in this fixture")
}

// The journal-level dry run of the bill path a live workshop run only
// reaches after ~95k ticks: with a CraftingSpot standing, its club recipe
// funded and the native preview accepting, one resource step commits a
// StockTarget bill method on MaintainResource through the real store's
// admission (which once bound bills to the food goals only), and the next
// step sees that open work rather than a second bill.
func TestResourceDispatchCommitsWorkshopBillThroughTheJournal(t *testing.T) {
	t.Parallel()
	base, db, _, _, sleeping := sleepingFixture(t)
	base.reviewer.policy.ResourceTargets = map[policy.Resource]int64{"MeleeWeapon_Club": 3}
	sleeping.reply.GetObserved().Resources = []*o.Quantity{{DefName: proto.String("MeleeWeapon_Club"), Units: proto.Int64(0)}}
	club := policy.GearRecipe{
		Definition: "Make_MeleeWeapon_Club", Products: []policy.Resource{"MeleeWeapon_Club"},
		Available: domain.Known(true), AvailableOn: domain.Known(true),
		Ingredients:  domain.Known([][]policy.Amount{{{Resource: "WoodLog", Count: 40}}}),
		RequiredWork: domain.Known([]policy.WorkRequirement{{Work: "Crafting", Skill: "Crafting"}}),
	}
	native := &resourceNative{
		workshopNative: &workshopNative{sleepingNative: sleeping, benches: []bridge.GearBenchRead{{Token: "bench-cas", Bench: policy.GearBench{ID: "Thing_CraftingSpot1", Bills: domain.Known([]policy.GearBill{}), Recipes: domain.Known([]policy.GearRecipe{club})}}}},
		stock:          []policy.Stock{{Resource: "WoodLog", Available: domain.Known(int64(200))}},
	}
	// The review must see the deficit and rank MaintainResource into a
	// development slot: the fixture's player Wall plan holds one, so two
	// workers give the capacity for a second.
	v := sleeping.reply.GetObserved()
	v.ColonistCount = proto.Uint32(2)
	v.WorkerCount = proto.Uint32(2)
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	worker := func(id string) *o.PawnState {
		return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(true)}, Biography: &o.PawnBiography{}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true)}, Issues: []*o.ReadIssue{missing("pawn.snapshot"), missing("mental_state")}}
	}
	sleeping.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: []*o.PawnState{worker("crafter"), worker("builder")}, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(2), Returned: proto.Uint64(2), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}}}
	base.reviewer.native = native
	got, err := base.reviewer.Step(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	selected := false
	for _, row := range got.Review.Development.Rows {
		selected = selected || row.Goal == policy.MaintainResource && row.Selected
	}
	if !selected {
		t.Fatal("MaintainResource holds no development slot", got.Review.Development.Rows)
	}
	planner, err := NewRoutineResourcePlanner(base.reviewer, native)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(context.Background())
	if err != nil || result.Reason != BuildingMethodAdmitted || result.Plan == "" {
		t.Fatal(result, err)
	}
	if len(native.previews) != 1 || native.previews[0].Bench() != "Thing_CraftingSpot1" || native.previews[0].Recipe() != "Make_MeleeWeapon_Club" || native.previews[0].BeforeToken() != "bench-cas" {
		t.Fatal(native.previews)
	}
	plan, err := db.LoadPlan(context.Background(), result.Plan)
	if err != nil || len(plan.Spec.Actions()) != 1 {
		t.Fatal(plan, err)
	}
	bill, ok := plan.Spec.Actions()[0].ProductionBill()
	if !ok || bill.Target() != 3 || bill.Mode() != domain.StockTarget {
		t.Fatal(bill, ok)
	}
	again, err := planner.Step(context.Background())
	if err != nil || again.Reason != BuildingMethodExistingWork || len(native.previews) != 1 {
		t.Fatal(again, err, native.previews)
	}
}

// MaintainAnimalFeed's delivery constraint on the shared tail: a bench set
// that excludes every standing bench refuses the bill without a preview
// (producing where the animal cannot eat only piles feed up, #237), and one
// that names the bench lets the same step commit the bill there.
func TestResourceDispatchHonoursTheBenchFilter(t *testing.T) {
	t.Parallel()
	base, _, _, _, sleeping := sleepingFixture(t)
	base.reviewer.policy.ResourceTargets = map[policy.Resource]int64{"MeleeWeapon_Club": 3}
	sleeping.reply.GetObserved().Resources = []*o.Quantity{{DefName: proto.String("MeleeWeapon_Club"), Units: proto.Int64(0)}}
	club := policy.GearRecipe{
		Definition: "Make_MeleeWeapon_Club", Products: []policy.Resource{"MeleeWeapon_Club"},
		Available: domain.Known(true), AvailableOn: domain.Known(true),
		Ingredients:  domain.Known([][]policy.Amount{{{Resource: "WoodLog", Count: 40}}}),
		RequiredWork: domain.Known([]policy.WorkRequirement{{Work: "Crafting", Skill: "Crafting"}}),
	}
	native := &resourceNative{
		workshopNative: &workshopNative{sleepingNative: sleeping, benches: []bridge.GearBenchRead{{Token: "bench-cas", Bench: policy.GearBench{ID: "Thing_CraftingSpot1", Bills: domain.Known([]policy.GearBill{}), Recipes: domain.Known([]policy.GearRecipe{club})}}}},
		stock:          []policy.Stock{{Resource: "WoodLog", Available: domain.Known(int64(200))}},
	}
	v := sleeping.reply.GetObserved()
	v.ColonistCount = proto.Uint32(2)
	v.WorkerCount = proto.Uint32(2)
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	worker := func(id string) *o.PawnState {
		return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(true)}, Biography: &o.PawnBiography{}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true)}, Issues: []*o.ReadIssue{missing("pawn.snapshot"), missing("mental_state")}}
	}
	sleeping.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: []*o.PawnState{worker("crafter"), worker("builder")}, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(2), Returned: proto.Uint64(2), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}}}
	base.reviewer.native = native
	if _, err := base.reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoutineResourcePlanner(base.reviewer, native)
	if err != nil {
		t.Fatal(err)
	}
	p := base.reviewer.player
	ctx, epoch, done, err := p.enter(context.Background(), false)
	if err != nil {
		t.Fatal(err)
	}
	defer done()
	state := p.session.State()
	review, err := p.journal.LoadRoutineReview(ctx)
	if err != nil {
		t.Fatal(err)
	}
	var goal store.GoalState
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainResource {
			if goal, err = p.journal.LoadGoal(ctx, binding.Goal); err != nil {
				t.Fatal(err)
			}
		}
	}
	if goal.Goal.Status != domain.GoalActive {
		t.Fatal(goal)
	}
	identity := boundary.Identity(state.Snapshot)
	stock := resourceStockFacts(v)
	result, err := planner.dispatchResourceGoal(ctx, epoch, state, goal, review.Tick, identity, "MeleeWeapon_Club", 3, stock, []string{}, base.reviewer.clock.Now())
	if err != nil || result.Reason != BuildingMethodRefused || result.NativeWorkTicks != stockWaitTicks || len(native.previews) != 0 {
		t.Fatal(result, err, native.previews)
	}
	result, err = planner.dispatchResourceGoal(ctx, epoch, state, goal, review.Tick, identity, "MeleeWeapon_Club", 3, stock, []string{"Thing_ButcherSpot9"}, base.reviewer.clock.Now())
	if err != nil || result.Reason != BuildingMethodRefused || len(native.previews) != 0 {
		t.Fatal(result, err, native.previews)
	}
	result, err = planner.dispatchResourceGoal(ctx, epoch, state, goal, review.Tick, identity, "MeleeWeapon_Club", 3, stock, []string{"Thing_CraftingSpot1"}, base.reviewer.clock.Now())
	if err != nil || result.Reason != BuildingMethodAdmitted || len(native.previews) != 1 || native.previews[0].Bench() != "Thing_CraftingSpot1" {
		t.Fatal(result, err, native.previews)
	}
}
