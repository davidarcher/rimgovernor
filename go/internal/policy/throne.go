package policy

import (
	"fmt"
	"slices"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Staging the throne room (#1601, epic #1598). A colonist who holds an
// Empire title, or has the favor to claim the next one, is owed the throne
// room of the next title that asks for one (RoyalRung.Throne*): the plan
// grows a ThroneRoomSizes room (GrowThroneRoom, layout_throne.go), the
// sleeping planner raises its shell, places one of the title's throne
// definitions at the template slot, and furnishes the room to the title's
// minimum impressiveness through the room quality levers (ThroneRoomTargets).
// The throne's footprint is the native catalog's, never a constant here.
//
// Once the throne stands and the holder holds the title, NextThroneStep
// reports ThroneAssign until the royalty read lists the holder as the
// throne's owner (RoyalThrone); the runtime sends it as the generic Assign
// intent, and the step holds MaintainHousing open until the read shows it
// done.

// ThroneNeed is the throne room a colonist is owed: the first title above
// the holder's current one (the current one at the top) whose requirement
// names a throne and a minimum area.
type ThroneNeed struct {
	// Holder is the colonist the room is for and Title the rung it is
	// sized to.
	Holder PawnID
	Title  string
	// ThroneRequirements are Title's requirements, read from the def
	// mirror: the area, impressiveness, throne definitions, assignment,
	// flooring, furnishings and forbidden buildings.
	ThroneRequirements
	// Titled is whether the holder holds a title already: native lets
	// only a titled colonist own a throne, so assignment waits for it.
	Titled bool
}

// ThroneRequirements is one title's throneRoomRequirements as the def mirror
// holds them (RoyalTitleDef, RoomRequirement_* messages), one field per
// requirement kind. Names are the game's own, never a Go list: nothing
// here is a def-name constant.
type ThroneRequirements struct {
	// Things are the throne definitions the title accepts and Assigned
	// whether one must be assigned to the holder (HasAssignedThroneAnyOf);
	// a title that asks for no throne has none.
	Things   []string
	Assigned bool
	// MinArea and MinImpressiveness are the room minimums (Area,
	// Impressiveness); the impressiveness is zero when the title sets none.
	MinArea, MinImpressiveness int
	// FloorTags are the terrain tags whose floors satisfy the flooring
	// requirement (TerrainWithTags: every cell's terrain carries one),
	// empty when the title does not ask for floors; FloorLabel is its
	// labelKey ("RoomRequirementAllFloored", "RoomRequirementAllFineFloored").
	FloorTags  []string
	FloorLabel string
	// AnyOfCounts are ThingAnyOfCount requirements (two braziers), Counts
	// ThingCount ones (the columns), AnyOf ThingAnyOf ones (one instrument)
	// and Glowing the def sets every standing building of which must be lit
	// (AllThingsAnyOfAreGlowing, AllThingsAreGlowing).
	AnyOfCounts []ThingAnyOfCount
	Counts      []ThingCount
	AnyOf       [][]string
	Glowing     [][]string
	// ForbiddenBuildingTags are the building tags no building in the room
	// may carry (ForbiddenBuildings: Production, Bed, Biotech, Anomaly) and
	// ForbidAltars whether an ideology altar is forbidden too.
	ForbiddenBuildingTags []string
	ForbidAltars          bool
	// ForbiddenDefs are the building definitions those tags (and altars,
	// when forbidden) name in the def catalog (ThingDef.building.buildingTags,
	// ThingDef.isAltar), sorted: the set the room may hold none of (#1865).
	// Resolved with the ladder (bridge WithThroneRequirements), not read from
	// the title row.
	ForbiddenDefs []string
}

// Forbids reports whether def is a building the throne room may not hold.
func (r ThroneRequirements) Forbids(def string) bool {
	return slices.Contains(r.ForbiddenDefs, def)
}

// ThingAnyOfCount asks for Count buildings of any of Things.
type ThingAnyOfCount struct {
	Things []string
	Count  int
}

// ThingCount asks for Count buildings of Def.
type ThingCount struct {
	Def   string
	Count int
}

// rungRequirement is rung's throne requirement, false for a title that asks
// for no throne or whose requirement is not read.
func rungRequirement(rung RoyalRung) (ThroneNeed, bool) {
	req, ok := rung.Throne.Value()
	if !ok || len(req.Things) == 0 || req.MinArea <= 0 {
		return ThroneNeed{}, false
	}
	req.MinImpressiveness = max(req.MinImpressiveness, 0)
	return ThroneNeed{Title: rung.Title, ThroneRequirements: req}, true
}

// NextThroneNeed is the largest throne room any colonist is owed, false
// when none holds or can claim a title that asks for one. A colonist holds
// a title or can claim the next rung when its favor reaches the rung's
// FavorNeeded; the room sized is the first rung above its current title
// with a throne requirement, else the current title's own.
func NextThroneNeed(f RoyaltyFacts) (ThroneNeed, bool) {
	index := map[string]int{}
	for i, rung := range f.Ladder {
		index[rung.Title] = i
	}
	holders := make([]PawnID, 0, len(f.Holders))
	for id := range f.Holders {
		holders = append(holders, id)
	}
	sort.Slice(holders, func(i, j int) bool { return holders[i] < holders[j] })
	var best ThroneNeed
	found := false
	for _, id := range holders {
		for _, h := range f.Holders[id] {
			current := -1
			if h.Title != "" {
				i, ok := index[h.Title]
				if !ok {
					continue
				}
				current = i
			} else {
				// No title yet: owed only once the first rung is within
				// reach of the favor held.
				favor, fk := h.Favor.Value()
				if len(f.Ladder) == 0 {
					continue
				}
				needed, nk := f.Ladder[0].FavorNeeded.Value()
				if !fk || !nk || favor < needed {
					continue
				}
			}
			need, ok := ThroneNeed{}, false
			for i := current + 1; i < len(f.Ladder) && !ok; i++ {
				need, ok = rungRequirement(f.Ladder[i])
			}
			if !ok && current >= 0 {
				need, ok = rungRequirement(f.Ladder[current])
			}
			if !ok {
				continue
			}
			need.Holder = id
			need.Titled = holdsTitle(f, id)
			if !found || need.MinArea > best.MinArea || need.MinArea == best.MinArea && need.MinImpressiveness > best.MinImpressiveness {
				best, found = need, true
			}
		}
	}
	return best, found
}

// FurnitureDefinition is a throne definition as the native catalog describes
// it.
type FurnitureDefinition struct {
	Name      string
	Available domain.Fact[bool]
	Size      domain.Fact[Bounds]
	// Roles are the room-role furniture roles the catalog's def rows give the
	// definition (the PlanningDefinition view's RoomRoles).
	Roles []string
}

// ThroneStepKind is the next throne step.
type ThroneStepKind string

const (
	// ThroneNone: nothing is due, or a fact is unknown.
	ThroneNone ThroneStepKind = ""
	// ThroneReconcile: the room differs from the plan and the title's
	// template (Template: the throne and the required furniture): the build
	// side reconciles it (ReconcileRoom). The room's state is whatever the
	// diff leaves; there is no shell, place or blocked step.
	ThroneReconcile ThroneStepKind = "reconcile"
	// ThroneAssign: the throne stands unowned; assign Throne to
	// Need.Holder, replacing PreviousThrone (empty: none).
	ThroneAssign ThroneStepKind = "assign"
	// ThroneRefuel: the room's unlit light Lamp is out of fuel; order Pawn
	// to refuel it.
	ThroneRefuel ThroneStepKind = "refuel"
	// ThroneUnavailable: a requirement still unmet has no definition the
	// catalog makes available with a known size (Missing). The step names the
	// failure (Detail) and is not Owed (#1874).
	ThroneUnavailable ThroneStepKind = "unavailable"
)

// ThroneStep is one bounded step towards the title's throne room.
type ThroneStep struct {
	Kind ThroneStepKind
	Room PlannedRoom
	Need ThroneNeed
	// Template is a ThroneReconcile's wanted furniture: the throne and each
	// required piece, standing ones where they stand, missing ones in their
	// template slot.
	Template []WantedPiece
	// Throne is the standing throne to assign and PreviousThrone the
	// throne the holder owns already, empty when none.
	Throne, PreviousThrone string
	// Lamp and Pawn are a ThroneRefuel's light and hauler.
	Lamp string
	Pawn PawnID
	// Missing are the unmet requirements no available definition serves, each
	// the any-of list of definition names, for ThroneUnavailable.
	Missing []string
}

// Failed reports whether the step is a named failure planning cannot clear.
func (s ThroneStep) Failed() bool { return s.Kind == ThroneUnavailable }

// Detail names a failed step: the title, the room and each requirement without
// an available definition.
func (s ThroneStep) Detail() string {
	return fmt.Sprintf("the %s throne room at (%d,%d) needs definitions the catalog does not make available with a known size: %s", s.Need.Title, s.Room.Interior.X, s.Room.Interior.Z, strings.Join(s.Missing, "; "))
}

// throneIntruders are the standing buildings of a class need forbids with a
// cell inside room's interior, in census order.
func throneIntruders(room PlannedRoom, need ThroneNeed, built []CurrentBuilding) []CurrentBuilding {
	if len(need.ForbiddenDefs) == 0 {
		return nil
	}
	inside := map[domain.Cell]bool{}
	for _, c := range rectCells(room.Interior) {
		inside[c] = true
	}
	var out []CurrentBuilding
	for _, b := range built {
		if !need.Forbids(b.Building.Definition()) {
			continue
		}
		if slices.ContainsFunc(b.Cells, func(c domain.Cell) bool { return inside[c] }) {
			out = append(out, b)
		}
	}
	return out
}

// Owed reports whether the planner can act on the step now.
func (s ThroneStep) Owed() bool {
	return s.Kind == ThroneReconcile || s.Kind == ThroneAssign || s.Kind == ThroneRefuel
}

// throneDefinition is the first of need's throne definitions the catalog
// makes available with a known footprint.
func throneDefinition(need ThroneNeed, defs []FurnitureDefinition) (InteriorPieceDef, bool) {
	return availableDefinition(need.Things, defs)
}

// throneWant is one counted piece requirement of the title: Count buildings
// of any of Things, planned in slots named Key.0, Key.1 and so on.
type throneWant struct {
	Key    string
	Things []string
	Count  int
}

// wants are the title's counted piece requirements (the braziers, the
// columns, the instrument), in the mirror's order, besides the throne.
func (n ThroneNeed) wants() []throneWant {
	var out []throneWant
	for i, r := range n.AnyOfCounts {
		out = append(out, throneWant{fmt.Sprintf("anyofcount%d", i), r.Things, r.Count})
	}
	for i, r := range n.Counts {
		out = append(out, throneWant{fmt.Sprintf("count%d", i), []string{r.Def}, r.Count})
	}
	for i, set := range n.AnyOf {
		out = append(out, throneWant{fmt.Sprintf("anyof%d", i), set, 1})
	}
	return out
}

// DefNames are every definition the title's piece requirements name, the
// throne's included: the definitions whose catalog rows the step needs.
func (n ThroneNeed) DefNames() []string {
	names := append([]string(nil), n.Things...)
	for _, w := range n.wants() {
		names = append(names, w.Things...)
	}
	return names
}

// availableDefinition is the first of things the catalog makes available
// with a known footprint.
func availableDefinition(things []string, defs []FurnitureDefinition) (InteriorPieceDef, bool) {
	for _, thing := range things {
		for _, d := range defs {
			if d.Name != thing {
				continue
			}
			available, ak := d.Available.Value()
			size, sk := d.Size.Value()
			if ak && available && sk && size.Width > 0 && size.Height > 0 {
				return InteriorPieceDef{Def: d.Name, Size: domain.Cell{X: size.Width, Z: size.Height}}, true
			}
		}
	}
	return InteriorPieceDef{}, false
}

// standingThroneIn is the standing throne of need inside r's interior.
func standingThroneIn(r PlannedRoom, need ThroneNeed, built []CurrentBuilding) (CurrentBuilding, bool) {
	for _, b := range built {
		if len(b.Cells) == 0 || !rectInside(r.Interior, cellsRectangle(b.Cells)) {
			continue
		}
		for _, thing := range need.Things {
			if b.Building.Definition() == thing {
				return b, true
			}
		}
	}
	return CurrentBuilding{}, false
}

// holdsTitle is whether id holds an Empire title now.
func holdsTitle(f RoyaltyFacts, id PawnID) bool {
	for _, h := range f.Holders[id] {
		if h.Title != "" {
			return true
		}
	}
	return false
}

// throneAssignment is the assignment still due for the standing throne: it
// must be listed by the royalty read (a throne built after the read waits for
// the next one) and unowned, and the holder must hold the title. A throne
// another colonist owns is left alone.
func throneAssignment(step ThroneStep, throne CurrentBuilding, thrones []RoyalThrone) ThroneStep {
	if !step.Need.Assigned || !step.Need.Titled {
		return ThroneStep{}
	}
	listed := false
	for _, t := range thrones {
		if t.ID == throne.ID {
			if t.Owner != "" {
				return ThroneStep{}
			}
			listed = true
		} else if t.Owner == step.Need.Holder {
			step.PreviousThrone = t.ID
		}
	}
	if !listed {
		return ThroneStep{}
	}
	step.Kind, step.Throne = ThroneAssign, throne.ID
	return step
}

// NextThroneStep picks the next throne step for need from the plan, the room
// census, the colony's walls and doors, buildings and the throne definitions.
// None while the plan holds no room of the title's area (the layout review owes
// it), and once the room matches, the throne stands and is assigned (or cannot
// be yet). An unmet requirement with no available definition is
// ThroneUnavailable. Otherwise the room is reconciled whenever its ring, doors
// or furniture differ from the plan and the template, or it holds a forbidden
// building (packed, not blocked); the throne's assignment comes first.
func NextThroneStep(plan LayoutPlan, rooms RoomObservation, ground GroundCensus, built []CurrentBuilding, need ThroneNeed, defs []FurnitureDefinition, thrones []RoyalThrone) ThroneStep {
	room, ok := plan.ThroneRoomFor(need.MinArea)
	if !ok {
		return ThroneStep{}
	}
	// A forbidden class is never planned: its definitions are not offered.
	defs = slices.DeleteFunc(slices.Clone(defs), func(d FurnitureDefinition) bool { return need.Forbids(d.Name) })
	step := ThroneStep{Room: room, Need: need}
	standing, stands := standingThroneIn(room, need, built)
	if stands {
		if step := throneAssignment(step, standing, thrones); step.Kind != ThroneNone {
			return step
		}
	}
	def, dok := throneDefinition(need, defs)
	// An unmet requirement no available definition serves is a named
	// failure, not a silent skip (#1874).
	var missing []string
	if !stands && !dok && len(need.Things) > 0 {
		missing = append(missing, strings.Join(need.Things, " or "))
	}
	for _, w := range need.wants() {
		if _, ok := availableDefinition(w.Things, defs); !ok && standingCount(room, w.Things, built) < w.Count {
			missing = append(missing, strings.Join(w.Things, " or "))
		}
	}
	if len(missing) > 0 {
		step.Kind, step.Missing = ThroneUnavailable, missing
		return step
	}
	if !stands && !dok {
		return ThroneStep{}
	}
	in, rok := InteriorRoomFromLayout(room, rooms.Shapes)
	if !rok {
		return ThroneStep{}
	}
	// The template plans every required piece in its slot whether or not
	// it stands; a piece that stands is wanted where it stands, each
	// missing one from the first available definition of its any-of list (a
	// requirement no available definition serves plans nothing).
	for _, w := range need.wants() {
		d, ok := availableDefinition(w.Things, defs)
		if !ok {
			continue
		}
		for i := 0; i < w.Count; i++ {
			in.Required = append(in.Required, RequiredPiece{Slot: fmt.Sprintf("%s.%d", w.Key, i), Piece: d})
		}
	}
	interior, ok := PlanInterior(in, def)
	if !ok {
		return ThroneStep{}
	}
	taken := map[domain.Cell]bool{}
	for _, b := range built {
		for _, c := range b.Cells {
			taken[c] = true
		}
	}
	free := func(p InteriorPiece) bool {
		for _, c := range rectCells(p.Rect) {
			if taken[c] {
				return false
			}
		}
		return true
	}
	planned := func(p InteriorPiece) WantedPiece {
		return WantedPiece{DefName: p.Def, Minimum: domain.Cell{X: p.Rect.X, Z: p.Rect.Z}, Maximum: domain.Cell{X: p.Rect.X + p.Rect.Width - 1, Z: p.Rect.Z + p.Rect.Height - 1}, Slot: p.Slot, Size: p.Size, Rot: p.Rot}
	}
	standingPiece := func(b CurrentBuilding) WantedPiece {
		r := cellsRectangle(b.Cells)
		return WantedPiece{DefName: b.Building.Definition(), Minimum: domain.Cell{X: r.X, Z: r.Z}, Maximum: domain.Cell{X: r.X + r.Width - 1, Z: r.Z + r.Height - 1}}
	}
	var template []WantedPiece
	absent := false
	if stands {
		template = append(template, standingPiece(standing))
	} else {
		absent = true
		for _, p := range interior.Pieces {
			if p.Slot == throneSlot && p.Def == def.Def {
				if !free(p) {
					return ThroneStep{}
				}
				template = append(template, planned(p))
				break
			}
		}
	}
	for _, w := range need.wants() {
		count := 0
		for _, b := range built {
			if count < w.Count && len(b.Cells) > 0 && rectInside(room.Interior, cellsRectangle(b.Cells)) && slices.Contains(w.Things, b.Building.Definition()) {
				template = append(template, standingPiece(b))
				count++
			}
		}
		for _, p := range interior.Pieces {
			if key, _, _ := strings.Cut(p.Slot, "."); count < w.Count && key == w.Key && p.Slot != throneSlot && free(p) {
				template = append(template, planned(p))
				absent = true
				count++
			}
		}
	}
	step.Template = template
	if absent || !plan.GroundMatches(room, ground) || len(throneIntruders(room, need, built)) > 0 {
		step.Kind = ThroneReconcile
		return step
	}
	return ThroneStep{}
}

// standingCount is the number of buildings of any of things standing inside
// r's interior.
func standingCount(r PlannedRoom, things []string, built []CurrentBuilding) int {
	n := 0
	for _, b := range built {
		if len(b.Cells) > 0 && rectInside(r.Interior, cellsRectangle(b.Cells)) && slices.Contains(things, b.Building.Definition()) {
			n++
		}
	}
	return n
}

// ThroneRoomTargets is the impressiveness target of the standing throne
// room, keyed by census room id, for the room quality levers (the room
// upgrade and the beauty upgrade): the title's minimum, reason "title".
// Empty when the plan holds no standing room of the title's area or the
// title asks for no impressiveness.
func ThroneRoomTargets(plan LayoutPlan, rooms RoomObservation, need ThroneNeed) map[string]RoomTarget {
	room, ok := plan.ThroneRoomFor(need.MinArea)
	if !ok || need.MinImpressiveness <= 0 {
		return nil
	}
	standing, ok := CensusRoomIn(room, rooms)
	if !ok {
		return nil
	}
	return map[string]RoomTarget{standing.ID: {Room: standing.ID, Min: float64(need.MinImpressiveness), Reasons: []string{"title"}}}
}

// ThroneAreaOwed is the room area the plan must grow for need, zero when it
// already holds one (or nobody is owed a throne).
func ThroneAreaOwed(plan LayoutPlan, need ThroneNeed, owed bool) int {
	if !owed {
		return 0
	}
	if _, ok := plan.ThroneRoomFor(need.MinArea); ok {
		return 0
	}
	return need.MinArea
}
