package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	snap "github.com/davidarcher/RimGovernor/go/internal/snapshot"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// NewRoundsShelterPlanner prefers furnishing verified indoor space. Only when
// that whole method has no space does it propose a native-grounded starter shell.
// The shell is the layout plan's storeroom; a room the plan marks Dug is
// mined by plan dig (#1250).
func NewRoundsShelterPlanner(reviewer *Rounder, native RoundsBuildingSource) (*RoundsBuildingPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoundsShelterPlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoundsBuildingPlanner{reviewer: reviewer, native: native, concern: policy.MaintainHousing, phase: policy.HousingShelter, definition: "Wall", shelter: true}, nil
}

// Expansion reuses the same furnishing and whole-shell admission path to keep
// one spare indoor sleeping place beyond the observed population.
func NewRoundsExpansionPlanner(reviewer *Rounder, native RoundsBuildingSource) (*RoundsBuildingPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoundsExpansionPlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoundsBuildingPlanner{reviewer: reviewer, native: native, concern: policy.MaintainHousing, phase: policy.HousingExpansion, definition: "Wall", shelter: true}, nil
}

// roomModule is the layout module this planner sites a shell as (#609): a
// facility ladder's room role names it, the shelter and expansion
// planners raise the shelter.
func (r *RoundsBuildingPlanner) roomModule() (policy.ModuleRole, bool) {
	return policy.ModuleRoleOf(r.roomRole())
}

// roomRole is the room role a planner sites for (#637): a facility
// ladder's own role, and Shelter for the shelter and expansion planners,
// which raise the colony's temporary starter room (#2043).
func (r *RoundsBuildingPlanner) roomRole() policy.RoomRole {
	if r.facility != nil {
		return r.facility.Role
	}
	return policy.RoomRoleShelter
}

// shellShapesAtDoor lists the planned rooms (shellPlan) whose door would
// stand on door: the shapes adoption matches a standing ring against
// besides the journal's earlier rings.
func shellShapesAtDoor(planned []domain.RoomFootprint, door domain.Cell) []domain.RoomFootprint {
	var shells []domain.RoomFootprint
	for _, shell := range planned {
		if shell.Door() == door {
			shells = append(shells, shell)
		}
	}
	return shells
}

// plannedRole is the room role whose planned rooms this planner builds
// (#1231): the initial shelter stands on the layout plan's shelter room at
// every tier, Camp included (#2043); the storage room is built for supplies
// separately. Any other shell builds its own role's rooms.
func (r *RoundsBuildingPlanner) plannedRole() policy.RoomRole {
	return r.roomRole()
}

// plannedRooms is the layout plan's rooms for this planner's role, in plan
// order, with their footprints (#787). No plan means none: there is no
// free search (#1231).
func (r *RoundsBuildingPlanner) plannedRooms(facts observation.ColonyProjection) ([]policy.LayoutRoom, []domain.RoomFootprint) {
	plan, known := facts.LayoutPlan.Value()
	want, ok := policy.LayoutModule(r.plannedRole())
	if !known || !ok {
		return nil, nil
	}
	roles := []policy.ModuleRole{want}
	var rooms []policy.LayoutRoom
	var shells []domain.RoomFootprint
	for _, role := range roles {
		for _, room := range plan.AllRooms() {
			if room.Role != role {
				continue
			}
			if shell, err := room.Footprint(); err == nil {
				rooms, shells = append(rooms, room), append(shells, shell)
			}
		}
	}
	return rooms, shells
}

// shellPlan is the planned rooms this planner's shell stands on.
func (r *RoundsBuildingPlanner) shellPlan(facts observation.ColonyProjection) []domain.RoomFootprint {
	_, shells := r.plannedRooms(facts)
	return shells
}

// plannedShell is this review's planned room for the shell (#1231): the
// first of the planner's planned rooms that can stand now, its ring cells
// reused, claimed or free and its rock marked for plan dig. free are cells
// offered as unoccupied whatever the census says (the shelter's own bunks,
// #612). ok is false when there is no plan or every
// planned room is blocked; the caller then refuses.
func (r *RoundsBuildingPlanner) plannedShell(call context.Context, facts observation.ColonyProjection, protected, free []domain.Cell, check func() error) (policy.StarterLayout, policy.LayoutRoom, bool, error) {
	rooms, shells := r.plannedRooms(facts)
	if len(shells) == 0 {
		return policy.StarterLayout{}, policy.LayoutRoom{}, false, nil
	}
	cells, err := r.shellRuinHolds(call, facts, shellSiteCells(facts, free), check)
	if err != nil {
		return policy.StarterLayout{}, policy.LayoutRoom{}, false, err
	}
	request := policy.StarterRequest{Bounds: facts.Bounds, Cells: cells, Protected: protected, WallDef: shellStyle(facts).WallDef, Planned: shells}
	snap.NoteShelter(call, request)
	layout, ok, err := policy.PlannedLayout(request)
	if err != nil || !ok {
		return policy.StarterLayout{}, policy.LayoutRoom{}, false, err
	}
	return layout, rooms[layout.Planned], true, nil
}

// prepareShell readies a planned room's ground before its ring (#1231):
// plan dig mines the rock inside it and in its door (#836), then the
// claimable ruins of the ring's kind on its ring are claimed as wall
// (#718). handled is false when there is nothing to prepare, so the ring
// is sited this review.
func (r *RoundsBuildingPlanner) prepareShell(call, epoch context.Context, s excavationStep, layout policy.StarterLayout, room policy.LayoutRoom, check func() error) (RoundsBuildingResult, bool, error) {
	if len(layout.Mined) > 0 {
		plan, _ := s.facts.LayoutPlan.Value()
		result, handled, err := r.digPlannedRoom(call, epoch, s, plan, room, check)
		// A dig still in flight does not hold the ring: walls are built
		// beside the mining, not after it.
		inFlight := handled && (result.Verdict.Is(WaitMethodUsed) || result.Verdict == BuildingReasonExistingWork)
		if err != nil || handled && !inFlight {
			return result, handled, err
		}
	}
	if len(layout.Claimed) > 0 {
		return r.admitShellClaims(call, epoch, s, layout, check)
	}
	return RoundsBuildingResult{}, false, nil
}

// prepareShellRoom sites a non-shelter shell's planned room and prepares
// it (prepareShell); a room that cannot be sited is refused later, by
// previewShell.
func (r *RoundsBuildingPlanner) prepareShellRoom(call, epoch context.Context, s excavationStep, protected []domain.Cell, check func() error) (RoundsBuildingResult, bool, error) {
	layout, room, ok, err := r.plannedShell(call, s.facts, protected, nil, check)
	if err != nil || !ok {
		return RoundsBuildingResult{}, false, err
	}
	return r.prepareShell(call, epoch, s, layout, room, check)
}

// previewPlannedRing previews the planned room's ring (#1231). A room
// still holding rock plan dig could not mine now is refused: a ring
// cannot stand on it.
func (r *RoundsBuildingPlanner) previewPlannedRing(call context.Context, snapshot domain.GenerationSnapshot, facts observation.ColonyProjection, layout policy.StarterLayout, check func() error) ([]policy.Preview, policy.StockObservation, Verdict, error) {
	return r.previewFreshShell(call, snapshot, facts, []policy.StarterLayout{layout}, check)
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

// roundsDefinitionsAvailable reports whether every named definition can be
// raised by any colonist now (no construction skill needed); a shell piece
// also has to be a single cell.
func roundsDefinitionsAvailable(facts observation.ColonyProjection, names []string, shell bool) bool {
	return definitionsGate(facts, names, shell).IsZero()
}

// definitionsGate is the verdict roundsDefinitionsAvailable reads: the zero
// Verdict when every named definition is buildable, else the first thing
// missing, named.
func definitionsGate(facts observation.ColonyProjection, names []string, shell bool) Verdict {
	for _, name := range names {
		def, found := definitionRow(facts, name)
		if !found {
			return fieldUnavailable(name + "_definition")
		}
		if v := definitionAvailability(def); !v.IsZero() {
			return v
		}
		skill, known := def.ConstructionSkill.Value()
		if !known {
			return fieldUnavailable(name + "_construction_skill")
		}
		if skill != 0 {
			return refuse(RefusalNoWorker, "builder_for_"+name, fmt.Sprintf("construction_skill_%d", skill))
		}
		if shell {
			size, known := def.Size.Value()
			if !known {
				return fieldUnavailable(name + "_size")
			}
			if size.Width != 1 || size.Height != 1 {
				return noSpace(name + "_footprint")
			}
		}
	}
	return Verdict{}
}

func definitionRow(facts observation.ColonyProjection, name string) (observation.PlanningDefinition, bool) {
	for _, def := range facts.Definitions {
		if def.Name == name {
			return def, true
		}
	}
	return observation.PlanningDefinition{}, false
}

// definitionAvailability is the zero Verdict for a definition the game
// reports buildable, else the missing fact or the research it waits on.
func definitionAvailability(def observation.PlanningDefinition) Verdict {
	ready, known := def.Available.Value()
	if !known {
		return fieldUnavailable(def.Name + "_availability")
	}
	if ready {
		return Verdict{}
	}
	if len(def.Research) == 0 {
		return awaitingPlan(def.Name, "unavailable")
	}
	return researchWait(strings.Join(def.Research, "+"))
}

// structureReader is the optional native census a shell planner uses to
// recognise a shell it began earlier; sources without it always site afresh.
type structureReader interface {
	ReadStructures(ctx context.Context, identity *c.Identity, minimum, maximum domain.Cell, definitions []string) (bridge.StructureRead, bridge.Result, error)
}

// shellAdoptionReach bounds the census of earlier walls and doors to the
// colony centre's neighbourhood, where the starter search sites shells.
const shellAdoptionReach int32 = 64

// previewShell previews a shell's ring on its planned room (#1231),
// after adopting a ring begun earlier; no plan or a blocked planned room
// is refused as no space.
func (r *RoundsBuildingPlanner) previewShell(ctx context.Context, snapshot domain.GenerationSnapshot, facts observation.ColonyProjection, protected []domain.Cell, check func() error) ([]policy.Preview, policy.StockObservation, Verdict, error) {
	if selected, stock, reason, adopted, err := r.adoptShell(ctx, snapshot, facts, protected, check); err != nil || adopted {
		return selected, stock, reason, err
	}
	layout, _, ok, err := r.plannedShell(ctx, facts, protected, nil, check)
	if err != nil || !ok {
		return nil, policy.StockObservation{}, noSpace("planned_shell_room"), err
	}
	return r.previewPlannedRing(ctx, snapshot, facts, layout, check)
}

// shellSiteCells is the ground a starter shell may stand on: the observed
// cells neither indoors nor roofed, natural rock under any roof, which
// the shell reuses as wall or mines from its interior (#700), and ruins and
// player edifices under any roof, which a ring clears or reuses (#709). Cells in free are offered as
// unoccupied ground whatever the census says of them: the bunks this
// planner placed earlier stand on the interior the ring is raised around
// (#612).
func shellSiteCells(facts observation.ColonyProjection, free []domain.Cell) []policy.SiteCell {
	freed := make(map[domain.Cell]bool, len(free))
	for _, c := range free {
		freed[c] = true
	}
	var cells []policy.SiteCell
	for _, c := range facts.Cells {
		indoors, indoorKnown := c.Indoors.Value()
		roof, roofKnown := c.Roofed.Value()
		edifice, _ := c.PlayerEdifice.Value()
		if !(indoorKnown && !indoors && roofKnown && !roof) && !positiveFact(c.NaturalRock) && !positiveFact(c.Ruin) && edifice == "" {
			continue
		}
		if freed[c.Cell] {
			c.Walkable, c.Occupied, c.Zone = domain.Known(true), domain.Known(false), domain.Known(false)
		}
		cells = append(cells, c)
	}
	return cells
}

// previewFreshShell previews the layouts in order and returns the first
// whose whole ring is placeable now.
func (r *RoundsBuildingPlanner) previewFreshShell(ctx context.Context, snapshot domain.GenerationSnapshot, facts observation.ColonyProjection, layouts []policy.StarterLayout, check func() error) ([]policy.Preview, policy.StockObservation, Verdict, error) {
	style := shellStyle(facts)
	for candidate, layout := range layouts {
		perimeter := layout.Shell.StyledPlacements(style)
		if len(perimeter) == 0 {
			return nil, policy.StockObservation{}, Verdict{}, fmt.Errorf("%w: previewFreshShell: len(perimeter) == 0", ErrControl)
		}
		// A door still in rock waits for the dig; the rest of the ring goes
		// up now, and the door follows once its cell is open (standing walls
		// are Reused by the next siting).
		rock := make(map[domain.Cell]bool, len(facts.Cells))
		for _, c := range facts.Cells {
			rock[c.Cell] = positiveFact(c.NaturalRock)
		}
		waiting := append(append([]domain.Cell(nil), layout.Reused...), layout.Claimed...)
		for _, c := range layout.Mined {
			if !rock[c] {
				// Fogged or unobserved: native has not said it is rock, so the
				// ring is not raised around it.
				return nil, policy.StockObservation{}, noSpace("planned_room_rock"), nil
			}
			waiting = append(waiting, c)
		}
		perimeter = unreused(perimeter, waiting)
		if len(perimeter) == 0 {
			return nil, policy.StockObservation{}, noSpace("planned_room_rock"), nil
		}
		ids := make([]domain.ActionID, len(perimeter))
		for i := range perimeter {
			ids[i] = domain.ActionID(fmt.Sprintf("%s-%d-%d", snapshot.Plan, candidate, i))
		}
		previews, placeable, reason, err := r.previewShellCells(ctx, snapshot, facts, ids, perimeter, check)
		if err != nil || !reason.IsZero() {
			return nil, policy.StockObservation{}, reason, err
		}
		stock := policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}
		var selected []policy.Preview
		for i, preview := range previews {
			if !placeable[i] {
				break
			}
			if err := mergeRoundsStock(&stock, preview.Stock, len(selected) == 0); err != nil {
				return nil, policy.StockObservation{}, Verdict{}, err
			}
			selected = append(selected, preview.Preview)
		}
		if len(selected) == len(perimeter) {
			return selected, stock, Verdict{}, nil
		}
	}
	return nil, policy.StockObservation{}, noSpace("shell_perimeter"), nil
}

// unreused drops the placements on ring cells natural rock, a player wall
// or a claimable ruin wall already walls (#700, #709, #718). A ruin home
// clearance deconstructs before its claim leaves a gap adoption closes.
func unreused(perimeter []domain.Building, reused []domain.Cell) []domain.Building {
	if len(reused) == 0 {
		return perimeter
	}
	rock := make(map[domain.Cell]bool, len(reused))
	for _, c := range reused {
		rock[c] = true
	}
	kept := make([]domain.Building, 0, len(perimeter))
	for _, b := range perimeter {
		if !rock[b.Cell()] {
			kept = append(kept, b)
		}
	}
	return kept
}

func positiveFact(f domain.Fact[bool]) bool {
	v, known := f.Value()
	return known && v
}

// shellBatchPreviewer is the batched preview a native source may offer: one
// call for every cell of a ring instead of one hop per cell (#599).
type shellBatchPreviewer interface {
	PreviewBuildings(context.Context, []domain.Action, domain.GenerationSnapshot) ([]bridge.BuildingPreview, bridge.Result, error)
}

// previewShellCells previews every shell placement natively, in one batched
// call when the source offers it, and reports per cell whether it is
// placeable now. A stale or mismatched preview is ErrControl and an
// unreadable material scan is the unknown-prerequisite reason; either ends
// the sweep, since the ring is only ever admitted whole.
func (r *RoundsBuildingPlanner) previewShellCells(ctx context.Context, snapshot domain.GenerationSnapshot, facts observation.ColonyProjection, ids []domain.ActionID, buildings []domain.Building, check func() error) ([]bridge.BuildingPreview, []bool, Verdict, error) {
	if len(ids) != len(buildings) || len(buildings) == 0 {
		return nil, nil, Verdict{}, fmt.Errorf("%w: previewShellCells: len(ids) != len(buildings) || len(buildings) == 0", ErrControl)
	}
	if err := check(); err != nil {
		return nil, nil, Verdict{}, err
	}
	actions := make([]domain.Action, len(buildings))
	for i, building := range buildings {
		action, err := domain.NewBuildingAction(ids[i], building)
		if err != nil {
			return nil, nil, Verdict{}, err
		}
		actions[i] = action
	}
	var previews []bridge.BuildingPreview
	if batch, ok := r.native.(shellBatchPreviewer); ok {
		var err error
		if previews, _, err = batch.PreviewBuildings(ctx, actions, snapshot); err != nil {
			return nil, nil, Verdict{}, err
		}
	} else {
		for _, action := range actions {
			preview, _, err := r.native.PreviewBuilding(ctx, action, snapshot)
			if err != nil {
				return nil, nil, Verdict{}, err
			}
			previews = append(previews, preview)
		}
	}
	if len(previews) != len(actions) {
		return nil, nil, Verdict{}, fmt.Errorf("%w: previewShellCells: len(previews) != len(actions)", ErrControl)
	}
	placeable := make([]bool, len(previews))
	for i, preview := range previews {
		v := preview.Preview
		stuff, known := v.MadeFromStuff.Value()
		if !known || !stuff {
			return nil, nil, fieldUnavailable("shell_preview"), nil
		}
		footprint, known := v.Footprint.Value()
		can, canKnown := v.CanPlace.Value()
		safe, safeKnown := v.SafeToPlace.Value()
		placeable[i] = known && len(footprint) == 1 && footprint[0] == buildings[i].Cell() && canKnown && can && safeKnown && safe
	}
	return previews, placeable, Verdict{}, nil
}

// shellDefinitions are the definitions a shell ring is made of and the
// adoption census reads back: walls and either door the door ladder
// proposes (#610).
var shellDefinitions = []string{"Wall", "Door", "Autodoor"}

// shellDoor reports a door definition of the ladder.
func shellDoor(definition string) bool { return definition == "Door" || definition == "Autodoor" }

// shellStands reports that the census holds the shape's building on its
// cell: the same definition, or any door of the ladder where the shape
// wants a door, so a ring begun with wood doors is still one ring once the
// tier proposes autodoors.
func shellStands(standing map[domain.Cell]string, building domain.Building) bool {
	def, ok := standing[building.Cell()]
	if !ok {
		return false
	}
	return def == building.Definition() || shellDoor(def) && shellDoor(building.Definition())
}

// shellMethodPatterns match every whole-shell method a shelter-style
// planner admits (selection's "*-shell" methods and their "-repair-N"
// successors: initial shelter, expansion, workshop and hospital shells
// alike); adoptShell reads their plans back as the durable record of the
// rings it ordered.
var shellMethodPatterns = []string{"comfort-shell*", "workshop-shell*", "hospital-shell*", "laboratory-shell*", "sleeping-shell*", "shelter-shell*", "expansion-shelter-shell*"}

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
// shell with no template is recognised -- and then the planned rooms of the
// layout plan with their door there (shellShapesAtDoor), which are
// all that remains when the journal did not survive the restart. A door is
// a candidate when it stands natively or when an earlier plan ordered it: a
// ring whose door was cancelled but whose walls stand is still one ring, and
// reissuing it orders the door first in the wave as a fresh shell would.
// Shapes at one door share their lowest courses, so the shape
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
func (r *RoundsBuildingPlanner) adoptShell(ctx context.Context, snapshot domain.GenerationSnapshot, facts observation.ColonyProjection, protected []domain.Cell, check func() error) ([]policy.Preview, policy.StockObservation, Verdict, bool, error) {
	reader, ok := r.native.(structureReader)
	if !ok {
		return nil, policy.StockObservation{}, Verdict{}, false, nil
	}
	center, planned := facts.Center().Value()
	if !planned {
		return nil, policy.StockObservation{}, Verdict{}, false, nil
	}
	minimum := domain.Cell{X: max(0, center.X-shellAdoptionReach), Z: max(0, center.Z-shellAdoptionReach)}
	maximum := domain.Cell{X: min(facts.Bounds.Width-1, center.X+shellAdoptionReach), Z: min(facts.Bounds.Height-1, center.Z+shellAdoptionReach)}
	if minimum.X > maximum.X || minimum.Z > maximum.Z {
		return nil, policy.StockObservation{}, Verdict{}, false, nil
	}
	census, _, err := reader.ReadStructures(ctx, boundary.Identity(snapshot), minimum, maximum, shellDefinitions)
	if err != nil {
		return nil, policy.StockObservation{}, Verdict{}, false, err
	}
	if err := check(); err != nil {
		return nil, policy.StockObservation{}, Verdict{}, false, err
	}
	if !roundsCachedFresh(bridge.FactColony, census.Tick, facts.Identity.Tick) || census.Generation != uint64(snapshot.Native) {
		return nil, policy.StockObservation{}, Verdict{}, false, fmt.Errorf("%w: adoptShell: !roundsCachedFresh(bridge.FactColony, census.Tick, facts.Identity.Tick) || census.Generation != uint64(sna", ErrControl)
	}
	standing := make(map[domain.Cell]string, len(census.Structures))
	seen := map[domain.Cell]bool{}
	var doors []domain.Cell
	for _, s := range census.Structures {
		standing[s.Cell] = s.Definition
		if shellDoor(s.Definition) && !seen[s.Cell] {
			seen[s.Cell] = true
			doors = append(doors, s.Cell)
		}
	}
	earlier, err := r.earlierShells(ctx, minimum, maximum)
	if err != nil {
		return nil, policy.StockObservation{}, Verdict{}, false, err
	}
	for door := range earlier {
		if !seen[door] {
			seen[door] = true
			doors = append(doors, door)
		}
	}
	if len(doors) == 0 {
		return nil, policy.StockObservation{}, Verdict{}, false, nil
	}
	sort.Slice(doors, func(i, j int) bool {
		a, b := squaredDistance(doors[i], center), squaredDistance(doors[j], center)
		if a != b {
			return a < b
		}
		return doors[i].Z < doors[j].Z || doors[i].Z == doors[j].Z && doors[i].X < doors[j].X
	})
	guarded := make(map[domain.Cell]bool, len(protected))
	for _, c := range protected {
		guarded[c] = true
	}
	expanded := shellStyle(facts)
	for d, door := range doors {
		shapes := earlier[door]
		for _, shell := range shellShapesAtDoor(r.shellPlan(facts), door) {
			shapes = append(shapes, shell.StyledPlacements(expanded))
		}
		best, bestMatched := -1, 0
		for shape, perimeter := range shapes {
			matched := 0
			for _, building := range perimeter {
				if shellStands(standing, building) {
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
		var ids []domain.ActionID
		var missing []domain.Building
		for i, building := range shapes[best] {
			cell := building.Cell()
			if shellStands(standing, building) {
				continue
			}
			if _, other := standing[cell]; other || guarded[cell] {
				return nil, policy.StockObservation{}, BuildingShellBlocked, true, nil
			}
			ids = append(ids, domain.ActionID(fmt.Sprintf("%s-adopt-%d-%d-%d", snapshot.Plan, d, best, i)))
			missing = append(missing, building)
		}
		if len(missing) == 0 {
			// The shell stands whole; nothing to adopt and nothing to site
			// while it waits on its roof.
			return nil, policy.StockObservation{}, BuildingShellBlocked, true, nil
		}
		previews, placeable, reason, err := r.previewShellCells(ctx, snapshot, facts, ids, missing, check)
		if err != nil {
			return nil, policy.StockObservation{}, Verdict{}, false, err
		}
		if !reason.IsZero() {
			return nil, policy.StockObservation{}, reason, true, nil
		}
		stock := policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}
		var selected []policy.Preview
		for i, preview := range previews {
			if !placeable[i] {
				return nil, policy.StockObservation{}, BuildingShellBlocked, true, nil
			}
			if err := mergeRoundsStock(&stock, preview.Stock, len(selected) == 0); err != nil {
				return nil, policy.StockObservation{}, Verdict{}, false, err
			}
			selected = append(selected, preview.Preview)
		}
		return selected, stock, Verdict{}, true, nil
	}
	return nil, policy.StockObservation{}, Verdict{}, false, nil
}

// facilityLadder reports whether the planner walks a facility ladder whose
// last rung stages a room for the furniture (comfort, workshop, hospital,
// sleeping, laboratory), as opposed to the initial shelter and expansion,
// whose shell is the deficit itself.
func (r *RoundsBuildingPlanner) facilityLadder() bool {
	switch r.concern {
	case policy.MaintainResource, policy.MaintainEquipment, policy.MaintainMedicalReserves, policy.EnsureResearch:
		return true
	}
	return r.phase == policy.HousingSleeping || r.phase == policy.ComfortRanked
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
func (r *RoundsBuildingPlanner) earlierShells(ctx context.Context, minimum, maximum domain.Cell) (map[domain.Cell][][]domain.Building, error) {
	history, err := r.reviewer.player.journal.PlanHistoryWithMethods(ctx, shellHistoryLimit, shellMethodPatterns...)
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
			if shellDoor(b.Definition()) {
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

// mergeRoundsStock folds one more preview's stock into a bundle's. Previews
// of one bundle are native calls made one after another, and an Available
// count can differ between them (it is net of the native blueprint census
// and moves as colonists haul); the bundle is funded from the lowest value
// any preview saw, and an unknown one leaves the resource unknown.
func mergeRoundsStock(stock *policy.StockObservation, next policy.StockObservation, first bool) error {
	if first {
		stock.NativeConstruction = next.NativeConstruction
	} else {
		stock.NativeConstruction = stock.NativeConstruction && next.NativeConstruction
	}
	index := make(map[policy.Resource]int, len(stock.Values))
	for i, v := range stock.Values {
		index[v.Resource] = i
	}
	seen := map[policy.Resource]bool{}
	for _, v := range next.Values {
		if seen[v.Resource] {
			return fmt.Errorf("%w: mergeRoundsStock: seen[v.Resource]", ErrControl)
		}
		seen[v.Resource] = true
		if i, exists := index[v.Resource]; exists {
			stock.Values[i].Available = lowerStock(stock.Values[i].Available, v.Available)
		} else {
			stock.Values = append(stock.Values, v)
			index[v.Resource] = len(stock.Values) - 1
		}
	}
	return nil
}

func lowerStock(a, b domain.Fact[int64]) domain.Fact[int64] {
	x, xk := a.Value()
	y, yk := b.Value()
	if !xk || !yk {
		return domain.Unknown[int64]()
	}
	return domain.Known(min(x, y))
}

// shellRepairLimit bounds how many times one shell is repaired under one
// Episode; a ring the player keeps cancelling is not fought forever.
const shellRepairLimit = 8

// shellRepairMethod walks a shell's method chain under the goal's current
// epoch: method, then method-repair-1, -2, ... Each bound plan is the
// method in use while it has open work or every cell completed, and hands
// on to the next repair method once it settled with a cell unsuccessful.
// It returns the method the next shell plan should bind, or the plan in use
// when one already covers the shell (including the chain's limit).
func (r *RoundsBuildingPlanner) shellRepairMethod(call context.Context, goal store.WorkOwner, method domain.MethodID) (domain.MethodID, *store.PlanState, error) {
	journal := r.reviewer.player.journal
	base := method
	for repair := 0; ; repair++ {
		bound, err := journal.LoadOwnerMethod(call, goal, method)
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
		if store.PlanOpen(plan) || !shellSettledWithGap(plan) || repair >= shellRepairLimit {
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
