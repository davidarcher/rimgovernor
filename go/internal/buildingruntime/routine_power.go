package buildingruntime

import (
	"context"
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func NewRoutinePowerPlanner(reviewer *RoutineReviewer, native RoutineBuildingSource) (*RoutineBuildingPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoutinePowerPlanner: reviewer == nil || native == nil", ErrControl)
	}
	if _, ok := native.(observation.RoutineSource); !ok {
		return nil, fmt.Errorf("%w: NewRoutinePowerPlanner: !ok", ErrControl)
	}
	return &RoutineBuildingPlanner{reviewer: reviewer, native: native, goal: policy.EnsureBasicPower}, nil
}

// selectPower maps the policy outcome onto the planner. pendingConsumers
// names, with multiplicity, the buildings other goals' admitted plans are
// about to build; their declared draw joins the budget's demand.
func (r *RoutineBuildingPlanner) selectPower(facts observation.ColonyProjection, pendingConsumers []string) (*RoutineBuildingPlanner, Verdict, error) {
	planning := policy.DefaultPowerPlanning()
	planning.Generators = facts.GeneratorOptions()
	planning.BatteryAvailable = facts.DefinitionAvailable(policy.BatteryDefinition)
	planning.GeothermalAvailable = facts.DefinitionAvailable(policy.GeothermalDefinition)
	planning.PendingDemandW = pendingDemand(facts, pendingConsumers)
	proposal, err := policy.SelectPowerMethod(facts.PowerPlanning, facts.Bounds, facts.Cells, nil, planning)
	if err != nil {
		return nil, Verdict{}, err
	}
	switch proposal.Method {
	case policy.PowerUnknown:
		return nil, fieldUnavailable("power"), nil
	case policy.PowerNoMethod:
		return nil, BuildingReasonNoDeficit, nil
	case policy.PowerRouteBlocked:
		return nil, BuildingReasonNoSpace, nil
	case policy.PowerNoGenerator:
		// Every generator the family can compile is unavailable; when the
		// research they require is unfinished, that is what the goal waits
		// on, not a cheaper generator.
		if gate := policy.ResearchGate(generatorResearch(facts), facts.Facts.Research); gate != "" {
			return nil, researchWait(gate), nil
		}
		return nil, awaitingMethod(proposal.Method), nil
	case policy.PowerWaitOutput, policy.PowerWaitFuel, policy.PowerWaitRepair, policy.PowerWaitBlackout, policy.PowerWaitPlayer, policy.PowerWaitCharge:
		return nil, awaitingMethod(proposal.Method), nil
	}
	resolved := *r
	resolved.power = &proposal
	resolved.definition, resolved.environment = string(proposal.Method), policy.PlacementAnywhere
	switch proposal.Method {
	case policy.PowerGenerate:
		// A geothermal generator is anchored on its geyser (FixedSite);
		// every other generator is searched for beside the consumer.
		resolved.definition = proposal.Definition
	case policy.PowerStore:
		// A battery short-circuits unroofed in rain or snow, so the bank
		// sits indoors.
		resolved.definition, resolved.environment = proposal.Definition, policy.PlacementIndoors
	}
	if proposal.Method == policy.PowerShelter {
		resolved.definition = "Wall"
	}
	return &resolved, Verdict{}, nil
}

// pendingBuildingDefinitions lists, with multiplicity, the definitions of
// building actions still open across every held reservation: what other
// goals are about to build.
func pendingBuildingDefinitions(held []policy.Reservation) []string {
	var out []string
	for _, h := range held {
		b, ok := h.Progress.Action().Building()
		if !ok || !pendingWork(h.Progress) || slices.Contains(policy.PowerFamilyDefinitions(), b.Definition()) {
			continue
		}
		if len(out) == 64 {
			break
		}
		out = append(out, b.Definition())
	}
	return out
}

// pendingDemand sums the declared draw of the pending consumers the census
// describes (PlanningDefinition.PowerW, positive for a consumer).
func pendingDemand(facts observation.ColonyProjection, pending []string) float64 {
	draw := map[string]float64{}
	for _, d := range facts.Definitions {
		if w, known := d.PowerW.Value(); known && w > 0 {
			draw[d.Name] = w
		}
	}
	total := 0.0
	for _, name := range pending {
		total += draw[name]
	}
	return total
}

// generatorResearch lists, in policy.GeneratorDefinitions order, the native
// research every unavailable generator definition requires.
func generatorResearch(facts observation.ColonyProjection) []string {
	definitions := map[string]observation.PlanningDefinition{}
	for _, d := range facts.Definitions {
		definitions[d.Name] = d
	}
	seen := map[string]bool{}
	var required []string
	for _, name := range policy.GeneratorDefinitions {
		d, ok := definitions[name]
		if !ok {
			continue
		}
		if available, known := d.Available.Value(); !known || available {
			continue
		}
		for _, project := range d.Research {
			if !seen[project] {
				seen[project] = true
				required = append(required, project)
			}
		}
	}
	return required
}

// Existing native power may need ordinary hauling/refueling, but only a
// completed method in this direction can lend a non-renewable clock budget.
func powerOutputAllowance(ctx context.Context, journal *store.Store, goal domain.Goal, current domain.GenerationSnapshot, tick domain.Tick) (uint32, error) {
	methods, err := journal.LoadGoalMethods(ctx, goal.ID, goal.Epoch)
	if err != nil {
		return 0, err
	}
	var allowance uint32
	for _, m := range methods {
		plan, err := journal.LoadPlan(ctx, m.Plan)
		if err != nil {
			return 0, err
		}
		allowance = max(allowance, powerNativeWorkTicks(plan, current, tick))
		if len(plan.Spec.Actions()) > 0 {
			walls := true
			for _, a := range plan.Spec.Actions() {
				b, ok := a.Building()
				walls = walls && ok && (b.Definition() == "Wall" || b.Definition() == "Door")
			}
			if walls {
				allowance = max(allowance, shelterNativeWorkTicks(plan, current, tick))
			}
		}
	}
	return allowance, nil
}

func (r *RoutineBuildingPlanner) previewPowerShelter(ctx context.Context, snapshot domain.GenerationSnapshot, facts observation.ColonyProjection, protected []domain.Cell, check func() error) ([]policy.Preview, policy.StockObservation, Verdict, error) {
	stock := policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}
	wall, wok := animalContainmentDefinition(facts.Definitions, "Wall")
	door, dok := animalContainmentDefinition(facts.Definitions, "Door")
	if !wok || !dok {
		return nil, stock, fieldUnavailable("wall_door_definitions"), nil
	}
	stuff, known := animalContainmentStuff(wall, door)
	if !known {
		return nil, stock, fieldUnavailable("wall_door_stuff"), nil
	}
	room := r.power.Room
	entry := domain.Cell{X: room.X + room.Width/2, Z: room.Z}
	perimeter := []domain.Cell{entry}
	for x := room.X; x < room.X+room.Width; x++ {
		for z := room.Z; z < room.Z+room.Height; z++ {
			c := domain.Cell{X: x, Z: z}
			if c != entry && (x == room.X || x == room.X+room.Width-1 || z == room.Z || z == room.Z+room.Height-1) {
				perimeter = append(perimeter, c)
			}
		}
	}
	blocked := map[domain.Cell]bool{}
	for _, c := range protected {
		blocked[c] = true
	}
	var selected []policy.Preview
	for i, c := range perimeter {
		if blocked[c] {
			return nil, stock, BuildingReasonExistingWork, nil
		}
		if err := check(); err != nil {
			return nil, stock, Verdict{}, err
		}
		name := "Wall"
		if i == 0 {
			name = "Door"
		}
		b, err := domain.NewBuilding(name, c, domain.North, stuff)
		if err != nil {
			return nil, stock, Verdict{}, err
		}
		a, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", snapshot.Plan, i)), b)
		if err != nil {
			return nil, stock, Verdict{}, err
		}
		p, _, err := r.native.PreviewBuilding(ctx, a, snapshot)
		if err != nil {
			return nil, stock, Verdict{}, err
		}
		v := p.Preview
		footprint, fk := v.Footprint.Value()
		can, ck := v.CanPlace.Value()
		safe, sk := v.SafeToPlace.Value()
		made, mk := v.MadeFromStuff.Value()
		if !fk || !ck || !sk || !mk {
			return nil, stock, fieldUnavailable("power_preview"), nil
		}
		if len(footprint) != 1 || footprint[0] != c || !can || !safe || made != (stuff != "") {
			return nil, stock, BuildingReasonNoSpace, nil
		}
		if err = mergeRoutineStock(&stock, p.Stock, i == 0); err != nil {
			return nil, stock, Verdict{}, err
		}
		selected = append(selected, v)
	}
	return selected, stock, Verdict{}, nil
}

// powerDefinition reports whether a completed building belongs to the power
// family: a conduit, any compilable generator or the battery.
func powerDefinition(name string) bool {
	if name == "PowerConduit" || name == "HiddenConduit" || name == "WaterproofConduit" {
		return true
	}
	return slices.Contains(policy.PowerFamilyDefinitions(), name)
}

func powerNativeWorkTicks(plan store.PlanState, current domain.GenerationSnapshot, tick domain.Tick) uint32 {
	if len(plan.Progress) < 1 || len(plan.Progress) > 8 {
		return 0
	}
	current.Plan, current.Revision = plan.Spec.ID(), plan.Spec.Revision()
	var completed domain.Tick
	for _, p := range plan.Progress {
		b, ok := p.Action().Building()
		if !ok || !powerDefinition(b.Definition()) {
			return 0
		}
		v := p.View()
		effect, known := v.Effect.Value()
		if v.Stage != domain.Completed || v.Unresolved || !known || effect != domain.EffectCompleted || !v.Snapshot.Matches(current) || tick < v.Tick {
			return 0
		}
		completed = max(completed, v.Tick)
	}
	if tick-completed >= 10000 {
		return 0
	}
	return min(uint32(120), uint32(10000-(tick-completed)))
}

// previewPowerSite previews the one placement a fixed-site proposal names: a
// geothermal generator centred on its geyser. The geyser itself stands on
// the footprint, so the site census (which reports it occupied) is not
// consulted; the native preview's legality and safety are.
func (r *RoutineBuildingPlanner) previewPowerSite(ctx context.Context, snapshot domain.GenerationSnapshot, facts observation.ColonyProjection, protected []domain.Cell, check func() error) ([]policy.Preview, policy.StockObservation, Verdict, error) {
	stock := policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}
	if r.power == nil || !r.power.FixedSite() {
		return nil, stock, Verdict{}, fmt.Errorf("%w: previewPowerSite: r.power == nil || !r.power.FixedSite()", ErrControl)
	}
	if err := check(); err != nil {
		return nil, stock, Verdict{}, err
	}
	building, err := domain.NewBuilding(r.definition, r.power.Center, domain.North, "")
	if err != nil {
		return nil, stock, Verdict{}, err
	}
	action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-0", snapshot.Plan)), building)
	if err != nil {
		return nil, stock, Verdict{}, err
	}
	preview, _, err := r.native.PreviewBuilding(ctx, action, snapshot)
	if err != nil {
		return nil, stock, Verdict{}, err
	}
	p := preview.Preview
	footprint, fk := p.Footprint.Value()
	made, mk := p.MadeFromStuff.Value()
	legal, lk := p.CanPlace.Value()
	safe, sk := p.SafeToPlace.Value()
	if !fk || !mk || !lk || !sk {
		return nil, stock, fieldUnavailable("power_preview"), nil
	}
	blocked := map[domain.Cell]bool{}
	for _, c := range protected {
		blocked[c] = true
	}
	for _, c := range footprint {
		if blocked[c] {
			return nil, stock, BuildingReasonExistingWork, nil
		}
	}
	if made || len(footprint) == 0 || !legal || !safe {
		clockSchedulerLog("%s: no site for %s on geyser %v: legal=%v safe=%v blockers=%v", r.goal, r.definition, r.power.Center, legal, safe, p.Blockers)
		return nil, stock, BuildingReasonNoSpace, nil
	}
	if err = mergeRoutineStock(&stock, preview.Stock, true); err != nil {
		return nil, stock, Verdict{}, err
	}
	return []policy.Preview{p}, stock, Verdict{}, nil
}

// previewPowerOrSearch places a generator or battery on the v2 layout
// plan's sites (#788) at Masonry and above, and falls back to the search
// near the consumer when the plan has no open site for it (no plan yet, the
// battery room not built and roofed, every site taken or refused).
func (r *RoutineBuildingPlanner) previewPowerOrSearch(call context.Context, snapshot domain.GenerationSnapshot, facts observation.ColonyProjection, protected []domain.Cell, missing int64, check func() error) ([]policy.Preview, policy.StockObservation, Verdict, error) {
	if r.power != nil && (r.power.Method == policy.PowerGenerate || r.power.Method == policy.PowerStore) {
		selected, stock, ok, err := r.previewPlannedPower(call, snapshot, facts, protected, missing, check)
		if err != nil || ok {
			return selected, stock, Verdict{}, err
		}
	}
	return r.previewSearch(call, snapshot, facts, protected, missing, check)
}

// previewPlannedPower previews the plan's sites for r.definition in plan
// order and keeps the first missing ones the native accepts: legal, safe,
// on exactly the planned footprint, a turbine with a clear catch zone, a
// battery under roof. A battery past its side's first row brings the wall
// block between it and the one before, in the style's run stuff (stone at
// Masonry), unless one stands. False when fewer than missing sites fit.
func (r *RoutineBuildingPlanner) previewPlannedPower(ctx context.Context, snapshot domain.GenerationSnapshot, facts observation.ColonyProjection, protected []domain.Cell, missing int64, check func() error) ([]policy.Preview, policy.StockObservation, bool, error) {
	stock := policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}
	plan, planned := facts.LayoutPlan.Value()
	if tier, ok := facts.BuildTier.Value(); !planned || !ok || tier < policy.BuildTierMasonry || missing < 1 {
		return nil, stock, false, nil
	}
	sites := policy.PlannedPowerSites(plan, r.definition)
	if len(sites) == 0 {
		clockSchedulerLog("%s: the plan reserves no %s site", r.goal, r.definition)
		return nil, stock, false, nil
	}
	// refused names why each planned site was passed over, logged once when
	// fewer than missing sites fit (#1585: a reserved turbine that is never
	// built left no trace).
	var refused []string
	blocked := map[domain.Cell]bool{}
	for _, c := range protected {
		blocked[c] = true
	}
	built := map[domain.Cell]bool{}
	if census, ok := facts.Facts.CurrentConstruction.Value(); ok && census.Colony {
		for _, b := range census.Buildings {
			for _, c := range b.Cells {
				built[c] = true
			}
		}
	}
	roofed := map[domain.Cell]bool{}
	for _, c := range facts.Cells {
		if v, known := c.Roofed.Value(); known && v {
			roofed[c.Cell] = true
		}
	}
	wallStuff := ""
	if s, ok := policy.WallStuffFor(styleTier(facts), policy.WallRun, styleStock(facts), styleWoody(facts)); ok {
		wallStuff = string(s)
	}
	n, merged := 0, 0
	preview := func(name string, c domain.Cell, rot domain.Rotation, stuff string, area policy.Rectangle) (policy.Preview, bool, error) {
		if err := check(); err != nil {
			return policy.Preview{}, false, err
		}
		b, err := domain.NewBuilding(name, c, rot, stuff)
		if err != nil {
			return policy.Preview{}, false, err
		}
		a, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", snapshot.Plan, n)), b)
		if err != nil {
			return policy.Preview{}, false, err
		}
		n++
		p, _, err := r.native.PreviewBuilding(ctx, a, snapshot)
		if err != nil {
			return policy.Preview{}, false, err
		}
		v := p.Preview
		footprint, fk := v.Footprint.Value()
		can, ck := v.CanPlace.Value()
		safe, sk := v.SafeToPlace.Value()
		made, mk := v.MadeFromStuff.Value()
		if !fk || !ck || !sk || !mk || !can || !safe || made != (stuff != "") || !sameCells(footprint, policy.RectangleCells(area)) {
			refused = append(refused, fmt.Sprintf("%s@%v: known=%v/%v/%v/%v canPlace=%v safe=%v madeFromStuff=%v footprintMatches=%v blockers=%v", name, c, fk, ck, sk, mk, can, safe, made, sameCells(footprint, policy.RectangleCells(area)), v.Blockers))
			return policy.Preview{}, false, nil
		}
		if name == policy.WindTurbineDefinition {
			if w, known := v.WindBlockedCells.Value(); !known || w > 0 {
				refused = append(refused, fmt.Sprintf("%s@%v: WindBlockedCells=%d known=%v", name, c, w, known))
				return policy.Preview{}, false, nil
			}
		}
		if err = mergeRoutineStock(&stock, p.Stock, merged == 0); err != nil {
			return policy.Preview{}, false, err
		}
		merged++
		return v, true, nil
	}
	var selected []policy.Preview
	placed := int64(0)
	for _, s := range sites {
		if placed == missing {
			break
		}
		cells := policy.RectangleCells(s.Area)
		free := true
		for _, c := range cells {
			free = free && !blocked[c] && !built[c] && (r.definition != policy.BatteryDefinition || roofed[c])
		}
		if !free {
			refused = append(refused, fmt.Sprintf("%s@%v: site cells blocked, built or (battery) unroofed", r.definition, s.Cell))
			continue
		}
		v, ok, err := preview(r.definition, s.Cell, s.Rotation, r.stuff, s.Area)
		if err != nil {
			return nil, stock, false, err
		}
		if !ok {
			continue
		}
		selected = append(selected, v)
		placed++
		for _, c := range policy.RectangleCells(s.Block) {
			if built[c] || blocked[c] {
				continue
			}
			w, ok, err := preview("Wall", c, domain.North, wallStuff, policy.Rectangle{X: c.X, Z: c.Z, Width: 1, Height: 1})
			if err != nil {
				return nil, stock, false, err
			}
			if ok {
				selected = append(selected, w)
			}
		}
	}
	if placed < missing {
		clockSchedulerLog("%s: planned %s sites fit %d of %d missing: %v", r.goal, r.definition, placed, missing, refused)
		return nil, stock, false, nil
	}
	return selected, stock, true, nil
}

// sameCells reports whether a and b hold the same cells.
func sameCells(a, b []domain.Cell) bool {
	if len(a) != len(b) {
		return false
	}
	set := map[domain.Cell]bool{}
	for _, c := range a {
		set[c] = true
	}
	for _, c := range b {
		if !set[c] {
			return false
		}
	}
	return true
}

// Conduits may legally underlay occupied cells. Validate the exact observed
// route with native previews instead of treating occupied cells as free floor.
func (r *RoutineBuildingPlanner) previewPowerRoute(ctx context.Context, snapshot domain.GenerationSnapshot, facts observation.ColonyProjection, protected []domain.Cell, check func() error) ([]policy.Preview, policy.StockObservation, Verdict, error) {
	stock := policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}
	if r.power == nil || r.power.Method != policy.PowerConnect || len(r.power.Cells) < 1 || len(r.power.Cells) > 8 {
		return nil, stock, Verdict{}, fmt.Errorf("%w: previewPowerRoute: r.power == nil || r.power.Method != policy.PowerConnect || len(r.power.Cells) < 1 || len(r.power.Cells) > 8", ErrControl)
	}
	blocked := map[domain.Cell]bool{}
	for _, c := range protected {
		blocked[c] = true
	}
	var selected []policy.Preview
	for i, cell := range r.power.Cells {
		if blocked[cell] {
			return nil, stock, BuildingReasonExistingWork, nil
		}
		if err := check(); err != nil {
			return nil, stock, Verdict{}, err
		}
		building, err := domain.NewBuilding(string(policy.PowerConnect), cell, domain.North, "")
		if err != nil {
			return nil, stock, Verdict{}, err
		}
		action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", snapshot.Plan, i)), building)
		if err != nil {
			return nil, stock, Verdict{}, err
		}
		preview, _, err := r.native.PreviewBuilding(ctx, action, snapshot)
		if err != nil {
			return nil, stock, Verdict{}, err
		}
		p := preview.Preview
		footprint, fk := p.Footprint.Value()
		made, mk := p.MadeFromStuff.Value()
		legal, lk := p.CanPlace.Value()
		safe, sk := p.SafeToPlace.Value()
		if !fk || !mk || !lk || !sk {
			return nil, stock, fieldUnavailable("power_preview"), nil
		}
		if made || len(footprint) != 1 || footprint[0] != cell || !legal || !safe {
			return nil, stock, BuildingReasonNoSpace, nil
		}
		selected = append(selected, p)
		if err = mergeRoutineStock(&stock, preview.Stock, i == 0); err != nil {
			return nil, stock, Verdict{}, err
		}
	}
	return selected, stock, Verdict{}, nil
}
