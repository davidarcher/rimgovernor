package buildingruntime

import (
	"context"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestGearPlannerAssignsPolicyBeforeWearOrProduction(t *testing.T) {
	reviewer, db, _, _, native := routineFixture(t)
	setGearProductionNeed(native.reply.GetObserved())
	settleGearPolicies(t, native)
	gear := native.reply.GetObserved().GetPlanning().GetObserved().GetGear()
	gear.Pawns[0].ApparelPolicy = &o.ApparelPolicyState{Token: proto.String("policy-cas"), PawnName: proto.String("Ann"), Name: proto.String("Player custom"), Child: proto.Bool(false), Slave: proto.Bool(false), IncapableOfViolence: proto.Bool(false), Drafted: proto.Bool(false), MinHitPoints: proto.Float32(0), MaxHitPoints: proto.Float32(1), MinQuality: proto.Int32(0), MaxQuality: proto.Int32(6), ExcludesTainted: proto.Bool(false)}
	n := &gearProductionNative{gearTestNative: &gearTestNative{equipTestNative: &equipTestNative{routineNative: native, ids: []string{"a", "b"}}}}
	reviewer.native = n
	reviewer.methods = domain.Known([]policy.ConcernID{policy.MaintainEquipment})
	if _, err := reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoutineGearPlanner(reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(context.Background())
	if err != nil || result.Verdict != BuildingReasonAdmitted {
		t.Fatal(result, err)
	}
	state, err := db.LoadPlan(context.Background(), result.Plan)
	if err != nil {
		t.Fatal(err)
	}
	value, ok := state.Spec.Actions()[0].ApparelPolicy()
	if !ok || value.Pawn() != "a" || value.Spec().Name != "Ann" || value.Spec().Token != "policy-cas" {
		t.Fatal(value, ok)
	}
}

// TestGearPlannerAdmitsEveryPawnPolicyInOneStep pins #660: policy writes
// were admitted one per development slot per round, so eight colonists took a
// whole window to assign and no wear order or bill ever followed.
func TestGearPlannerAdmitsEveryPawnPolicyInOneStep(t *testing.T) {
	reviewer, db, _, _, native := routineFixture(t)
	setGearProductionNeed(native.reply.GetObserved())
	native.finished = []string{}
	gear := native.reply.GetObserved().GetPlanning().GetObserved().GetGear()
	for _, pawn := range gear.Pawns {
		pawn.ApparelPolicy = &o.ApparelPolicyState{Token: proto.String("policy-cas-" + pawn.GetPawn().GetId()), PawnName: proto.String("Name " + pawn.GetPawn().GetId()), Name: proto.String("Player custom"), Child: proto.Bool(false), Slave: proto.Bool(false), IncapableOfViolence: proto.Bool(false), Drafted: proto.Bool(false), MinHitPoints: proto.Float32(0), MaxHitPoints: proto.Float32(1), MinQuality: proto.Int32(0), MaxQuality: proto.Int32(6), ExcludesTainted: proto.Bool(false)}
	}
	n := &gearProductionNative{gearTestNative: &gearTestNative{equipTestNative: &equipTestNative{routineNative: native, ids: []string{"a", "b"}}}}
	reviewer.native = n
	reviewer.methods = domain.Known([]policy.ConcernID{policy.MaintainEquipment})
	if _, err := reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoutineGearPlanner(reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	call, epoch, done, err := reviewer.player.enter(context.Background(), "test", false)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.stepOne(call, epoch, newStepArbiter())
	done()
	if err != nil || result.Verdict != BuildingReasonAdmitted {
		t.Fatal(result, err)
	}
	review, err := db.LoadRounds(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	pawns := map[domain.PawnID]bool{}
	for _, binding := range review.Goals {
		if binding.Need != policy.MaintainEquipment {
			continue
		}
		goal, err := db.LoadGoal(context.Background(), binding.Goal)
		if err != nil {
			t.Fatal(err)
		}
		for _, method := range goal.Methods {
			plan, err := db.LoadPlan(context.Background(), method.Plan)
			if err != nil {
				t.Fatal(err)
			}
			if value, ok := plan.Spec.Actions()[0].ApparelPolicy(); ok {
				pawns[value.Pawn()] = true
			}
		}
	}
	if !pawns["a"] || !pawns["b"] || len(pawns) != 2 {
		t.Fatal("one step admitted policies for", pawns)
	}
}

type gearProductionNative struct {
	*gearTestNative
}

func (n *gearProductionNative) ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error) {
	recipe := policy.GearRecipe{Definition: "Make_Apparel_BasicShirt", Products: []policy.Resource{"Apparel_BasicShirt"}, Available: domain.Known(true), AvailableOn: domain.Known(true), Ingredients: domain.Known([][]policy.Amount{{{Resource: "Cloth", Count: 40}, {Resource: "Leather_Plain", Count: 40}}}), RequiredWork: domain.Known([]policy.WorkRequirement{{Work: "Tailoring", Skill: "Crafting"}})}
	return []bridge.GearBenchRead{{Token: "bench-cas", Bench: policy.GearBench{ID: "tailor", Bills: domain.Known([]policy.GearBill{}), Recipes: domain.Known([]policy.GearRecipe{recipe})}}}, bridge.Result{}, nil
}
func (n *gearProductionNative) ReadSupplyStock(context.Context, *c.Identity, []string) ([]policy.Stock, bridge.Result, error) {
	return []policy.Stock{{Resource: "Cloth", Available: domain.Known(int64(100))}, {Resource: "Leather_Plain", Available: domain.Known(int64(100))}}, bridge.Result{}, nil
}

func setGearProductionNeed(v *o.ColonyFactsSnapshot) {
	v.ColonistCount = proto.Uint32(2)
	v.WorkerCount = proto.Uint32(2)
	v.Issues = append(v.Issues, &o.ReadIssue{Field: proto.String("naming"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}})
	v.OutdoorTemperatureC = proto.Float64(21)
	gear := &o.GearSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext)}
	for _, id := range []string{"a", "b"} {
		pawn := &o.GearLoadout{Snapshot: &o.SnapshotRef{Context: proto.Clone(v.Context).(*c.ObservationContext), EntityId: proto.String(id), Token: proto.String("loadout-" + id)}, Pawn: &c.Ref{Id: proto.String(id)}, Equipment: &o.PawnEquipment{Armed: proto.Bool(true)}, Gender: d.Gender_GENDER_FEMALE.Enum(), DevelopmentalStage: d.DevelopmentalStage_DEVELOPMENTAL_STAGE_ADULT.Enum(), BodyPartGroups: []string{"Torso", "Arms", "Legs"}}
		gear.Pawns = append(gear.Pawns, pawn)
	}
	// Pawn a is bare and its model wants a cloth shirt; pawn b wears one.
	gearModel(gear.Pawns[0], false, nil, gearBillOption("Apparel_BasicShirt", "Cloth"))
	gearModel(gear.Pawns[1], false, []*o.GearLoadoutOption{gearWornOption("shirt-b", "Apparel_BasicShirt", "Cloth")})
	v.Planning.GetObserved().Gear = gear
}

// gearModel gives a census row the inputs the loadout model needs: comfort
// range, apparel policy and the worn garments and options of its model.
func gearModel(pawn *o.GearLoadout, drafted bool, worn []*o.GearLoadoutOption, options ...*o.GearLoadoutOption) {
	pawn.ComfortableMinC, pawn.ComfortableMaxC = proto.Float64(10), proto.Float64(30)
	id := pawn.GetPawn().GetId()
	pawn.ApparelPolicy = &o.ApparelPolicyState{Token: proto.String("policy-" + id), PawnName: proto.String("Pawn " + id), PolicyId: proto.String("outfit-" + id), Name: proto.String("Player custom"), Drafted: proto.Bool(drafted), Child: proto.Bool(false), Slave: proto.Bool(false), IncapableOfViolence: proto.Bool(false), MinHitPoints: proto.Float32(0), MaxHitPoints: proto.Float32(1), MinQuality: proto.Int32(0), MaxQuality: proto.Int32(6), ExcludesTainted: proto.Bool(false)}
	pawn.LoadoutModel = &o.GearLoadoutModel{Worn: worn, Options: options}
}

// settleGearPolicies sets every census pawn's current outfit to the one the
// planner would assign it, so a step's first admission is the wear order or
// bill under test and not the outfit write that precedes them.
func settleGearPolicies(t *testing.T, n *routineNative) {
	t.Helper()
	// A gear census is judged against the finished projects, so the frame
	// must carry the research section (an empty list is a colony with none).
	if n.finished == nil {
		n.finished = []string{}
	}
	v := n.reply.GetObserved()
	catalog, err := n.DefinitionCatalog(context.Background(), v.Context.Identity)
	if err != nil {
		t.Fatal(err)
	}
	finished := map[string]bool{}
	for _, name := range n.finishedResearch() {
		finished[name] = true
	}
	census, known := observation.GearFacts(v, bridge.Tables{Catalog: catalog}, observation.GearDefinitions{Catalog: catalog, Finished: domain.Known(finished)}).Value()
	if !known {
		t.Fatal("gear census unknown")
	}
	rows := v.GetPlanning().GetObserved().GetGear().Pawns
	for i, pawn := range census.Pawns {
		desired, needed := policy.DesiredApparelPolicy(pawn)
		if !needed {
			continue
		}
		spec := desired.Spec()
		state := rows[i].ApparelPolicy
		state.Name, state.AllowedDefs = proto.String(spec.Name), spec.Definitions
		state.MinHitPoints, state.MaxHitPoints = proto.Float32(float32(spec.MinHP)), proto.Float32(float32(spec.MaxHP))
		state.MinQuality, state.MaxQuality = proto.Int32(spec.MinQuality), proto.Int32(spec.MaxQuality)
		state.ExcludesTainted = proto.Bool(true)
	}
}

func gearWornOption(id, def, stuff string) *o.GearLoadoutOption {
	return &o.GearLoadoutOption{Id: proto.String(id), DefName: proto.String(def), Stuff: proto.String(stuff), Quality: proto.Int32(2), Source: proto.String("worn"), Condition: proto.Float64(1)}
}

// gearBillOption is a producible definition at Normal quality with no recipe
// research and a cost of 40 of its stuff (or steel for an unstuffed def).
func gearBillOption(def, stuff string) *o.GearLoadoutOption {
	cost := stuff
	if cost == "" {
		cost = "Steel"
	}
	option := &o.GearLoadoutOption{Id: proto.String("bill:" + def + "/" + stuff), DefName: proto.String(def), Quality: proto.Int32(2), Source: proto.String("bill"), Condition: proto.Float64(1), Ingredients: []*o.Quantity{{DefName: proto.String(cost), Units: proto.Int64(40)}}}
	if stuff != "" {
		option.Stuff = proto.String(stuff)
	}
	return option
}

func TestGearProductionPersistsOnlyFundedMaterials(t *testing.T) {
	t.Parallel()
	{
		reviewer, db, session, _, native := routineFixture(t)
		reviewer.policy.Stage.Floor = policy.StageDevelopment
		setGearProductionNeed(native.reply.GetObserved())
		gear := native.reply.GetObserved().GetPlanning().GetObserved().Gear
		gearModel(gear.Pawns[1], false, nil, gearBillOption("Apparel_BasicShirt", "Cloth"))
		n := &gearProductionNative{gearTestNative: &gearTestNative{equipTestNative: &equipTestNative{routineNative: native, ids: []string{"a", "b"}}}}
		settleGearPolicies(t, native)
		reviewer.native = n
		reviewer.methods = domain.Known([]policy.ConcernID{policy.MaintainEquipment})
		if _, err := reviewer.Step(context.Background()); err != nil {
			t.Fatal(err)
		}
		snapshot := session.State().Snapshot
		ladder := store.ProductionLadderRecord{World: store.World{Colony: snapshot.Colony, Load: snapshot.Load, Map: snapshot.Map}, Resource: "Apparel_BasicShirt", Goal: policy.MaintainEquipment, Research: []string{"ComplexClothing"}}
		if err := db.SaveProductionLadder(context.Background(), ladder); err != nil {
			t.Fatal(err)
		}
		if needs, err := routineResearchNeeds(context.Background(), db, policy.RoutinePolicy{}, policy.CoreItemFacts(), snapshot); err != nil || !reflect.DeepEqual(needs, ladder.Research) {
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
		if result.Verdict != BuildingReasonAdmitted {
			t.Fatal(result)
		}
		plan, err := db.LoadPlan(context.Background(), result.Plan)
		if err != nil {
			t.Fatal(err)
		}
		bill, ok := plan.Spec.Actions()[0].ProductionBill()
		if !ok || !reflect.DeepEqual(bill.Ingredients(), []string{"Cloth"}) {
			t.Fatal("wrong funded material persisted", bill, ok)
		}
		if bill.Mode() != domain.GearBatch || bill.Target() != 2 {
			t.Fatal("colony gap not batched", bill)
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

// ReadResearch serves the finished projects the gear census's loadout model
// joins its bill options to (the planner's own read of them).
func (n *gearTestNative) ReadResearch(context.Context, *c.Identity) (bridge.ResearchRead, bridge.Result, error) {
	return bridge.ResearchRead{Context: n.reply.GetObserved().Context, Finished: n.finishedResearch()}, bridge.Result{}, nil
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

// The gear family must admit a GearReplace method once the rounds
// ranks MaintainEquipment in deficit: the goal was hard-gated
// method_unavailable for every review until #233, and no planner test covered
// the family (#258).
func TestGearPlannerAdmitsReplaceMethod(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	reviewer, db, _, _, native := routineFixture(t)
	reviewer.policy.Stage.Floor = policy.StageDevelopment
	v := native.reply.GetObserved()
	v.ColonistCount = proto.Uint32(2)
	v.WorkerCount = proto.Uint32(2)
	v.Issues = append(v.Issues, &o.ReadIssue{Field: proto.String("naming"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}})
	observedContext := func() *c.ObservationContext { return proto.Clone(v.Context).(*c.ObservationContext) }
	v.OutdoorTemperatureC = proto.Float64(21)
	loadout := func(id string, deficit bool, candidates ...*o.GearCandidate) *o.GearLoadout {
		pawn := &o.GearLoadout{Snapshot: &o.SnapshotRef{Context: observedContext(), EntityId: proto.String(id), Token: proto.String("loadout-" + id)}, Pawn: &c.Ref{Id: proto.String(id)}, Equipment: &o.PawnEquipment{Armed: proto.Bool(!deficit)}, Candidates: candidates, Gender: d.Gender_GENDER_FEMALE.Enum(), DevelopmentalStage: d.DevelopmentalStage_DEVELOPMENTAL_STAGE_ADULT.Enum(), BodyPartGroups: []string{"Torso", "Arms", "Legs"}}
		if deficit {
			// The model wants the loose parka (the one wear candidate).
			gearModel(pawn, false, nil, &o.GearLoadoutOption{Id: proto.String("parka"), DefName: proto.String("Apparel_Parka"), Stuff: proto.String("Cloth"), Quality: proto.Int32(2), Source: proto.String("loose"), Condition: proto.Float64(1)})
		} else {
			gearModel(pawn, false, []*o.GearLoadoutOption{gearWornOption("shirt-"+id, "Apparel_BasicShirt", "Cloth")})
		}
		return pawn
	}
	// The census lists a weapon beside the parka: only the parka is a wear
	// candidate; the weapon's eligibility belongs to the equip family and
	// the wear operation refuses it as absent (#339).
	bow := &o.GearCandidate{Item: &o.GearItem{Thing: native.entity(&o.EntityRef{Id: proto.String("bow"), DefName: proto.String("Bow_Short"), MapId: proto.Int32(v.Context.Identity.GetMapId()), Position: &c.Cell{X: proto.Int32(5), Z: proto.Int32(5)}})}, Gain: proto.Float64(9)}
	parka := &o.GearCandidate{Item: &o.GearItem{Thing: native.entity(&o.EntityRef{Id: proto.String("parka"), DefName: proto.String("Apparel_Parka"), MapId: proto.Int32(v.Context.Identity.GetMapId()), Position: &c.Cell{X: proto.Int32(6), Z: proto.Int32(5)}})}, Gain: proto.Float64(1)}
	v.Planning.GetObserved().Gear = &o.GearSnapshot{Context: observedContext(), Pawns: []*o.GearLoadout{loadout("a", true, bow, parka), loadout("b", false)}}
	n := &gearTestNative{equipTestNative: &equipTestNative{routineNative: native, ids: []string{"a", "b"}}}
	settleGearPolicies(t, native)
	reviewer.native = n
	reviewer.methods = domain.Known([]policy.ConcernID{policy.MaintainEquipment})
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	review, err := db.LoadRounds(ctx)
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
	if err != nil || result.Verdict != BuildingReasonAdmitted {
		t.Fatal(result, err)
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
	if result, err = planner.Step(ctx); err != nil || result.Verdict != BuildingReasonExistingWork {
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
	observedContext := func() *c.ObservationContext { return proto.Clone(v.Context).(*c.ObservationContext) }
	v.OutdoorTemperatureC = proto.Float64(21)
	loadout := func(id string, deficit bool, candidates ...*o.GearCandidate) *o.GearLoadout {
		pawn := &o.GearLoadout{Snapshot: &o.SnapshotRef{Context: observedContext(), EntityId: proto.String(id), Token: proto.String("loadout-" + id)}, Pawn: &c.Ref{Id: proto.String(id)}, Equipment: &o.PawnEquipment{Armed: proto.Bool(!deficit)}, Candidates: candidates, Gender: d.Gender_GENDER_FEMALE.Enum(), DevelopmentalStage: d.DevelopmentalStage_DEVELOPMENTAL_STAGE_ADULT.Enum(), BodyPartGroups: []string{"Torso", "Arms", "Legs"}}
		if deficit {
			// The model wants a shirt that only a bill can supply; the
			// census's one candidate is a weapon.
			gearModel(pawn, false, nil, gearBillOption("Apparel_BasicShirt", "Cloth"))
		} else {
			gearModel(pawn, false, []*o.GearLoadoutOption{gearWornOption("shirt-"+id, "Apparel_BasicShirt", "Cloth")})
		}
		return pawn
	}
	log := &o.GearCandidate{Item: &o.GearItem{Thing: native.entity(&o.EntityRef{Id: proto.String("log"), DefName: proto.String("WoodLog"), MapId: proto.Int32(v.Context.Identity.GetMapId()), Position: &c.Cell{X: proto.Int32(5), Z: proto.Int32(5)}})}, Gain: proto.Float64(1)}
	v.Planning.GetObserved().Gear = &o.GearSnapshot{Context: observedContext(), Pawns: []*o.GearLoadout{loadout("a", true, log), loadout("b", false)}}
	n := &gearTestNative{equipTestNative: &equipTestNative{routineNative: native, ids: []string{"a", "b"}}}
	settleGearPolicies(t, native)
	reviewer.native = n
	reviewer.policy.Stage.Floor = policy.StageStable // MaintainEquipment is raised from Stable
	reviewer.methods = domain.Known([]policy.ConcernID{policy.MaintainEquipment})
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	reviewed := n.benchReads // the review reads the benches for its own deficit work
	planner, err := NewRoutineGearPlanner(reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(ctx)
	if err != nil || result.Verdict == BuildingReasonAdmitted {
		t.Fatal("weapon candidate admitted as a wear order", result, err)
	}
	if n.benchReads-reviewed != 1 {
		t.Fatal("bench census not consulted once the weapon was skipped", n.benchReads-reviewed)
	}
}

// Before its colony stage the ranking drops MaintainEquipment (no row, no
// slot): the planner waits quietly instead of admitting a wear order or bill
// that the development check refuses on every tick.
func TestGearPlannerWaitsWhileTheGoalIsNotRaised(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	reviewer, _, _, _, native := routineFixture(t)
	setGearProductionNeed(native.reply.GetObserved())
	n := &gearProductionNative{gearTestNative: &gearTestNative{equipTestNative: &equipTestNative{routineNative: native, ids: []string{"a", "b"}}}}
	settleGearPolicies(t, native)
	reviewer.native = n
	reviewer.methods = domain.Known([]policy.ConcernID{policy.MaintainEquipment})
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoutineGearPlanner(reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(ctx)
	if err != nil || result.Verdict != BuildingReasonNoDeficit {
		t.Fatal(result, err)
	}
}
