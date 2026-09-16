package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"testing"
)

func TestComfortPlacementRejectsCrampedRecreationAndPreservesUnknown(t *testing.T) {
	t.Parallel()
	for _, test := range []struct {
		name     string
		access   []domain.Fact[bool]
		reason   RoutineBuildingReason
		previews int
	}{
		{"cramped then playable", []domain.Fact[bool]{domain.Known(false), domain.Known(true)}, "", 2},
		{"cramped", []domain.Fact[bool]{domain.Known(false), domain.Known(false)}, BuildingMethodNoSpace, 2},
		{"unavailable", []domain.Fact[bool]{domain.Unknown[bool](), domain.Known(false)}, BuildingMethodUnknown, 2},
	} {
		t.Run(test.name, func(t *testing.T) {
			planner, _, session, _, native := sleepingFixture(t)
			planner.goal, planner.definition, planner.environment = policy.EnsureComfort, "HorseshoesPin", policy.PlacementAnywhere
			native.onPreview = func(_ context.Context, p *bridge.BuildingPreview) {
				b, _ := p.Preview.Action.Building()
				p.Preview.Footprint = domain.Known([]domain.Cell{b.Cell()})
				p.Preview.WatchCellsAccessible = test.access[native.previews-1]
			}
			facts := observation.ColonyProjection{Bounds: policy.Bounds{Width: 10, Height: 10}, Center: domain.Cell{X: 2, Z: 2}, Identity: observation.Identity{Tick: domain.Tick(native.reply.GetObserved().Context.GetTick())}}
			for _, c := range []domain.Cell{{X: 2, Z: 2}, {X: 3, Z: 2}} {
				facts.Cells = append(facts.Cells, policy.SiteCell{Cell: c, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false)})
			}
			selected, _, reason, err := planner.previewMethod(context.Background(), session.State().Snapshot, facts, nil, 1, func() error { return nil })
			if err != nil || reason != test.reason || native.previews != test.previews {
				t.Fatal(selected, reason, err, native.previews)
			}
			if reason == "" {
				b, _ := selected[0].Action.Building()
				if b.Cell() != (domain.Cell{X: 3, Z: 2}) {
					t.Fatal("cramped site selected", b)
				}
			}
		})
	}
}

func TestRoutineBuildingNativeUseBudgetRequiresOutcomeAndCurrentDirection(t *testing.T) {
	t.Parallel()
	for _, definition := range []string{"Table1x2c", "DiningChair", "HorseshoesPin", "Campfire", "WoodFiredGenerator", "PowerConduit"} {
		t.Run(definition, func(t *testing.T) {
			budget := comfortNativeWorkTicks
			if definition == "WoodFiredGenerator" || definition == "PowerConduit" {
				budget = powerNativeWorkTicks
			}
			building, err := domain.NewBuilding(definition, domain.Cell{X: 2, Z: 2}, domain.North, "")
			if err != nil {
				t.Fatal(err)
			}
			action, err := domain.NewBuildingAction("a", building)
			if err != nil {
				t.Fatal(err)
			}
			spec, err := domain.NewPlan("plan", 1, []domain.Action{action})
			if err != nil {
				t.Fatal(err)
			}
			p, err := domain.NewProgress(spec, action.ID())
			if err != nil {
				t.Fatal(err)
			}
			// Reuse the validated native/session scope from the runtime fixture.
			_, _, session, _, _ := sleepingFixture(t)
			current := session.State().Snapshot
			snapshot := current
			snapshot.Plan, snapshot.Revision = spec.ID(), spec.Revision()
			state := store.PlanState{Spec: spec, Progress: []domain.Progress{p}}
			if budget(state, current, 7) != 0 {
				t.Fatal("pending furniture granted time")
			}
			p, err = p.Prepare(snapshot, 7)
			if err != nil {
				t.Fatal(err)
			}
			p, err = p.MarkDispatched(snapshot, 7)
			if err != nil {
				t.Fatal(err)
			}
			p, err = p.RecordReceipt(1, domain.ReceiptAccepted)
			if err != nil {
				t.Fatal(err)
			}
			state.Progress[0] = p
			if budget(state, current, 7) != 0 {
				t.Fatal("receipt granted time")
			}
			p, err = p.Observe(domain.Observation{Action: action.ID(), Attempt: 1, Snapshot: snapshot, Tick: 100, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch}, snapshot)
			if err != nil {
				t.Fatal(err)
			}
			state.Progress[0] = p
			for _, row := range []struct {
				tick domain.Tick
				want uint32
			}{{99, 0}, {100, 120}, {101, 120}, {10099, 1}, {10100, 0}} {
				want := row.want
				if definition == "Campfire" {
					want = 0
				}
				if got := budget(state, current, row.tick); got != want {
					t.Fatal(row, got, want)
				}
			}
			current.Native++
			if budget(state, current, 100) != 0 {
				t.Fatal("new direction inherited time")
			}
		})
	}
}

func TestComfortBuilderHonorsNativeSkillAndPlayerWorkPreferences(t *testing.T) {
	t.Parallel()
	pawn := policy.WorkPawn{ID: "builder", Available: domain.Known(true), Applies: domain.Known(true), Manual: domain.Known(true), Ranged: domain.Known(false)}
	var skills []policy.WorkSkill
	for _, name := range []string{"Construction", "Plants", "Cooking", "Medicine", "Shooting"} {
		skills = append(skills, policy.WorkSkill{Name: name, Level: 4, Passion: "None"})
	}
	pawn.Skills = domain.Known(skills)
	var priorities []policy.WorkPriority
	for _, name := range []policy.WorkType{"Construction", "Growing", "Cooking", "Doctor", "PlantCutting", "Hunting", "Firefighter"} {
		priorities = append(priorities, policy.WorkPriority{Work: name})
	}
	pawn.Work = domain.Known(priorities)
	decision, err := policy.AssignWork([]policy.WorkPawn{pawn}, nil, nil)
	if err != nil || len(decision.Assignments) != 1 {
		t.Fatal(decision, err)
	}
	pawn.Work = domain.Known(decision.Assignments[0].Priorities)
	facts := observation.ColonyProjection{WorkPawns: domain.Known([]policy.WorkPawn{pawn}), Definitions: []observation.PlanningDefinition{{Name: "DiningChair", Available: domain.Known(true), ConstructionSkill: domain.Known(int32(4))}}}
	if !comfortBuilderAvailable(facts, "DiningChair", nil) {
		t.Fatal("native skilled furniture rejected despite qualified assigned builder")
	}
	if comfortBuilderAvailable(facts, "DiningChair", []policy.WorkOverride{{Pawn: "builder", Work: "Construction", Priority: 0}}) {
		t.Fatal("player disabled builder was ignored")
	}
	skills[0].Level = 3
	if comfortBuilderAvailable(facts, "DiningChair", nil) {
		t.Fatal("native construction prerequisite bypassed")
	}
	skills[0].Level = 4
	facts.WorkPawns = domain.Unknown[[]policy.WorkPawn]()
	if comfortBuilderAvailable(facts, "DiningChair", nil) {
		t.Fatal("missing work census accepted")
	}
}

func TestComfortCompilerResolvesNativeMaterialAndDiningAdjacency(t *testing.T) {
	t.Parallel()
	planner := &RoutineBuildingPlanner{goal: policy.EnsureComfort}
	people := []policy.PawnID{"pawn"}
	census := policy.ComfortObservation{People: people}
	facts := observation.ColonyProjection{Facts: policy.RoutineFacts{Comfort: domain.Known(census)}, Definitions: []observation.PlanningDefinition{{Name: "Table1x2c", Stuff: domain.Known("WoodLog")}, {Name: "DiningChair", Stuff: domain.Known("WoodLog")}}}
	selected, reason, err := planner.selectComfort(facts, policy.ComfortHistory{})
	if err != nil || reason != "" || selected.definition != "Table1x2c" || selected.stuff != "WoodLog" || selected.environment != policy.PlacementIndoors || selected.facility == nil || selected.facility.Role != policy.RoomRoleDiningRoom {
		t.Fatal(selected, reason, err)
	}
	census.Surfaces = []policy.DiningSurface{{ID: "table", Adjacent: []domain.Cell{{X: 2, Z: 3}}}}
	facts.Facts.Comfort = domain.Known(census)
	selected, reason, err = planner.selectComfort(facts, policy.ComfortHistory{})
	if err != nil || reason != "" || selected.definition != "DiningChair" || len(selected.adjacent) != 1 || selected.adjacent[0] != (domain.Cell{X: 2, Z: 3}) {
		t.Fatal(selected, reason, err)
	}
	census.Dining = []policy.ComfortFacility{{ID: "chair", AccessibleTo: people}}
	facts.Facts.Comfort = domain.Known(census)
	selected, reason, err = planner.selectComfort(facts, policy.ComfortHistory{})
	if err != nil || reason != "" || selected.definition != "HorseshoesPin" || selected.environment != policy.PlacementIndoors || selected.facility == nil || selected.facility.Role != policy.RoomRoleRecRoom {
		t.Fatal(selected, reason, err)
	}
	census.Recreation = []policy.ComfortFacility{{ID: "hoop", AccessibleTo: people}}
	facts.Facts.Comfort = domain.Known(census)
	selected, reason, err = planner.selectComfort(facts, policy.ComfortHistory{})
	if err != nil || reason != BuildingComfortWait || selected != nil {
		t.Fatal(selected, reason, err)
	}
	if planner.definition != "" || planner.stuff != "" || len(planner.adjacent) != 0 {
		t.Fatal("selection mutated reusable compiler", planner)
	}
}

func TestComfortFurnishingOnlyPreviewsHostingRoomsAndFallsBackToShell(t *testing.T) {
	t.Parallel()
	dining, _ := policy.Facility(policy.RoomRoleDiningRoom)
	site := func(cells ...domain.Cell) []policy.SiteCell {
		var rows []policy.SiteCell
		for _, c := range cells {
			rows = append(rows, policy.SiteCell{Cell: c, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false), Roofed: domain.Known(true), Indoors: domain.Known(true)})
		}
		return rows
	}
	barracks, hosting := domain.Cell{X: 2, Z: 2}, domain.Cell{X: 3, Z: 2}
	for _, test := range []struct {
		name   string
		rooms  domain.Fact[policy.RoomObservation]
		reason RoutineBuildingReason
		cell   domain.Cell
	}{
		{"hosting room only", domain.Known(policy.RoomObservation{Rooms: []policy.Room{{ID: "b", Role: domain.Known(policy.RoomRoleBarracks), Cells: []domain.Cell{barracks}}, {ID: "d", Role: domain.Known(policy.RoomRoleRecRoom), Cells: []domain.Cell{hosting}}}}), "", hosting},
		{"no hosting room", domain.Known(policy.RoomObservation{Rooms: []policy.Room{{ID: "b", Role: domain.Known(policy.RoomRoleBarracks), Cells: []domain.Cell{barracks, hosting}}}}), BuildingMethodNoSpace, domain.Cell{}},
		{"census unknown", domain.Unknown[policy.RoomObservation](), BuildingMethodUnknown, domain.Cell{}},
	} {
		t.Run(test.name, func(t *testing.T) {
			planner, _, session, _, native := sleepingFixture(t)
			planner.goal, planner.definition, planner.environment, planner.facility = policy.EnsureComfort, "Table1x2c", policy.PlacementIndoors, &dining
			native.onPreview = func(_ context.Context, p *bridge.BuildingPreview) {
				b, _ := p.Preview.Action.Building()
				p.Preview.Footprint = domain.Known([]domain.Cell{b.Cell()})
			}
			facts := observation.ColonyProjection{Bounds: policy.Bounds{Width: 10, Height: 10}, Center: domain.Cell{X: 2, Z: 2}, Identity: observation.Identity{Tick: domain.Tick(native.reply.GetObserved().Context.GetTick())}, Cells: site(barracks, hosting), Rooms: test.rooms}
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
	shell := &RoutineBuildingPlanner{goal: policy.EnsureComfort, definition: "Wall", shelter: true}
	facts := observation.ColonyProjection{Facts: policy.RoutineFacts{Colonists: domain.Known(int64(2))}}
	if missing, method, reason := shell.selection(facts); missing != 32 || method != "comfort-shell" || reason != "" {
		t.Fatal(missing, method, reason)
	}
}
