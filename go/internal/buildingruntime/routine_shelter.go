package buildingruntime

import (
	"context"
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
func NewRoutineShelterPlanner(reviewer *RoutineReviewer, native RoutineBuildingSource) (*RoutineBuildingPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	return &RoutineBuildingPlanner{reviewer: reviewer, native: native, goal: policy.EnsureInitialShelter, definition: "Wall", shelter: true}, nil
}

// Expansion reuses the same furnishing and whole-shell admission path to keep
// one spare indoor sleeping place beyond the observed population.
func NewRoutineExpansionPlanner(reviewer *RoutineReviewer, native RoutineBuildingSource) (*RoutineBuildingPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	return &RoutineBuildingPlanner{reviewer: reviewer, native: native, goal: policy.EnsureExpansion, definition: "Wall", shelter: true}, nil
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
// size by shape, so any nonempty fully completed plan qualifies.
func shelterNativeWorkTicks(plan store.PlanState, current domain.GenerationSnapshot, tick domain.Tick) uint32 {
	if len(plan.Progress) == 0 {
		return 0
	}
	current.Plan, current.Revision = plan.Spec.ID(), plan.Spec.Revision()
	completed := domain.Tick(0)
	for _, p := range plan.Progress {
		v := p.View()
		effect, known := v.Effect.Value()
		if v.Stage != domain.Completed || v.Unresolved || !known || effect != domain.EffectCompleted || !v.Snapshot.Matches(current) || v.Tick > tick {
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
	if selected, stock, adopted, err := r.adoptShell(ctx, snapshot, facts, protected, style, check); err != nil || adopted {
		return selected, stock, "", err
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
	if v.Action != action || !v.Snapshot.Matches(snapshot) || v.Tick != facts.Identity.Tick || !preview.Stock.Snapshot.Matches(snapshot) || preview.Stock.Tick != facts.Identity.Tick {
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

// adoptShell recognises a shell this controller began earlier in this world
// and reissues only the cells it is missing. Resuming control invalidates
// routine goals and cancels their plans, so after a restart the walls and
// door already standing, framed or blueprinted natively are the only durable
// record of the shell; a cancelled frame likewise leaves a gap in an
// otherwise ordered ring. Without this the next review would site a second
// shell beside the first. A shell is recognised from a player door plus at
// least one wall on one of the shapes the starter search issues at that door
// (policy.ShellShapesAtDoor); every other cell of that shape must be placeable
// now. Doors are tried nearest the colony centre first.
func (r *RoutineBuildingPlanner) adoptShell(ctx context.Context, snapshot domain.GenerationSnapshot, facts observation.ColonyProjection, protected []domain.Cell, style policy.ShelterStyle, check func() error) ([]policy.Preview, policy.StockObservation, bool, error) {
	reader, ok := r.native.(structureReader)
	if !ok {
		return nil, policy.StockObservation{}, false, nil
	}
	minimum := domain.Cell{X: max(0, facts.Center.X-shellAdoptionReach), Z: max(0, facts.Center.Z-shellAdoptionReach)}
	maximum := domain.Cell{X: min(facts.Bounds.Width-1, facts.Center.X+shellAdoptionReach), Z: min(facts.Bounds.Height-1, facts.Center.Z+shellAdoptionReach)}
	if minimum.X > maximum.X || minimum.Z > maximum.Z {
		return nil, policy.StockObservation{}, false, nil
	}
	census, _, err := reader.ReadStructures(ctx, boundary.Identity(snapshot), minimum, maximum, []string{"Wall", "Door"})
	if err != nil {
		return nil, policy.StockObservation{}, false, err
	}
	if err := check(); err != nil {
		return nil, policy.StockObservation{}, false, err
	}
	if census.Tick != facts.Identity.Tick || census.Generation != uint64(snapshot.Native) {
		return nil, policy.StockObservation{}, false, ErrControl
	}
	standing := make(map[domain.Cell]string, len(census.Structures))
	var doors []domain.Cell
	for _, s := range census.Structures {
		standing[s.Cell] = s.Definition
		if s.Definition == "Door" {
			doors = append(doors, s.Cell)
		}
	}
	if len(doors) == 0 {
		return nil, policy.StockObservation{}, false, nil
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
		for shape, shell := range policy.ShellShapesAtDoor(door, style) {
			perimeter := shell.Placements("Wall", "Door", "WoodLog")
			stock := policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}
			var selected []policy.Preview
			matched, fits := 0, true
			for i, building := range perimeter {
				cell := building.Cell()
				if standing[cell] == building.Definition() {
					matched++
					continue
				}
				if _, other := standing[cell]; other || guarded[cell] {
					fits = false
					break
				}
				preview, placeable, reason, err := r.previewShellCell(ctx, snapshot, facts, domain.ActionID(fmt.Sprintf("%s-adopt-%d-%d-%d", snapshot.Plan, d, shape, i)), building, check)
				if err != nil {
					return nil, policy.StockObservation{}, false, err
				}
				if reason != "" || !placeable {
					fits = false
					break
				}
				if err := mergeRoutineStock(&stock, preview.Stock, len(selected) == 0); err != nil {
					return nil, policy.StockObservation{}, false, err
				}
				selected = append(selected, preview.Preview)
			}
			if fits && matched >= 2 && len(selected) > 0 {
				return selected, stock, true, nil
			}
		}
	}
	return nil, policy.StockObservation{}, false, nil
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
