package buildingruntime

import (
	"context"
	"errors"
	"reflect"
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

func TestGearPlannerAssignsPolicyBeforeWearOrProduction(t *testing.T) {
	reviewer, db, _, _, native := routineFixture(t)
	setGearProductionNeed(native.reply.GetObserved())
	gear := native.reply.GetObserved().GetPlanning().GetObserved().GetGear()
	gear.Pawns[0].ApparelPolicy = &o.ApparelPolicyState{Token: proto.String("policy-cas"), Name: proto.String("Player custom"), Child: proto.Bool(false), Slave: proto.Bool(false), IncapableOfViolence: proto.Bool(false), Drafted: proto.Bool(false), MinHitPoints: proto.Float32(0), MaxHitPoints: proto.Float32(1), MinQuality: proto.Int32(0), MaxQuality: proto.Int32(6), ExcludesTainted: proto.Bool(false), Definitions: []*o.ApparelPolicyDefinition{{DefName: proto.String("Apparel_BasicShirt"), Adult: proto.Bool(true), Armor: proto.Bool(false), Child: proto.Bool(false)}}}
	n := &gearProductionNative{gearTestNative: &gearTestNative{equipTestNative: &equipTestNative{routineNative: native, ids: []string{"a", "b"}}}}
	reviewer.native = n
	reviewer.methods = domain.Known([]policy.GoalID{policy.MaintainEquipment})
	if _, err := reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoutineGearPlanner(reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(context.Background())
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	state, err := db.LoadPlan(context.Background(), result.Plan)
	if err != nil {
		t.Fatal(err)
	}
	value, ok := state.Spec.Actions()[0].ApparelPolicy()
	if !ok || value.Pawn() != "a" || value.Spec().Name != "RimGovernor worker" || value.Spec().Token != "policy-cas" {
		t.Fatal(value, ok)
	}
	if len(n.previews) != 0 {
		t.Fatal("bill preview before apparel assignment")
	}
}

type gearProductionNative struct {
	*gearTestNative
	previews []domain.ProductionBill
	refuse   bool
}

func (n *gearProductionNative) ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error) {
	recipe := policy.GearRecipe{Definition: "Make_Apparel_BasicShirt", Products: []policy.Resource{"Apparel_BasicShirt"}, Available: domain.Known(true), AvailableOn: domain.Known(true), Ingredients: domain.Known([][]policy.Amount{{{Resource: "Cloth", Count: 40}, {Resource: "Leather_Plain", Count: 40}}}), RequiredWork: domain.Known([]policy.WorkRequirement{{Work: "Tailoring", Skill: "Crafting"}})}
	return []bridge.GearBenchRead{{Token: "bench-cas", Bench: policy.GearBench{ID: "tailor", Bills: domain.Known([]policy.GearBill{}), Recipes: domain.Known([]policy.GearRecipe{recipe})}}}, bridge.Result{}, nil
}
func (n *gearProductionNative) ReadSupplyStock(context.Context, *c.Identity, []string) ([]policy.Stock, bridge.Result, error) {
	return []policy.Stock{{Resource: "Cloth", Available: domain.Known(int64(100))}, {Resource: "Leather_Plain", Available: domain.Known(int64(100))}}, bridge.Result{}, nil
}
func (n *gearProductionNative) PreviewBill(_ context.Context, _ *c.Identity, bill domain.ProductionBill) (*op.PreviewReply, bridge.Result, error) {
	n.previews = append(n.previews, bill)
	return &op.PreviewReply{Outcome: &op.PreviewReply_Evaluated{Evaluated: &op.PreviewEvaluation{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Accepted: proto.Bool(!n.refuse)}}}, bridge.Result{}, nil
}

func setGearProductionNeed(v *o.ColonyFactsSnapshot) {
	v.ColonistCount = proto.Uint32(2)
	v.WorkerCount = proto.Uint32(2)
	v.Issues = append(v.Issues, &o.ReadIssue{Field: proto.String("naming"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}})
	complete := func(n uint64) *o.Completeness {
		return &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(n), Returned: proto.Uint64(n), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}
	}
	gear := &o.GearSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Completeness: complete(2)}
	for _, id := range []string{"a", "b"} {
		pawn := &o.GearLoadout{Snapshot: &o.SnapshotRef{Context: proto.Clone(v.Context).(*c.ObservationContext), EntityId: proto.String(id), Token: proto.String("loadout-" + id)}, Pawn: &o.EntityRef{Id: proto.String(id), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Equipment: &o.PawnEquipment{Armed: proto.Bool(true)}, Deficit: proto.Bool(id == "a"), Completeness: complete(0)}
		gear.Pawns = append(gear.Pawns, pawn)
	}
	gear.Pawns[0].ReplacementNeeds = []*o.GearReplacementNeed{{DefName: proto.String("Apparel_BasicShirt"), Stuff: proto.String("Cloth"), Reason: proto.String("wear")}}
	v.Planning.GetObserved().Gear = gear
}

func TestGearProductionPreviewsAndPersistsOnlyFundedMaterials(t *testing.T) {
	t.Parallel()
	for _, refuse := range []bool{false, true} {
		reviewer, db, session, _, native := routineFixture(t)
		setGearProductionNeed(native.reply.GetObserved())
		gear := native.reply.GetObserved().GetPlanning().GetObserved().Gear
		gear.Pawns[1].Deficit = proto.Bool(true)
		gear.Pawns[1].ReplacementNeeds = []*o.GearReplacementNeed{proto.Clone(gear.Pawns[0].ReplacementNeeds[0]).(*o.GearReplacementNeed)}
		n := &gearProductionNative{gearTestNative: &gearTestNative{equipTestNative: &equipTestNative{routineNative: native, ids: []string{"a", "b"}}}, refuse: refuse}
		reviewer.native = n
		reviewer.methods = domain.Known([]policy.GoalID{policy.MaintainEquipment})
		reviewer.rules = []policy.ResourceRule{{Resource: "Cloth", Spending: policy.Stop}}
		if _, err := reviewer.Step(context.Background()); err != nil {
			t.Fatal(err)
		}
		snapshot := session.State().Snapshot
		ladder := store.ProductionLadderRecord{World: store.World{Colony: snapshot.Colony, Load: snapshot.Load, Map: snapshot.Map}, Resource: "Apparel_BasicShirt", Goal: policy.MaintainEquipment, Research: []string{"ComplexClothing"}}
		if err := db.SaveProductionLadder(context.Background(), ladder); err != nil {
			t.Fatal(err)
		}
		if needs, err := routineResearchNeeds(context.Background(), db, policy.RoutinePolicy{}, snapshot); err != nil || !reflect.DeepEqual(needs, ladder.Research) {
			t.Fatal("equipment research lost without a resource target", needs, err)
		}
		planner, err := NewRoutineGearPlanner(reviewer, n)
		if err != nil {
			t.Fatal(err)
		}
		result, err := planner.Step(context.Background())
		if err != nil {
			t.Fatal(err)
		}
		if len(n.previews) != 1 || !reflect.DeepEqual(n.previews[0].Ingredients(), []string{"Leather_Plain"}) {
			t.Fatal("protected cloth reached preview", n.previews)
		}
		if n.previews[0].Mode() != domain.GearBatch || n.previews[0].Target() != 2 {
			t.Fatal("colony gap not batched", n.previews)
		}
		if refuse {
			if result.Reason != BuildingMethodRefused || result.Plan != "" {
				t.Fatal(result)
			}
			continue
		}
		if result.Reason != BuildingMethodAdmitted {
			t.Fatal(result)
		}
		plan, err := db.LoadPlan(context.Background(), result.Plan)
		if err != nil {
			t.Fatal(err)
		}
		bill, ok := plan.Spec.Actions()[0].ProductionBill()
		if !ok || bill != n.previews[0] {
			t.Fatal("persisted bill differs from preview", bill, n.previews)
		}
	}
}

// gearTestNative is the equip colony with a gear census attached: pawn a has
// an unworn replacement candidate on the map, pawn b is fully equipped. The
// candidate's own definition is in stock so the wear order is funded (#233).
type gearTestNative struct {
	*equipTestNative
	benchReads int
}

func (n *gearTestNative) ReadGearBenches(ctx context.Context, _ *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error) {
	n.benchReads++
	return nil, bridge.Result{}, ctx.Err()
}
func (n *gearTestNative) ReadSupplyStock(ctx context.Context, _ *c.Identity, names []string) ([]policy.Stock, bridge.Result, error) {
	var stock []policy.Stock
	for _, name := range names {
		stock = append(stock, policy.Stock{Resource: policy.Resource(name), Available: domain.Known[int64](1)})
	}
	return stock, bridge.Result{}, ctx.Err()
}
func (n *gearTestNative) PreviewBill(context.Context, *c.Identity, domain.ProductionBill) (*op.PreviewReply, bridge.Result, error) {
	return nil, bridge.Result{}, errors.New("no bill preview expected for a wear order")
}

// The gear family must admit a GearReplace method once the routine review
// ranks MaintainEquipment in deficit: the goal was hard-gated
// method_unavailable for every review until #233, and no planner test covered
// the family (#258).
func TestGearPlannerAdmitsReplaceMethod(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	reviewer, db, _, _, native := routineFixture(t)
	v := native.reply.GetObserved()
	v.ColonistCount = proto.Uint32(2)
	v.WorkerCount = proto.Uint32(2)
	v.Issues = append(v.Issues, &o.ReadIssue{Field: proto.String("naming"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}})
	complete := func(n int) *o.Completeness {
		return &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(uint64(n)), Returned: proto.Uint64(uint64(n)), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}
	}
	observedContext := func() *c.ObservationContext { return proto.Clone(v.Context).(*c.ObservationContext) }
	loadout := func(id string, deficit bool, candidates ...*o.GearCandidate) *o.GearLoadout {
		return &o.GearLoadout{Snapshot: &o.SnapshotRef{Context: observedContext(), EntityId: proto.String(id), Token: proto.String("loadout-" + id)}, Pawn: &o.EntityRef{Id: proto.String(id), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Equipment: &o.PawnEquipment{Armed: proto.Bool(!deficit)}, Candidates: candidates, Deficit: proto.Bool(deficit), Completeness: complete(len(candidates))}
	}
	// The census lists a weapon beside the parka: only the parka is a wear
	// candidate; the weapon's eligibility belongs to the equip family and
	// the wear operation refuses it as absent (#339).
	bow := &o.GearCandidate{Item: &o.GearItem{Thing: &o.EntityRef{Id: proto.String("bow"), DefName: proto.String("Bow_Short"), MapId: proto.Int32(v.Context.Identity.GetMapId()), Position: &c.Cell{X: proto.Int32(5), Z: proto.Int32(5)}}, Weapon: proto.Bool(true), Apparel: proto.Bool(false), Ranged: proto.Bool(true)}, Gain: proto.Float64(9)}
	parka := &o.GearCandidate{Item: &o.GearItem{Thing: &o.EntityRef{Id: proto.String("parka"), DefName: proto.String("Apparel_Parka"), MapId: proto.Int32(v.Context.Identity.GetMapId()), Position: &c.Cell{X: proto.Int32(6), Z: proto.Int32(5)}}, Weapon: proto.Bool(false), Apparel: proto.Bool(true)}, Gain: proto.Float64(1)}
	v.Planning.GetObserved().Gear = &o.GearSnapshot{Context: observedContext(), Pawns: []*o.GearLoadout{loadout("a", true, bow, parka), loadout("b", false)}, Completeness: complete(2)}
	n := &gearTestNative{equipTestNative: &equipTestNative{routineNative: native, ids: []string{"a", "b"}}}
	reviewer.native = n
	reviewer.methods = domain.Known([]policy.GoalID{policy.MaintainEquipment})
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	review, err := db.LoadRoutineReview(ctx)
	if err != nil {
		t.Fatal(err)
	}
	found := false
	for _, binding := range review.Goals {
		if binding.Need != policy.MaintainEquipment {
			continue
		}
		g, err := db.LoadGoal(ctx, binding.Goal)
		if err != nil || g.Goal.Need != domain.NeedDeficit || g.Goal.Status != domain.GoalActive {
			t.Fatal("MaintainEquipment is not an active deficit", g, err)
		}
		found = true
	}
	if !found {
		t.Fatal("MaintainEquipment missing from the review", review.Goals)
	}
	planner, err := NewRoutineGearPlanner(reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	if n.benchReads != 0 {
		t.Fatal("bench census read while a wear candidate was pending")
	}
	plan, err := db.LoadPlan(ctx, result.Plan)
	if err != nil || len(plan.Progress) != 1 {
		t.Fatal(plan, err)
	}
	replace, ok := plan.Spec.Actions()[0].GearReplace()
	if !ok || replace.Pawn() != "a" || replace.Thing() != "parka" || replace.Definition() != "Apparel_Parka" {
		t.Fatal("expected pawn a to wear the parka", replace)
	}
	// A second step sees the open plan and admits nothing more.
	if result, err = planner.Step(ctx); err != nil || result.Reason != BuildingMethodExistingWork {
		t.Fatal(result, err)
	}
}

// A census whose only candidates are weapons proposes no wear order: the
// wear operation cannot target a weapon, so the planner falls through to the
// bench census instead of committing a plan that is refused on every attempt
// and holds a development slot for the run (#339).
func TestGearPlannerSkipsWeaponCandidates(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	reviewer, _, _, _, native := routineFixture(t)
	v := native.reply.GetObserved()
	v.ColonistCount = proto.Uint32(2)
	v.WorkerCount = proto.Uint32(2)
	v.Issues = append(v.Issues, &o.ReadIssue{Field: proto.String("naming"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}})
	complete := func(n int) *o.Completeness {
		return &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(uint64(n)), Returned: proto.Uint64(uint64(n)), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}
	}
	observedContext := func() *c.ObservationContext { return proto.Clone(v.Context).(*c.ObservationContext) }
	loadout := func(id string, deficit bool, candidates ...*o.GearCandidate) *o.GearLoadout {
		return &o.GearLoadout{Snapshot: &o.SnapshotRef{Context: observedContext(), EntityId: proto.String(id), Token: proto.String("loadout-" + id)}, Pawn: &o.EntityRef{Id: proto.String(id), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Equipment: &o.PawnEquipment{Armed: proto.Bool(!deficit)}, Candidates: candidates, Deficit: proto.Bool(deficit), Completeness: complete(len(candidates))}
	}
	log := &o.GearCandidate{Item: &o.GearItem{Thing: &o.EntityRef{Id: proto.String("log"), DefName: proto.String("WoodLog"), MapId: proto.Int32(v.Context.Identity.GetMapId()), Position: &c.Cell{X: proto.Int32(5), Z: proto.Int32(5)}}, Weapon: proto.Bool(true), Apparel: proto.Bool(false), Melee: proto.Bool(true)}, Gain: proto.Float64(1)}
	v.Planning.GetObserved().Gear = &o.GearSnapshot{Context: observedContext(), Pawns: []*o.GearLoadout{loadout("a", true, log), loadout("b", false)}, Completeness: complete(2)}
	n := &gearTestNative{equipTestNative: &equipTestNative{routineNative: native, ids: []string{"a", "b"}}}
	reviewer.native = n
	reviewer.methods = domain.Known([]policy.GoalID{policy.MaintainEquipment})
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoutineGearPlanner(reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(ctx)
	if err != nil || result.Reason == BuildingMethodAdmitted {
		t.Fatal("weapon candidate admitted as a wear order", result, err)
	}
	if n.benchReads != 1 {
		t.Fatal("bench census not consulted once the weapon was skipped", n.benchReads)
	}
}

// The weapon-demand census the gear planner reads before selecting a method
// asks for the colony's exact pawn IDs, so every other pawn the map holds
// counts as filtered. Treating that as an incomplete read failed the whole
// gear step with ErrControl once the goal's apparel policies were written, so
// MaintainEquipment never planned a wear or bill method and never recovered
// (#660).
func TestGearPlannerPlansPastFilteredWeaponCensus(t *testing.T) {
	t.Parallel()
	reviewer, db, _, _, native := routineFixture(t)
	setGearProductionNeed(native.reply.GetObserved())
	n := &gearProductionNative{gearTestNative: &gearTestNative{equipTestNative: &equipTestNative{routineNative: native, ids: []string{"a", "b"}, filtered: 4}}}
	reviewer.native = n
	reviewer.methods = domain.Known([]policy.GoalID{policy.MaintainEquipment})
	if _, err := reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoutineGearPlanner(reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(context.Background())
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal("filtered weapon census held the gear step", result, err)
	}
	plan, err := db.LoadPlan(context.Background(), result.Plan)
	if err != nil {
		t.Fatal(err)
	}
	if _, ok := plan.Spec.Actions()[0].ProductionBill(); !ok {
		t.Fatal("gear method is not a bill", plan.Spec.Actions()[0])
	}
}
