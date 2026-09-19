package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// NewRoutineShelterPlanner prefers furnishing verified indoor space. Only when
// that whole method has no space does it propose a native-grounded starter shell.
//
// With an excavation source the planner also considers digging the room
// into visible mountain rock and picks whichever site policy.ChooseExcavation
// prefers; a nil source keeps the open-site shell only.
func NewRoutineShelterPlanner(reviewer *RoutineReviewer, native RoutineBuildingSource, excavation RoutineExcavationSource) (*RoutineBuildingPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	return &RoutineBuildingPlanner{reviewer: reviewer, native: native, excavation: excavation, goal: policy.EnsureInitialShelter, definition: "Wall", shelter: true}, nil
}

// Expansion reuses the same furnishing and whole-shell admission path to keep
// one spare indoor sleeping place beyond the observed population.
func NewRoutineExpansionPlanner(reviewer *RoutineReviewer, native RoutineBuildingSource, excavation RoutineExcavationSource) (*RoutineBuildingPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	return &RoutineBuildingPlanner{reviewer: reviewer, native: native, excavation: excavation, goal: policy.EnsureExpansion, definition: "Wall", shelter: true}, nil
}

// shelterStyle maps the player faction's native tech level to a shell shape:
// a neolithic colony raises circular and oval huts, everyone else the
// rectangle. An unknown tech level keeps the rectangle.
func shelterStyle(facts observation.ColonyProjection) policy.ShelterStyle {
	if level, known := facts.PlayerTechLevel.Value(); known && level == "Neolithic" {
		return policy.ShelterHut
	}
	return policy.ShelterRectangle
}

// A completed starter shell may trigger RimWorld's normal automatic roofing.
// Give that work at most four in-game hours from the durable completion tick.
// Polling, restarting or cancelling cannot renew this budget. Shells vary in
// size by shape, so any nonempty fully completed plan qualifies. The budget
// is scoped to the world and plan revision the walls were completed in, not
// to the native order generation: an authority re-acquisition between the
// last dispatch and the review (a cancelled transport call, #174) moves the
// generation on without touching the standing walls, and nothing else lends
// the clock the roof needs when the shell is the only work.
func shelterNativeWorkTicks(plan store.PlanState, current domain.GenerationSnapshot, tick domain.Tick) uint32 {
	if len(plan.Progress) == 0 {
		return 0
	}
	completed := domain.Tick(0)
	for _, p := range plan.Progress {
		v := p.View()
		effect, known := v.Effect.Value()
		if v.Stage != domain.Completed || v.Unresolved || !known || effect != domain.EffectCompleted || !boundary.World(v.Snapshot, current) ||
			v.Snapshot.Plan != plan.Spec.ID() || v.Snapshot.Revision != plan.Spec.Revision() || v.Tick > tick {
			return 0
		}
		completed = max(completed, v.Tick)
	}
	const budget domain.Tick = 10000
	if tick-completed >= budget {
		return 0
	}
	return uint32(budget - (tick - completed))
}

func routineDefinitionsAvailable(facts observation.ColonyProjection, names []string, shell bool) bool {
	for _, name := range names {
		available := false
		for _, def := range facts.Definitions {
			if def.Name != name {
				continue
			}
			ready, known := def.Available.Value()
			skill, skillKnown := def.ConstructionSkill.Value()
			available = known && ready && skillKnown && skill == 0
			if shell {
				size, known := def.Size.Value()
				available = available && known && size.Width == 1 && size.Height == 1
			}
		}
		if !available {
			return false
		}
	}
	return true
}

// structureReader is the optional native census a shell planner uses to
// recognise a shell it began earlier; sources without it always site afresh.
type structureReader interface {
	ReadStructures(ctx context.Context, identity *c.Identity, minimum, maximum domain.Cell, definitions []string) (bridge.StructureRead, bridge.Result, error)
}

// shellAdoptionReach bounds the census of earlier walls and doors to the
// colony centre's neighbourhood, where the starter search sites shells.
const shellAdoptionReach int32 = 64

func (r *RoutineBuildingPlanner) previewShell(ctx context.Context, snapshot domain.GenerationSnapshot, facts observation.ColonyProjection, protected []domain.Cell, check func() error) ([]policy.Preview, policy.StockObservation, RoutineBuildingReason, error) {
	style := shelterStyle(facts)
	if selected, stock, reason, adopted, err := r.adoptShell(ctx, snapshot, facts, protected, style, check); err != nil || adopted {
		return selected, stock, reason, err
	}
	var cells []policy.SiteCell
	for _, c := range facts.Cells {
		indoors, indoorKnown := c.Indoors.Value()
		roof, roofKnown := c.Roofed.Value()
		if indoorKnown && !indoors && roofKnown && !roof {
			cells = append(cells, c)
		}
	}
	layouts, err := policy.StarterLayouts(policy.StarterRequest{Bounds: facts.Bounds, Anchor: facts.Center, Cells: cells, Protected: protected, Shelter: style})
	if err != nil {
		return nil, policy.StockObservation{}, "", err
	}
	for candidate, layout := range layouts {
		perimeter := layout.Shell.Placements("Wall", "Door", "WoodLog")
		if len(perimeter) == 0 {
			return nil, policy.StockObservation{}, "", ErrControl
		}
		stock := policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}
		var selected []policy.Preview
		for i, building := range perimeter {
			preview, placeable, reason, err := r.previewShellCell(ctx, snapshot, facts, domain.ActionID(fmt.Sprintf("%s-%d-%d", snapshot.Plan, candidate, i)), building, check)
			if err != nil || reason != "" {
				return nil, policy.StockObservation{}, reason, err
			}
			if !placeable {
				break
			}
			if err := mergeRoutineStock(&stock, preview.Stock, len(selected) == 0); err != nil {
				return nil, policy.StockObservation{}, "", err
			}
			selected = append(selected, preview.Preview)
		}
		if len(selected) == len(perimeter) {
			return selected, stock, "", nil
		}
	}
	return nil, policy.StockObservation{}, BuildingMethodNoSpace, nil
}

// previewShellCell previews one shell placement natively. It reports whether
// the cell is placeable now; a stale or mismatched preview is ErrControl and
// an unreadable material scan is the unknown-prerequisite reason.
func (r *RoutineBuildingPlanner) previewShellCell(ctx context.Context, snapshot domain.GenerationSnapshot, facts observation.ColonyProjection, id domain.ActionID, building domain.Building, check func() error) (bridge.BuildingPreview, bool, RoutineBuildingReason, error) {
	if err := check(); err != nil {
		return bridge.BuildingPreview{}, false, "", err
	}
	cell := building.Cell()
	action, err := domain.NewBuildingAction(id, building)
	if err != nil {
		return bridge.BuildingPreview{}, false, "", err
	}
	preview, _, err := r.native.PreviewBuilding(ctx, action, snapshot)
	if err != nil {
		return bridge.BuildingPreview{}, false, "", err
	}
	if err := check(); err != nil {
		return bridge.BuildingPreview{}, false, "", err
	}
	v := preview.Preview
	if v.Action != action || !v.Snapshot.Matches(snapshot) || !v.Tick.FreshFor(facts.Identity.Tick) || !preview.Stock.Snapshot.Matches(snapshot) || !preview.Stock.Tick.FreshFor(facts.Identity.Tick) {
		return bridge.BuildingPreview{}, false, "", ErrControl
	}
	stuff, known := v.MadeFromStuff.Value()
	if !known || !stuff {
		return bridge.BuildingPreview{}, false, BuildingMethodUnknown, nil
	}
	footprint, known := v.Footprint.Value()
	can, canKnown := v.CanPlace.Value()
	safe, safeKnown := v.SafeToPlace.Value()
	placeable := known && len(footprint) == 1 && footprint[0] == cell && canKnown && can && safeKnown && safe
	return preview, placeable, "", nil
}

// shellPlanPrefix names every whole-shell plan a shelter-style planner
// admits (initial shelter, expansion, workshop and hospital shells alike);
// adoptShell reads them back as the durable record of the rings it ordered.
const shellPlanPrefix = "routine-shell"

// shellHistoryLimit bounds how many earlier shell plans adoption consults.
// A world orders a handful of shells over its life and each interruption
// adds one partial plan, so the window comfortably holds every ring.
const shellHistoryLimit = 64

// adoptShell recognises a shell this controller began earlier in this world
// and reissues only the cells it is missing. Resuming control invalidates
// routine goals and cancels their plans, so after a restart the walls and
// door already standing, framed or blueprinted natively are the only durable
// native record of the shell; a cancelled frame likewise leaves a gap in an
// otherwise ordered ring. Without this the next review would site a second
// shell beside the first.
//
// The shapes considered at a door are, first, the rings this controller
// itself ordered in this world -- every earlier shell plan carrying a door
// at that cell, read back from the journal, which is how a grown irregular
// shell with no template is recognised -- and then the template shapes the
// starter search issues at that door (policy.ShellShapesAtDoor), which are
// all that remains when the journal did not survive the restart. A door is
// a candidate when it stands natively or when an earlier plan ordered it: a
// ring whose door was cancelled but whose walls stand is still one ring, and
// reissuing it orders the door first with the walls gated on it as a fresh
// shell would. Shapes at one door share their lowest courses, so the shape
// is the one the census matches best, decided before any preview and with
// an earlier plan winning ties over a template: adopting the first shape
// whose remaining cells happened to be placeable issued a second, taller
// ring over a half-built hut once the true ring was briefly blocked. A shape
// nothing standing matches is not adopted. When the best-matched shape is
// not placeable now, or stands whole but encloses no finished room yet, the
// review waits (BuildingShellBlocked) rather than siting a fresh shell
// beside it; a facility ladder passes by any ring that already encloses a
// room, whole or not, since its furnishing step found no site there (#218).
// Doors are tried nearest the colony centre first.
func (r *RoutineBuildingPlanner) adoptShell(ctx context.Context, snapshot domain.GenerationSnapshot, facts observation.ColonyProjection, protected []domain.Cell, style policy.ShelterStyle, check func() error) ([]policy.Preview, policy.StockObservation, RoutineBuildingReason, bool, error) {
	reader, ok := r.native.(structureReader)
	if !ok {
		return nil, policy.StockObservation{}, "", false, nil
	}
	minimum := domain.Cell{X: max(0, facts.Center.X-shellAdoptionReach), Z: max(0, facts.Center.Z-shellAdoptionReach)}
	maximum := domain.Cell{X: min(facts.Bounds.Width-1, facts.Center.X+shellAdoptionReach), Z: min(facts.Bounds.Height-1, facts.Center.Z+shellAdoptionReach)}
	if minimum.X > maximum.X || minimum.Z > maximum.Z {
		return nil, policy.StockObservation{}, "", false, nil
	}
	census, _, err := reader.ReadStructures(ctx, boundary.Identity(snapshot), minimum, maximum, []string{"Wall", "Door"})
	if err != nil {
		return nil, policy.StockObservation{}, "", false, err
	}
	if err := check(); err != nil {
		return nil, policy.StockObservation{}, "", false, err
	}
	if !census.Tick.FreshFor(facts.Identity.Tick) || census.Generation != uint64(snapshot.Native) {
		return nil, policy.StockObservation{}, "", false, ErrControl
	}
	standing := make(map[domain.Cell]string, len(census.Structures))
	seen := map[domain.Cell]bool{}
	var doors []domain.Cell
	for _, s := range census.Structures {
		standing[s.Cell] = s.Definition
		if s.Definition == "Door" && !seen[s.Cell] {
			seen[s.Cell] = true
			doors = append(doors, s.Cell)
		}
	}
	earlier, err := r.earlierShells(ctx, minimum, maximum)
	if err != nil {
		return nil, policy.StockObservation{}, "", false, err
	}
	for door := range earlier {
		if !seen[door] {
			seen[door] = true
			doors = append(doors, door)
		}
	}
	if len(doors) == 0 {
		return nil, policy.StockObservation{}, "", false, nil
	}
	sort.Slice(doors, func(i, j int) bool {
		a, b := squaredDistance(doors[i], facts.Center), squaredDistance(doors[j], facts.Center)
		if a != b {
			return a < b
		}
		return doors[i].Z < doors[j].Z || doors[i].Z == doors[j].Z && doors[i].X < doors[j].X
	})
	guarded := make(map[domain.Cell]bool, len(protected))
	for _, c := range protected {
		guarded[c] = true
	}
	for d, door := range doors {
		shapes := earlier[door]
		for _, shell := range policy.ShellShapesAtDoor(door, style) {
			shapes = append(shapes, shell.Placements("Wall", "Door", "WoodLog"))
		}
		best, bestMatched := -1, 0
		for shape, perimeter := range shapes {
			matched := 0
			for _, building := range perimeter {
				if standing[building.Cell()] == building.Definition() {
					matched++
				}
			}
			if matched > bestMatched {
				best, bestMatched = shape, matched
			}
		}
		if best < 0 {
			continue
		}
		// A ring that already encloses a census room is a finished room,
		// whatever cells its best-matched template still lacks. The initial
		// shelter and expansion own such a ring (adopting it reissues a gap
		// or waits on its roof); a facility ladder reaches here only because
		// its furnishing step found no site inside, so it passes the ring
		// by rather than bind its one shell method to a repair of it (#218).
		if r.facilityLadder() && shellEncloses(shapes[best], facts.Rooms) {
			continue
		}
		stock := policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}
		var selected []policy.Preview
		for i, building := range shapes[best] {
			cell := building.Cell()
			if standing[cell] == building.Definition() {
				continue
			}
			if _, other := standing[cell]; other || guarded[cell] {
				return nil, policy.StockObservation{}, BuildingShellBlocked, true, nil
			}
			preview, placeable, reason, err := r.previewShellCell(ctx, snapshot, facts, domain.ActionID(fmt.Sprintf("%s-adopt-%d-%d-%d", snapshot.Plan, d, best, i)), building, check)
			if err != nil {
				return nil, policy.StockObservation{}, "", false, err
			}
			if reason != "" {
				return nil, policy.StockObservation{}, reason, true, nil
			}
			if !placeable {
				return nil, policy.StockObservation{}, BuildingShellBlocked, true, nil
			}
			if err := mergeRoutineStock(&stock, preview.Stock, len(selected) == 0); err != nil {
				return nil, policy.StockObservation{}, "", false, err
			}
			selected = append(selected, preview.Preview)
		}
		if len(selected) == 0 {
			// The shell stands whole; nothing to adopt and nothing to site
			// while it waits on its roof.
			return nil, policy.StockObservation{}, BuildingShellBlocked, true, nil
		}
		return selected, stock, "", true, nil
	}
	return nil, policy.StockObservation{}, "", false, nil
}

// facilityLadder reports whether the planner walks a facility ladder whose
// last rung stages a room for the furniture (comfort, workshop, hospital,
// sleeping), as opposed to the initial shelter and expansion, whose shell is
// the deficit itself.
func (r *RoutineBuildingPlanner) facilityLadder() bool {
	return r.goal == policy.EnsureComfort || r.goal == policy.MaintainResource || r.goal == policy.MaintainMedicalCare || r.goal == policy.MaintainSleeping
}

// shellEncloses reports whether a standing ring is a finished room: some
// enclosed room of the same-tick census lies within the ring. A ring still
// waiting for its roof encloses nothing, and a facility ladder keeps waiting
// on it rather than siting a second ring beside an unfinished first (#218).
func shellEncloses(perimeter []domain.Building, rooms domain.Fact[policy.RoomObservation]) bool {
	census, known := rooms.Value()
	if !known || len(perimeter) == 0 {
		return false
	}
	ring := make(map[domain.Cell]bool, len(perimeter))
	minimum, maximum := perimeter[0].Cell(), perimeter[0].Cell()
	for _, b := range perimeter {
		cell := b.Cell()
		ring[cell] = true
		minimum.X, minimum.Z = min(minimum.X, cell.X), min(minimum.Z, cell.Z)
		maximum.X, maximum.Z = max(maximum.X, cell.X), max(maximum.Z, cell.Z)
	}
	for _, room := range census.Rooms {
		if enclosed, known := room.Enclosed.Value(); !known || !enclosed || len(room.Cells) == 0 {
			continue
		}
		inside := true
		for _, cell := range room.Cells {
			inside = inside && !ring[cell] && cell.X > minimum.X && cell.X < maximum.X && cell.Z > minimum.Z && cell.Z < maximum.Z
		}
		if inside {
			return true
		}
	}
	return false
}

// earlierShells reads back the rings this controller ordered earlier in this
// world from its shell plans, keyed by door cell: every plan that placed
// exactly one door contributes its complete perimeter (door first, as it
// was admitted), newest first. Partial plans -- adoptions of a ring whose
// door already stood -- carry no door and describe no ring of their own.
// Only doors within the census window count, so a ring the census cannot
// see is never matched against it.
func (r *RoutineBuildingPlanner) earlierShells(ctx context.Context, minimum, maximum domain.Cell) (map[domain.Cell][][]domain.Building, error) {
	history, err := r.reviewer.player.journal.PlanHistoryWithPrefix(ctx, shellPlanPrefix+"-", shellHistoryLimit)
	if err != nil {
		return nil, err
	}
	earlier := map[domain.Cell][][]domain.Building{}
	for _, plan := range history {
		var perimeter []domain.Building
		var door domain.Cell
		doors := 0
		for _, action := range plan.Spec.Actions() {
			b, ok := action.Building()
			if !ok {
				continue
			}
			if b.Definition() == "Door" {
				door, doors = b.Cell(), doors+1
			}
			perimeter = append(perimeter, b)
		}
		if doors != 1 || door.X < minimum.X || door.X > maximum.X || door.Z < minimum.Z || door.Z > maximum.Z {
			continue
		}
		earlier[door] = append(earlier[door], perimeter)
	}
	return earlier, nil
}

func squaredDistance(a, b domain.Cell) int64 {
	dx, dz := int64(a.X-b.X), int64(a.Z-b.Z)
	return dx*dx + dz*dz
}

// Every preview in a paused project must agree on each shared stock value.
func mergeRoutineStock(stock *policy.StockObservation, next policy.StockObservation, first bool) error {
	if len(next.Values) > 256 {
		return ErrControl
	}
	if first {
		stock.NativeConstruction = next.NativeConstruction
	} else {
		stock.NativeConstruction = stock.NativeConstruction && next.NativeConstruction
	}
	values := make(map[policy.Resource]domain.Fact[int64], len(stock.Values))
	for _, v := range stock.Values {
		values[v.Resource] = v.Available
	}
	seen := map[policy.Resource]bool{}
	for _, v := range next.Values {
		if seen[v.Resource] {
			return ErrControl
		}
		seen[v.Resource] = true
		if old, exists := values[v.Resource]; exists {
			if old != v.Available {
				return ErrControl
			}
		} else {
			stock.Values = append(stock.Values, v)
			values[v.Resource] = v.Available
		}
	}
	if len(stock.Values) > 256 {
		return ErrControl
	}
	return nil
}

// shellRepairLimit bounds how many times one shell is repaired under one
// goal epoch; a ring the player keeps cancelling is not fought forever.
const shellRepairLimit = 8

// shellRepairMethod walks a shell's method chain under the goal's current
// epoch: method, then method-repair-1, -2, ... Each bound plan is the
// method in use while it has open work or every cell completed, and hands
// on to the next repair method once it settled with a cell unsuccessful.
// It returns the method the next shell plan should bind, or the plan in use
// when one already covers the shell (including the chain's limit).
func (r *RoutineBuildingPlanner) shellRepairMethod(call context.Context, goal store.GoalState, method domain.MethodID) (domain.MethodID, *store.PlanState, error) {
	journal := r.reviewer.player.journal
	base := method
	for repair := 0; ; repair++ {
		bound, err := journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method)
		if errors.Is(err, store.ErrNotFound) {
			return method, nil, nil
		}
		if err != nil {
			return "", nil, err
		}
		plan, err := journal.LoadPlan(call, bound.Plan)
		if err != nil {
			return "", nil, err
		}
		if domain.GoalWorkOpen(plan.Progress) || !shellSettledWithGap(plan) || repair >= shellRepairLimit {
			return method, &plan, nil
		}
		method = domain.MethodID(fmt.Sprintf("%s-repair-%d", base, repair+1))
	}
}

// shellSettledWithGap reports a settled shell plan that did not complete
// every cell: some wall or door effect was unsuccessful, so the ring has a
// gap only a further plan can close.
func shellSettledWithGap(plan store.PlanState) bool {
	gap := false
	for _, p := range plan.Progress {
		v := p.View()
		effect, known := v.Effect.Value()
		if v.Stage != domain.Completed || !known || effect != domain.EffectCompleted {
			gap = true
		}
	}
	return gap
}
