package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// The initial shelter is raised around its bunks (#612). A fresh starter
// shell is three rungs under the one goal, each a method of its epoch:
//
//  1. shelter-spots: sleeping spots on the site's interior, placed anywhere
//     (nothing is roofed yet), the cells the beds will not take. They are
//     an interim only: a pawn on a spot still sleeps on the ground.
//  2. shelter-beds: the beds, the first construction on the site, on the
//     interior cells off the ring's corners, the entrance aisle and the
//     starter storage patch.
//  3. the ring itself, once the bed plan has no open work, sited around
//     the bunks: the layout whose interior holds every bunk and whose
//     corners hold no bed.
//
// The bunks stand on ground the census then reports occupied, so the ring
// search is given their cells as free (shellSiteCells). A ring adopted from
// an earlier world (walls already standing) skips the rungs: its gaps are
// closed first and the room is furnished by the indoor step and the
// sleeping family as before. A bed rung refused whole (no wood at all)
// yields to the ring rather than holding the colony outdoors; the sleeping
// family furnishes the room later.
const (
	shelterSpotsMethod   domain.MethodID = "shelter-spots"
	shelterBedsMethod    domain.MethodID = "shelter-beds"
	shelterBedDefinition                 = "Bed"
	bunkPlanPrefix                       = "routine-bunks"
)

// BunkPlanPrefix names the plans the shelter's bunk rungs admit, for
// acceptance tooling reading the journal.
const BunkPlanPrefix = bunkPlanPrefix

// ShelterSpotsMethod and ShelterBedsMethod name the bunk rungs' methods
// under the initial shelter goal.
func ShelterSpotsMethod() domain.MethodID { return shelterSpotsMethod }
func ShelterBedsMethod() domain.MethodID  { return shelterBedsMethod }

// shelterSite is one review's context for siting the initial shelter.
type shelterSite struct {
	state     ControlState
	review    store.RoutineReview
	goal      store.GoalState
	facts     observation.ColonyProjection
	read      observation.ColonyReading
	snapshot  domain.GenerationSnapshot
	protected []domain.Cell
	check     func() error
}

// shelterBunkRecord is what the goal epoch already placed: the anchors of
// the spots and beds bound under it.
type shelterBunkRecord struct {
	spots, beds           []domain.Cell
	spotsBound, bedsBound bool
}

func (b shelterBunkRecord) cells() []domain.Cell {
	var cells []domain.Cell
	for _, anchor := range append(append([]domain.Cell(nil), b.spots...), b.beds...) {
		f := policy.BunkFootprint(anchor)
		cells = append(cells, f[0], f[1])
	}
	return cells
}

// shelterBunks reads the bunk rungs bound under the goal's epoch.
func (r *RoutineBuildingPlanner) shelterBunks(call context.Context, goal store.GoalState) (shelterBunkRecord, error) {
	journal := r.reviewer.player.journal
	var record shelterBunkRecord
	read := func(method domain.MethodID) ([]domain.Cell, bool, error) {
		bound, err := journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method)
		if errors.Is(err, store.ErrNotFound) {
			return nil, false, nil
		}
		if err != nil {
			return nil, false, err
		}
		plan, err := journal.LoadPlan(call, bound.Plan)
		if err != nil {
			return nil, false, err
		}
		var anchors []domain.Cell
		for _, action := range plan.Spec.Actions() {
			if b, ok := action.Building(); ok {
				anchors = append(anchors, b.Cell())
			}
		}
		return anchors, true, nil
	}
	var err error
	if record.spots, record.spotsBound, err = read(shelterSpotsMethod); err != nil {
		return record, err
	}
	if record.beds, record.bedsBound, err = read(shelterBedsMethod); err != nil {
		return record, err
	}
	return record, nil
}

// stepShelterSite sites the initial shelter for this review. It returns the
// ring's previews for the caller to admit as the shell method, or a handled
// result: an adopted ring's own outcome, an excavation stage, or a bunk rung
// admitted (or refused) this review.
func (r *RoutineBuildingPlanner) stepShelterSite(call, epoch context.Context, s shelterSite) ([]policy.Preview, policy.StockObservation, RoutineBuildingReason, *RoutineBuildingResult, error) {
	none := policy.StockObservation{}
	style := shelterStyle(s.facts)
	if selected, stock, reason, adopted, err := r.adoptShell(call, s.snapshot, s.facts, s.protected, style, s.check); err != nil || adopted {
		return selected, stock, reason, nil, err
	}
	record, err := r.shelterBunks(call, s.goal)
	if err != nil {
		return nil, none, "", nil, err
	}
	free := record.cells()
	freed := make(map[domain.Cell]bool, len(free))
	for _, c := range free {
		freed[c] = true
	}
	var protected []domain.Cell
	for _, c := range s.protected {
		if !freed[c] {
			protected = append(protected, c)
		}
	}
	grid, _ := layoutAlignment(s.facts)
	layouts, err := policy.StarterLayouts(policy.StarterRequest{Bounds: s.facts.Bounds, Anchor: layoutAnchor(s.facts, r.district()), Cells: shellSiteCells(s.facts, free), Protected: protected, Shelter: style, Grid: grid, Shape: r.shapeFamily(s.facts)})
	if err != nil {
		return nil, none, "", nil, err
	}
	// Digging in is weighed against the best layout before anything is
	// previewed; a layout the native previews then refuse whole yields to
	// the dig below, as the previewed shell used to. Bunks already placed
	// commit the colony to the surface site: the dig is never chosen once
	// colonists sleep where the ring will rise.
	var target *policy.ExcavationTarget
	if r.excavation != nil && len(free) == 0 {
		if target, err = r.excavationCandidate(call, s.snapshot, s.facts, s.protected, s.check); err != nil {
			return nil, none, "", nil, err
		}
		var shell *policy.StarterLayout
		if len(layouts) > 0 {
			shell = &layouts[0]
		}
		if policy.ChooseExcavation(s.facts.Center, shell, target) {
			result, err := r.stepExcavation(call, epoch, excavationStep{state: s.state, review: s.review, goal: s.goal, facts: s.facts, read: s.read, target: *target})
			return nil, none, "", &result, err
		}
	}
	if len(free) > 0 {
		if layout, ok := policy.BunkLayout(layouts, record.beds, record.spots); ok {
			ordered := []policy.StarterLayout{layout}
			for _, other := range layouts {
				if other.Room != layout.Room || !domain.SameRoomFootprint(other.Shell, layout.Shell) {
					ordered = append(ordered, other)
				}
			}
			layouts = ordered
		} else {
			clockSchedulerLog("%s: no layout encloses the bunks placed earlier (beds=%v spots=%v); the shell is sited afresh", r.goal, record.beds, record.spots)
		}
	}
	if len(layouts) == 0 {
		return nil, none, BuildingMethodNoSpace, nil, nil
	}
	indoor := *r
	indoor.shelter, indoor.definition = false, "SleepingSpot"
	owed, _, reason := indoor.selection(s.facts)
	if reason != "" {
		return nil, none, reason, nil, nil
	}
	if !record.spotsBound && !record.bedsBound {
		bunks := policy.PlanShelterBunks(layouts[0], int(owed), int(owed), nil)
		result, admitted, err := r.admitBunks(call, epoch, s, shelterSpotsMethod, "SleepingSpot", bunks.Spots)
		if err != nil || admitted {
			return nil, none, "", &result, err
		}
	}
	if !record.bedsBound {
		bunks := policy.PlanShelterBunks(layouts[0], int(owed), 0, record.cells())
		result, admitted, err := r.admitBunks(call, epoch, s, shelterBedsMethod, shelterBedDefinition, bunks.Beds)
		if err != nil || admitted {
			return nil, none, "", &result, err
		}
	}
	selected, stock, reason, err := r.previewFreshShell(call, s.snapshot, s.facts, layouts, s.check)
	if err == nil && reason == BuildingMethodNoSpace && target != nil {
		result, err := r.stepExcavation(call, epoch, excavationStep{state: s.state, review: s.review, goal: s.goal, facts: s.facts, read: s.read, target: *target})
		return nil, none, "", &result, err
	}
	return selected, stock, reason, nil, err
}

// admitBunks previews the bunks natively and admits the placeable ones as
// the rung's method. It reports admitted=false, without error, when the
// rung has nothing to place (no candidate, definition unavailable, none
// placeable, or the method refused whole), so the next rung is tried in
// the same review.
func (r *RoutineBuildingPlanner) admitBunks(call, epoch context.Context, s shelterSite, method domain.MethodID, definition string, anchors []domain.Cell) (RoutineBuildingResult, bool, error) {
	if len(anchors) == 0 {
		clockSchedulerLog("%s: %s: no bunk fits the site", r.goal, method)
		return RoutineBuildingResult{}, false, nil
	}
	if !routineDefinitionsAvailable(s.facts, []string{definition}, false) {
		clockSchedulerLog("%s: %s: %s is not buildable now", r.goal, method, definition)
		return RoutineBuildingResult{}, false, nil
	}
	var stuff string
	for _, d := range s.facts.Definitions {
		if d.Name == definition {
			if v, known := d.Stuff.Value(); known {
				stuff = v
			}
		}
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", s.goal.Goal.ID, s.goal.Goal.Epoch, method)))
	snapshot := s.state.Snapshot
	snapshot.Plan = domain.PlanID(fmt.Sprintf("%s-%x", bunkPlanPrefix, digest[:16]))
	snapshot.Revision = 1
	actions := make([]domain.Action, 0, len(anchors))
	for i, anchor := range anchors {
		b, err := domain.NewBuilding(definition, anchor, domain.North, stuff)
		if err != nil {
			return RoutineBuildingResult{}, false, err
		}
		action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", snapshot.Plan, i)), b)
		if err != nil {
			return RoutineBuildingResult{}, false, err
		}
		actions = append(actions, action)
	}
	if err := s.check(); err != nil {
		return RoutineBuildingResult{}, false, err
	}
	var previews []bridge.BuildingPreview
	if batch, ok := r.native.(shellBatchPreviewer); ok {
		var err error
		if previews, _, err = batch.PreviewBuildings(call, actions, snapshot); err != nil {
			return RoutineBuildingResult{}, false, err
		}
	} else {
		for _, action := range actions {
			preview, _, err := r.native.PreviewBuilding(call, action, snapshot)
			if err != nil {
				return RoutineBuildingResult{}, false, err
			}
			previews = append(previews, preview)
		}
	}
	if err := s.check(); err != nil {
		return RoutineBuildingResult{}, false, err
	}
	if len(previews) != len(actions) {
		return RoutineBuildingResult{}, false, ErrControl
	}
	stock := policy.StockObservation{Snapshot: snapshot, Tick: s.facts.Identity.Tick}
	var selected []policy.Preview
	for i, preview := range previews {
		v := preview.Preview
		if v.Action != actions[i] || !v.Snapshot.Matches(snapshot) || !v.Tick.FreshFor(s.facts.Identity.Tick) || !preview.Stock.Snapshot.Matches(snapshot) || !preview.Stock.Tick.FreshFor(s.facts.Identity.Tick) {
			return RoutineBuildingResult{}, false, ErrControl
		}
		made, known := v.MadeFromStuff.Value()
		if !known || made != (stuff != "") {
			continue
		}
		can, canKnown := v.CanPlace.Value()
		safe, safeKnown := v.SafeToPlace.Value()
		footprint, footprintKnown := v.Footprint.Value()
		if !canKnown || !can || !safeKnown || !safe || !footprintKnown || !sameBunkFootprint(anchors[i], footprint) {
			continue
		}
		if err := mergeRoutineStock(&stock, preview.Stock, len(selected) == 0); err != nil {
			return RoutineBuildingResult{}, false, err
		}
		selected = append(selected, v)
	}
	if len(selected) == 0 {
		clockSchedulerLog("%s: %s: none of %d bunks placeable", r.goal, method, len(anchors))
		return RoutineBuildingResult{}, false, nil
	}
	result, err := r.admitPreviews(call, epoch, routineAdmission{state: s.state, review: s.review, goal: s.goal, facts: s.facts, read: s.read, method: method, snapshot: snapshot, selected: selected, stock: stock, purpose: policy.Routine, partial: true, check: s.check})
	if err != nil {
		return result, false, err
	}
	clockSchedulerLog("%s: %s: %s x%d reason=%s refused=%d", r.goal, method, definition, len(selected), result.Reason, len(result.Decision.Refused))
	return result, result.Reason == BuildingMethodAdmitted, nil
}

// sameBunkFootprint reports whether the native footprint is exactly the two
// cells a north-facing bunk anchored at anchor occupies.
func sameBunkFootprint(anchor domain.Cell, footprint []domain.Cell) bool {
	if len(footprint) != 2 {
		return false
	}
	want := policy.BunkFootprint(anchor)
	return footprint[0] == want[0] && footprint[1] == want[1] || footprint[0] == want[1] && footprint[1] == want[0]
}
