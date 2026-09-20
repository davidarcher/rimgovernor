package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// workshopNative adds the bench census and recipe catalog the workshop
// planner reads before the planning census, on top of the sleeping fixture.
type workshopNative struct {
	*sleepingNative
	benches []bridge.GearBenchRead
	hosts   []policy.RecipeHost
}

func (n *workshopNative) ReadTemperatureRooms(context.Context, *c.Identity) (*o.ListRoomsReply, bridge.Result, error) {
	return &o.ListRoomsReply{}, bridge.Result{}, nil
}

func (n *workshopNative) ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error) {
	return n.benches, bridge.Result{}, nil
}

func (n *workshopNative) ReadRecipeCatalog(_ context.Context, _ *c.Identity, product string) ([]policy.RecipeHost, bridge.Result, error) {
	var out []policy.RecipeHost
	for _, host := range n.hosts {
		for _, p := range host.Products {
			if string(p) == product {
				out = append(out, host)
			}
		}
	}
	return out, bridge.Result{}, nil
}

var clubRecipe = policy.RecipeHost{Definition: "Make_MeleeWeapon_Club", Products: []policy.Resource{"MeleeWeapon_Club"}, Available: true, Benches: []string{"CraftingSpot"}}

func TestComponentWorkshopUsesResourcePrerequisites(t *testing.T) {
	planner, session, native := workshopFixture(t)
	planner.reviewer.policy.ResourceTargets = map[policy.Resource]int64{policy.ComponentResource: 20}
	native.reply.GetObserved().Resources = []*o.Quantity{{DefName: proto.String("ComponentIndustrial"), Units: proto.Int64(2)}}
	native.hosts = []policy.RecipeHost{{Definition: "MakeComponent", Products: []policy.Resource{policy.ComponentResource}, Available: true, Benches: []string{"FabricationBench"}, Research: []string{"Fabrication"}}}
	ctx := context.Background()
	selection, reason, err := planner.prepareWorkshop(ctx, session.State(), store.RoutineReview{})
	if err != nil || reason != "" || selection == nil || selection.resource != policy.ComponentResource || selection.candidates[0] != "FabricationBench" {
		t.Fatal(selection, reason, err)
	}
	planner.workshop = selection
	facts := observation.ColonyProjection{Definitions: []observation.PlanningDefinition{
		{Name: "FabricationBench", Available: domain.Known(true), NeedsPower: domain.Known(true), ConstructionSkill: domain.Known(int32(0)), Stuff: domain.Known("")},
		{Name: "WoodFiredGenerator", Available: domain.Known(true)},
	}}
	bench, reason, err := planner.selectWorkshop(ctx, session.State(), store.RoutineReview{}, facts)
	if err != nil || reason != "" || bench == nil || bench.definition != "FabricationBench" || bench.facility == nil || bench.facility.Role != policy.RoomRoleWorkshop {
		t.Fatal(bench, reason, err)
	}
}

func TestEquipmentWorkshopDiscoversReplacementBenchWithoutResourceTargets(t *testing.T) {
	t.Parallel()
	planner, session, native := workshopFixture(t)
	planner.goal = policy.MaintainEquipment
	planner.reviewer.policy.ResourceTargets = nil
	setGearProductionNeed(native.reply.GetObserved())
	native.hosts = []policy.RecipeHost{{Definition: "Make_Apparel_BasicShirt", Products: []policy.Resource{"Apparel_BasicShirt"}, Available: true, Benches: []string{"HandTailoringBench"}}}
	ctx := context.Background()
	selection, reason, err := planner.prepareWorkshop(ctx, session.State(), store.RoutineReview{})
	if err != nil || reason != "" || selection == nil || selection.resource != "Apparel_BasicShirt" || selection.candidates[0] != "HandTailoringBench" {
		t.Fatal(selection, reason, err)
	}
	planner.workshop = selection
	facts := observation.ColonyProjection{Definitions: []observation.PlanningDefinition{{Name: "HandTailoringBench", Available: domain.Known(true), NeedsPower: domain.Known(false), ConstructionSkill: domain.Known(int32(0)), Stuff: domain.Known("WoodLog")}}}
	bench, reason, err := planner.selectWorkshop(ctx, session.State(), store.RoutineReview{}, facts)
	if err != nil || reason != "" || bench == nil || bench.goal != policy.MaintainEquipment || bench.definition != "HandTailoringBench" || !bench.facilityLadder() {
		t.Fatal(bench, reason, err)
	}
	bench.shelter = false
	facts.Facts.Colonists = domain.Known(int64(2))
	if count, _, reason := bench.selection(facts); count != 1 || reason != "" {
		t.Fatal(count, reason)
	}
	facts.Definitions[0].Available = domain.Known(false)
	facts.Definitions[0].Research = []string{"ComplexClothing"}
	if _, reason, err := planner.selectWorkshop(ctx, session.State(), store.RoutineReview{}, facts); err != nil || reason != BuildingWorkshopResearch {
		t.Fatal(reason, err)
	}
	w := session.State().Snapshot
	ladder, ok, err := planner.reviewer.player.journal.LoadProductionLadder(ctx, store.World{Colony: w.Colony, Load: w.Load, Map: w.Map})
	if err != nil || !ok || ladder.Goal != policy.MaintainEquipment || len(ladder.Research) != 1 || ladder.Research[0] != "ComplexClothing" {
		t.Fatal(ladder, ok, err)
	}
	native.benches = []bridge.GearBenchRead{{Token: "bench", Bench: policy.GearBench{ID: "tailor", Bills: domain.Known([]policy.GearBill{}), Recipes: domain.Known([]policy.GearRecipe{{Definition: "Make_Apparel_BasicShirt", Products: []policy.Resource{"Apparel_BasicShirt"}, Available: domain.Known(true), AvailableOn: domain.Known(true)}})}}}
	if _, reason, err := planner.prepareWorkshop(ctx, session.State(), store.RoutineReview{}); err != nil || reason != BuildingExistingFacility {
		t.Fatal(reason, err)
	}
	// A missing layer may suggest advanced armor before the worn shirt in
	// lexical order; it must not prevent staging the available tailoring bench.
	native.benches = nil
	native.reply.GetObserved().Planning.GetObserved().Gear.Pawns[0].ReplacementNeeds = append(native.reply.GetObserved().Planning.GetObserved().Gear.Pawns[0].ReplacementNeeds, &o.GearReplacementNeed{DefName: proto.String("Apparel_ArmorRecon"), Reason: proto.String("missing")})
	native.hosts = append(native.hosts, policy.RecipeHost{Definition: "Make_Armor", Products: []policy.Resource{"Apparel_ArmorRecon"}, Available: false, Research: []string{"ReconArmor"}, Benches: []string{"FabricationBench"}})
	selection, reason, err = planner.prepareWorkshop(ctx, session.State(), store.RoutineReview{})
	if err != nil || reason != "" || selection == nil || len(selection.alternatives) != 1 {
		t.Fatal(selection, reason, err)
	}
	planner.workshop = selection
	facts.Definitions[0].Available = domain.Known(true)
	facts.Definitions[0].Research = nil
	facts.Definitions = append(facts.Definitions, observation.PlanningDefinition{Name: "FabricationBench", Available: domain.Known(false), NeedsPower: domain.Known(true), ConstructionSkill: domain.Known(int32(6)), Research: []string{"Fabrication"}})
	bench, reason, err = planner.selectWorkshop(ctx, session.State(), store.RoutineReview{}, facts)
	if err != nil || reason != "" || bench == nil || bench.definition != "HandTailoringBench" || bench.workshop.resource != "Apparel_BasicShirt" {
		t.Fatal(bench, reason, err)
	}
}

func workshopFixture(t *testing.T) (*RoutineBuildingPlanner, *playerFakeSession, *workshopNative) {
	t.Helper()
	base, _, session, _, native := sleepingFixture(t)
	source := &workshopNative{sleepingNative: native, hosts: []policy.RecipeHost{clubRecipe}}
	base.reviewer.policy.ResourceTargets = map[policy.Resource]int64{"MeleeWeapon_Club": 3}
	native.reply.GetObserved().Resources = []*o.Quantity{{DefName: proto.String("MeleeWeapon_Club"), Units: proto.Int64(0)}}
	planner, err := NewRoutineWorkshopPlanner(base.reviewer, source)
	if err != nil {
		t.Fatal(err)
	}
	return planner, session, source
}

func TestWorkshopPrepareDiscoversBenchOrDefersToExistingBench(t *testing.T) {
	t.Parallel()
	for _, test := range []struct {
		name       string
		benches    []bridge.GearBenchRead
		hosts      []policy.RecipeHost
		targets    map[policy.Resource]int64
		stock      int64
		reason     RoutineBuildingReason
		candidates []string
	}{
		{"no bench", nil, []policy.RecipeHost{clubRecipe}, nil, 0, "", append([]string{"CraftingSpot"}, policy.GeneratorDefinitions...)},
		{"no deficit", nil, []policy.RecipeHost{clubRecipe}, nil, 3, BuildingMethodNoDeficit, nil},
		{"no targets", nil, []policy.RecipeHost{clubRecipe}, map[policy.Resource]int64{}, 0, BuildingMethodDisabled, nil},
		{"existing bench", []bridge.GearBenchRead{{Token: "t", Bench: policy.GearBench{ID: "spot", Bills: domain.Known([]policy.GearBill{}), Recipes: domain.Known([]policy.GearRecipe{{Definition: "Make_MeleeWeapon_Club", Products: []policy.Resource{"MeleeWeapon_Club"}, Available: domain.Known(true), AvailableOn: domain.Known(true)}})}}}, []policy.RecipeHost{clubRecipe}, nil, 0, BuildingExistingFacility, nil},
		{"research gated", nil, []policy.RecipeHost{{Definition: "Make_MeleeWeapon_Club", Products: []policy.Resource{"MeleeWeapon_Club"}, Available: false, Benches: []string{"CraftingSpot"}, Research: []string{"Smithing"}}}, nil, 0, "", append([]string{"CraftingSpot"}, policy.GeneratorDefinitions...)},
		{"no host", nil, []policy.RecipeHost{}, nil, 0, BuildingWorkshopUnavailable, nil},
	} {
		t.Run(test.name, func(t *testing.T) {
			planner, session, native := workshopFixture(t)
			native.benches, native.hosts = test.benches, test.hosts
			if test.targets != nil {
				planner.reviewer.policy.ResourceTargets = test.targets
			}
			native.reply.GetObserved().Resources[0].Units = proto.Int64(test.stock)
			selection, reason, err := planner.prepareWorkshop(context.Background(), session.State(), store.RoutineReview{})
			if err != nil || reason != test.reason {
				t.Fatal(selection, reason, err)
			}
			if reason != "" {
				if selection != nil {
					t.Fatal("selection with reason", selection)
				}
				return
			}
			if selection.resource != "MeleeWeapon_Club" || len(selection.candidates) != len(test.candidates) || selection.candidates[0] != test.candidates[0] || selection.candidates[1] != test.candidates[1] {
				t.Fatal(selection)
			}
		})
	}
}

func TestWorkshopSelectStagesFirstUnpoweredBenchInWorkshopRoom(t *testing.T) {
	t.Parallel()
	planner, session, _ := workshopFixture(t)
	planner.workshop = &workshopSelection{resource: "MeleeWeapon_Club", hosts: []policy.RecipeHost{clubRecipe, {Definition: "Make_Club_Powered", Products: []policy.Resource{"MeleeWeapon_Club"}, Available: true, Benches: []string{"FabricationBench"}}}, candidates: []string{"CraftingSpot", "FabricationBench"}}
	definition := func(name string, available, powered bool) observation.PlanningDefinition {
		return observation.PlanningDefinition{Name: name, Available: domain.Known(available), NeedsPower: domain.Known(powered), ConstructionSkill: domain.Known(int32(0)), Stuff: domain.Known("")}
	}
	ctx := context.Background()
	state := session.State()
	world := store.World{Colony: state.Snapshot.Colony, Load: state.Snapshot.Load, Map: state.Snapshot.Map}
	facts := observation.ColonyProjection{Definitions: []observation.PlanningDefinition{definition("CraftingSpot", true, false), definition("FabricationBench", true, true)}}
	selected, reason, err := planner.selectWorkshop(ctx, state, store.RoutineReview{}, facts)
	if err != nil || reason != "" || selected.definition != "CraftingSpot" || selected.environment != policy.PlacementIndoors || selected.facility == nil || selected.facility.Role != policy.RoomRoleWorkshop {
		t.Fatal(selected, reason, err)
	}
	if planner.definition != "Wall" || planner.facility != nil {
		t.Fatal("selection mutated reusable compiler", planner)
	}
	if ladder, ok, err := planner.reviewer.player.journal.LoadProductionLadder(ctx, world); err != nil || !ok || ladder.Bench != "CraftingSpot" || len(ladder.Research) != 0 {
		t.Fatal("buildable bench must clear the research rung", ladder, ok, err)
	}
	// A research-gated bench records its projects; a powered one is only
	// staged once a generator definition is available.
	facts.Definitions = []observation.PlanningDefinition{definition("CraftingSpot", false, false), definition("FabricationBench", true, true)}
	facts.Definitions[0].Research = []string{"Smithing"}
	if selected, reason, err = planner.selectWorkshop(ctx, state, store.RoutineReview{}, facts); err != nil || reason != BuildingWorkshopResearch || selected != nil {
		t.Fatal(selected, reason, err)
	}
	if ladder, ok, err := planner.reviewer.player.journal.LoadProductionLadder(ctx, world); err != nil || !ok || ladder.Bench != "CraftingSpot" || len(ladder.Research) != 1 || ladder.Research[0] != "Smithing" {
		t.Fatal(ladder, ok, err)
	}
	facts.Definitions = append(facts.Definitions, definition("WoodFiredGenerator", true, false))
	if selected, reason, err = planner.selectWorkshop(ctx, state, store.RoutineReview{}, facts); err != nil || reason != "" || selected.definition != "FabricationBench" {
		t.Fatal(selected, reason, err)
	}
	facts.Definitions = []observation.PlanningDefinition{definition("CraftingSpot", false, false), definition("FabricationBench", true, true)}
	if selected, reason, err = planner.selectWorkshop(ctx, state, store.RoutineReview{}, facts); err != nil || reason != BuildingWorkshopUnavailable || selected != nil {
		t.Fatal(selected, reason, err)
	}
	facts.Definitions = []observation.PlanningDefinition{definition("CraftingSpot", true, false)}
	facts.Definitions[0].NeedsPower = domain.Unknown[bool]()
	if selected, reason, err = planner.selectWorkshop(ctx, state, store.RoutineReview{}, facts); err != nil || reason != BuildingMethodUnknown || selected != nil {
		t.Fatal(selected, reason, err)
	}
	planner.workshop = nil
	if selected, reason, err = planner.selectWorkshop(ctx, state, store.RoutineReview{}, facts); err != nil || reason != BuildingMethodUnknown || selected != nil {
		t.Fatal(selected, reason, err)
	}
}

func TestWorkshopFurnishingOnlyPreviewsHostingRoomsAndFallsBackToShell(t *testing.T) {
	t.Parallel()
	workshop, _ := policy.Facility(policy.RoomRoleWorkshop)
	site := func(cells ...domain.Cell) []policy.SiteCell {
		var rows []policy.SiteCell
		for _, c := range cells {
			rows = append(rows, policy.SiteCell{Cell: c, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false), Roofed: domain.Known(true), Indoors: domain.Known(true)})
		}
		return rows
	}
	// A tomb never hosts a bench; a barracks does (the shared starter shell).
	tomb, hosting := domain.Cell{X: 2, Z: 2}, domain.Cell{X: 3, Z: 2}
	for _, test := range []struct {
		name   string
		rooms  domain.Fact[policy.RoomObservation]
		reason RoutineBuildingReason
		cell   domain.Cell
	}{
		{"hosting room only", domain.Known(policy.RoomObservation{Rooms: []policy.Room{{ID: "t", Role: domain.Known(policy.RoomRoleTomb), Cells: []domain.Cell{tomb}}, {ID: "r", Role: domain.Known(policy.RoomRoleRoom), Cells: []domain.Cell{hosting}}}}), "", hosting},
		{"shared barracks", domain.Known(policy.RoomObservation{Rooms: []policy.Room{{ID: "t", Role: domain.Known(policy.RoomRoleTomb), Cells: []domain.Cell{tomb}}, {ID: "b", Role: domain.Known(policy.RoomRoleBarracks), Cells: []domain.Cell{hosting}}}}), "", hosting},
		{"no hosting room", domain.Known(policy.RoomObservation{Rooms: []policy.Room{{ID: "t", Role: domain.Known(policy.RoomRoleTomb), Cells: []domain.Cell{tomb, hosting}}}}), BuildingMethodNoSpace, domain.Cell{}},
		{"census unknown", domain.Unknown[policy.RoomObservation](), BuildingMethodUnknown, domain.Cell{}},
	} {
		t.Run(test.name, func(t *testing.T) {
			planner, _, session, _, native := sleepingFixture(t)
			planner.goal, planner.definition, planner.environment, planner.facility = policy.MaintainResource, "CraftingSpot", policy.PlacementIndoors, &workshop
			native.onPreview = func(_ context.Context, p *bridge.BuildingPreview) {
				b, _ := p.Preview.Action.Building()
				p.Preview.Footprint = domain.Known([]domain.Cell{b.Cell()})
			}
			facts := observation.ColonyProjection{Bounds: policy.Bounds{Width: 10, Height: 10}, Center: domain.Cell{X: 2, Z: 2}, Identity: observation.Identity{Tick: domain.Tick(native.reply.GetObserved().Context.GetTick())}, Cells: site(tomb, hosting), Rooms: test.rooms}
			selected, _, reason, err := planner.previewMethod(context.Background(), session.State().Snapshot, facts, nil, 1, func() error { return nil })
			if err != nil || reason != test.reason {
				t.Fatal(selected, reason, err)
			}
			if reason != "" {
				if native.previews != 0 {
					t.Fatal("previewed outside a hosting room", native.previews)
				}
				return
			}
			b, _ := selected[0].Action.Building()
			if len(selected) != 1 || b.Cell() != test.cell || native.previews != 1 {
				t.Fatal(selected, native.previews)
			}
		})
	}
	shell := &RoutineBuildingPlanner{goal: policy.MaintainResource, definition: "Wall", shelter: true}
	facts := observation.ColonyProjection{Facts: policy.RoutineFacts{Colonists: domain.Known(int64(2))}}
	if missing, method, reason := shell.selection(facts); missing != 32 || method != "workshop-shell" || reason != "" {
		t.Fatal(missing, method, reason)
	}
	bench := &RoutineBuildingPlanner{goal: policy.MaintainResource, definition: "CraftingSpot"}
	if missing, method, reason := bench.selection(facts); missing != 1 || method != "workshop-CraftingSpot" || reason != "" {
		t.Fatal(missing, method, reason)
	}
}

// A bench anchor the native preview rejects facing north (its interaction
// spot on a wall) is retried facing east, south and west before the cell is
// given up; other definitions keep the single north preview.
func TestWorkshopBenchPreviewRetriesRotations(t *testing.T) {
	t.Parallel()
	workshop, _ := policy.Facility(policy.RoomRoleWorkshop)
	hosting := domain.Cell{X: 3, Z: 2}
	rooms := domain.Known(policy.RoomObservation{Rooms: []policy.Room{{ID: "r", Role: domain.Known(policy.RoomRoleRoom), Cells: []domain.Cell{hosting}}}})
	facts := func(tick domain.Tick) observation.ColonyProjection {
		return observation.ColonyProjection{Bounds: policy.Bounds{Width: 10, Height: 10}, Center: hosting, Identity: observation.Identity{Tick: tick}, Cells: []policy.SiteCell{{Cell: hosting, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false), Roofed: domain.Known(true), Indoors: domain.Known(true)}}, Rooms: rooms}
	}
	rejectUnless := func(accepted domain.Rotation) func(context.Context, *bridge.BuildingPreview) {
		return func(_ context.Context, p *bridge.BuildingPreview) {
			b, _ := p.Preview.Action.Building()
			p.Preview.Footprint = domain.Known([]domain.Cell{b.Cell()})
			p.Preview.CanPlace = domain.Known(b.Rotation() == accepted)
		}
	}
	t.Run("bench turns east", func(t *testing.T) {
		planner, _, session, _, native := sleepingFixture(t)
		planner.goal, planner.definition, planner.environment, planner.facility = policy.MaintainResource, "FueledSmithy", policy.PlacementIndoors, &workshop
		planner.workshop = &workshopSelection{resource: "MeleeWeapon_Gladius"}
		native.onPreview = rejectUnless(domain.East)
		selected, _, reason, err := planner.previewMethod(context.Background(), session.State().Snapshot, facts(domain.Tick(native.reply.GetObserved().Context.GetTick())), nil, 1, func() error { return nil })
		if err != nil || reason != "" || len(selected) != 1 {
			t.Fatal(selected, reason, err)
		}
		b, _ := selected[0].Action.Building()
		if b.Cell() != hosting || b.Rotation() != domain.East || native.previews != 2 {
			t.Fatal(b, native.previews)
		}
	})
	t.Run("no facing fits", func(t *testing.T) {
		planner, _, session, _, native := sleepingFixture(t)
		planner.goal, planner.definition, planner.environment, planner.facility = policy.MaintainResource, "FueledSmithy", policy.PlacementIndoors, &workshop
		planner.workshop = &workshopSelection{resource: "MeleeWeapon_Gladius"}
		native.onPreview = rejectUnless("")
		selected, _, reason, err := planner.previewMethod(context.Background(), session.State().Snapshot, facts(domain.Tick(native.reply.GetObserved().Context.GetTick())), nil, 1, func() error { return nil })
		if err != nil || reason != BuildingMethodNoSpace || len(selected) != 0 || native.previews != 4 {
			t.Fatal(selected, reason, err, native.previews)
		}
	})
	t.Run("comfort keeps north", func(t *testing.T) {
		planner, _, session, _, native := sleepingFixture(t)
		planner.goal, planner.definition, planner.environment, planner.facility = policy.EnsureComfort, "Table1x2c", policy.PlacementIndoors, &workshop
		native.onPreview = rejectUnless(domain.East)
		selected, _, reason, err := planner.previewMethod(context.Background(), session.State().Snapshot, facts(domain.Tick(native.reply.GetObserved().Context.GetTick())), nil, 1, func() error { return nil })
		if err != nil || reason != BuildingMethodNoSpace || len(selected) != 0 || native.previews != 1 {
			t.Fatal(selected, reason, err, native.previews)
		}
	})
}

func TestWorkshopShellWaitsWhileInitialShelterIsOwed(t *testing.T) {
	t.Parallel()
	r, db, _ := shelterFixture(t)
	review, err := db.LoadRoutineReview(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	owed, err := initialShelterOwed(context.Background(), r.reviewer.player, review)
	if err != nil || !owed {
		t.Fatal("initial shelter deficit not seen as owed:", owed, err)
	}
	var others []store.RoutineGoal
	for _, binding := range review.Goals {
		if binding.Need != policy.EnsureInitialShelter {
			others = append(others, binding)
		}
	}
	review.Goals = others
	if owed, err = initialShelterOwed(context.Background(), r.reviewer.player, review); err != nil || owed {
		t.Fatal("no shelter binding must not block a workshop shell:", owed, err)
	}
}
