package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
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
//  3. the ring itself, at the next review whether or not the beds stand
//     (#641), sited around the bunks: the layout whose interior holds every
//     bunk and whose corners hold no bed.
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

// shelterMineMethod digs the natural rock out of the shell's interior
// (#700), after the bunks and before the ring, under its own plan prefix
// so no shell or excavation history mistakes it for theirs.
const (
	shelterMineMethod   domain.MethodID = "shelter-mine"
	shellMinePlanPrefix string          = "routine-shelter-mine"
)

// shelterClearMethod claims the ruin walls of the ring's kind standing on
// the shell's ring (#718) and deconstructs the other ruins there (#709),
// after the bunks and before the ring, which is then raised on the ring's
// other cells and closed by adoption once the ruins are gone.
const (
	shelterClearMethod   domain.MethodID = "shelter-clear"
	shellClearPlanPrefix string          = "routine-shelter-clear"
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
	search := func(anchor domain.Cell) ([]policy.StarterLayout, error) {
		return policy.StarterLayouts(policy.StarterRequest{Bounds: s.facts.Bounds, Anchor: anchor, Cells: shellSiteCells(s.facts, free), Protected: protected, Shelter: style, Grid: grid, Shape: r.shapeFamily(s.facts), WallDef: shellStyle(s.facts).WallDef})
	}
	layouts, err := search(layoutAnchor(s.facts, r.district()))
	if err != nil {
		return nil, none, "", nil, err
	}
	if _, ok := policy.BunkLayout(layouts, record.beds, record.spots); len(free) > 0 && !ok {
		// The colony centre is where the pawns stand this tick, so it drifts
		// between reviews and can push the site the bunks stand on out of
		// the capped candidates (#672): search again from the bunks.
		rescue, err := search(bunkAnchor(free))
		if err != nil {
			return nil, none, "", nil, err
		}
		if _, ok := policy.BunkLayout(rescue, record.beds, record.spots); ok {
			layouts = rescue
		}
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
		result, admitted, err := r.admitBunks(call, epoch, s, shelterSpotsMethod, "SleepingSpot", bunks.Spots, nil)
		if err != nil || admitted {
			return nil, none, "", &result, err
		}
	}
	if !record.bedsBound {
		bunks := policy.PlanShelterBunks(layouts[0], int(owed), 0, record.cells())
		enclosure := func() (map[policy.Resource]int64, error) { return r.enclosureReserve(call, s, layouts) }
		result, admitted, err := r.admitBunks(call, epoch, s, shelterBedsMethod, shelterBedDefinition, bunks.Beds, enclosure)
		if err != nil || admitted {
			return nil, none, "", &result, err
		}
	}
	if r.excavation == nil {
		layouts = unmined(layouts)
	} else if len(layouts) > 0 && len(layouts[0].Mined) > 0 {
		result, admitted, err := r.admitShellMining(call, epoch, s, layouts[0])
		if err != nil || admitted {
			return nil, none, "", &result, err
		}
	}
	if len(layouts) > 0 && len(layouts[0].Cleared)+len(layouts[0].Claimed) > 0 {
		result, admitted, err := r.admitShellClearing(call, epoch, s, layouts[0])
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

// admitShellMining designates the natural rock inside the sited shell for
// mining (#700) once per goal epoch, as the shelter-mine rung. It reports
// admitted=false, without error, when the rung is spent, the site cannot be
// dug now (no support, a collapse pending, no miner) or nothing in it is
// eligible, so the ring is still raised this review; the rock left standing
// inside only shrinks the room until a later epoch digs it.
func (r *RoutineBuildingPlanner) admitShellMining(call, epoch context.Context, s shelterSite, layout policy.StarterLayout) (RoutineBuildingResult, bool, error) {
	if _, err := r.reviewer.player.journal.LoadGoalMethod(call, s.goal.Goal.ID, s.goal.Goal.Epoch, shelterMineMethod); err == nil {
		return RoutineBuildingResult{}, false, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineBuildingResult{}, false, err
	}
	snapshot := s.state.Snapshot
	snapshot.Revision = 1
	site, err := r.readExcavationSite(call, snapshot, s.facts.Identity.Tick, layout.Mined, layout.Shell.Threshold(), s.check)
	if err != nil {
		return RoutineBuildingResult{}, false, err
	}
	if site.CollapsePending || site.Support == policy.ExcavationSupportUnsupported || !site.WorkerAvailable {
		clockSchedulerLog("%s: %s: interior not diggable now: support=%d (%s) collapse=%v worker=%v", r.goal, shelterMineMethod, site.Support, site.SupportBlocker, site.CollapsePending, site.WorkerAvailable)
		return RoutineBuildingResult{}, false, nil
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", s.goal.Goal.ID, s.goal.Goal.Epoch, shelterMineMethod)))
	snapshot.Plan = domain.PlanID(fmt.Sprintf("%s-%x", shellMinePlanPrefix, digest[:16]))
	var actions []domain.Action
	for _, cell := range site.Cells {
		if !cell.Eligible || cell.MineDesignated || cell.Definition == "" {
			continue
		}
		excavation, err := domain.NewExcavation(cell.Cell, cell.Definition)
		if err != nil {
			return RoutineBuildingResult{}, false, err
		}
		action, err := domain.NewExcavationAction(domain.ActionID(fmt.Sprintf("%s-%d", snapshot.Plan, len(actions))), excavation)
		if err != nil {
			return RoutineBuildingResult{}, false, err
		}
		actions = append(actions, action)
	}
	if len(actions) == 0 {
		clockSchedulerLog("%s: %s: none of %d interior rock cells eligible", r.goal, shelterMineMethod, len(layout.Mined))
		return RoutineBuildingResult{}, false, nil
	}
	plan, err := domain.NewPlan(snapshot.Plan, 1, actions)
	if err != nil {
		return RoutineBuildingResult{}, false, err
	}
	result, err := r.admitExcavation(call, epoch, excavationStep{state: s.state, review: s.review, goal: s.goal, facts: s.facts, read: s.read}, snapshot, shelterMineMethod, plan, nil, policy.StockObservation{Snapshot: snapshot, Tick: s.facts.Identity.Tick}, s.check)
	if err != nil {
		return result, false, err
	}
	clockSchedulerLog("%s: %s: %d rock cells reason=%s", r.goal, shelterMineMethod, len(actions), result.Reason)
	return result, result.Reason == BuildingMethodAdmitted, nil
}

// shellClaimReader refreshes a claimable building's CAS token.
type shellClaimReader interface {
	ReadClaimBuildingTarget(context.Context, *c.Identity, string) (bridge.ClaimBuildingTarget, bridge.Result, error)
}

// admitShellClearing claims the ruin walls on the sited shell's ring that
// it keeps as wall (#718) and designates its other ruins for deconstruction
// (#709), once per goal epoch, as the shelter-clear rung. The clearance
// census names each ruin's building; a ruin to clear that the census holds
// for a reason other than lying outside Home (a roof it carries, an ancient
// danger) is left standing, and the ring's gap there waits on adoption. It
// reports admitted=false, without error, when the rung is spent or nothing
// on the ring is claimable or clearable, so the ring is still raised this
// review.
func (r *RoutineBuildingPlanner) admitShellClearing(call, epoch context.Context, s shelterSite, layout policy.StarterLayout) (RoutineBuildingResult, bool, error) {
	if _, err := r.reviewer.player.journal.LoadGoalMethod(call, s.goal.Goal.ID, s.goal.Goal.Epoch, shelterClearMethod); err == nil {
		return RoutineBuildingResult{}, false, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineBuildingResult{}, false, err
	}
	source, ok := r.native.(observation.ClearanceSource)
	if !ok {
		return RoutineBuildingResult{}, false, nil
	}
	snapshot := s.state.Snapshot
	snapshot.Revision = 1
	read, err := observation.ObserveClearanceCensus(call, source, s.facts.Identity)
	if err != nil {
		return RoutineBuildingResult{}, false, err
	}
	if err := s.check(); err != nil {
		return RoutineBuildingResult{}, false, err
	}
	census, known := read.Value()
	if !known {
		return RoutineBuildingResult{}, false, nil
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", s.goal.Goal.ID, s.goal.Goal.Epoch, shelterClearMethod)))
	snapshot.Plan = domain.PlanID(fmt.Sprintf("%s-%x", shellClearPlanPrefix, digest[:16]))
	var actions []domain.Action
	claims := policy.ShellClaims(census.Targets, layout.Claimed)
	if reader, ok := r.native.(shellClaimReader); ok {
		for _, target := range claims {
			current, _, err := reader.ReadClaimBuildingTarget(call, boundary.Identity(snapshot), target.EntityID)
			if err != nil {
				return RoutineBuildingResult{}, false, err
			}
			if current.PlayerOwned {
				continue
			}
			value, err := domain.NewClaimBuilding(target.EntityID, current.Token)
			if err != nil {
				return RoutineBuildingResult{}, false, err
			}
			action, err := domain.NewClaimBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", snapshot.Plan, len(actions))), value)
			if err != nil {
				return RoutineBuildingResult{}, false, err
			}
			actions = append(actions, action)
		}
		if err := s.check(); err != nil {
			return RoutineBuildingResult{}, false, err
		}
	}
	claimed := len(actions)
	targets := policy.ShellRuins(census.Targets, layout.Cleared)
	for _, target := range targets {
		value, err := domain.NewDeconstruction(target.EntityID, target.DefName, target.Minimum)
		if err != nil {
			return RoutineBuildingResult{}, false, err
		}
		action, err := domain.NewDeconstructionAction(domain.ActionID(fmt.Sprintf("%s-%d", snapshot.Plan, len(actions))), value)
		if err != nil {
			return RoutineBuildingResult{}, false, err
		}
		actions = append(actions, action)
	}
	if len(actions) == 0 {
		clockSchedulerLog("%s: %s: none of %d ring ruins claimable or clearable", r.goal, shelterClearMethod, len(layout.Claimed)+len(layout.Cleared))
		return RoutineBuildingResult{}, false, nil
	}
	plan, err := domain.NewPlan(snapshot.Plan, 1, actions)
	if err != nil {
		return RoutineBuildingResult{}, false, err
	}
	result, err := r.admitExcavation(call, epoch, excavationStep{state: s.state, review: s.review, goal: s.goal, facts: s.facts, read: s.read}, snapshot, shelterClearMethod, plan, nil, policy.StockObservation{Snapshot: snapshot, Tick: s.facts.Identity.Tick}, s.check)
	if err != nil {
		return result, false, err
	}
	clockSchedulerLog("%s: %s: %d claims, %d ruins reason=%s", r.goal, shelterClearMethod, claimed, len(actions)-claimed, result.Reason)
	return result, result.Reason == BuildingMethodAdmitted, nil
}

// admitBunks previews the bunks natively and admits the placeable ones as
// the rung's method. It reports admitted=false, without error, when the
// rung has nothing to place (no candidate, definition unavailable, none
// placeable, or the method refused whole), so the next rung is tried in
// the same review. A non-nil reserve is the enclosure budget (#641): the
// materials the ring needs are held back from the observed stock first, and
// only the bunks the remainder pays for are admitted, so partial stock
// raises walls before furniture; a rung the remainder pays for none of
// yields to the ring.
func (r *RoutineBuildingPlanner) admitBunks(call, epoch context.Context, s shelterSite, method domain.MethodID, definition string, anchors []domain.Cell, reserve func() (map[policy.Resource]int64, error)) (RoutineBuildingResult, bool, error) {
	if len(anchors) == 0 {
		clockSchedulerLog("%s: %s: no bunk fits the site", r.goal, method)
		return RoutineBuildingResult{}, false, nil
	}
	if !routineDefinitionsAvailable(s.facts, []string{definition}, false) {
		clockSchedulerLog("%s: %s: %s is not buildable now", r.goal, method, definition)
		return RoutineBuildingResult{}, false, nil
	}
	var held map[policy.Resource]int64
	if reserve != nil {
		var err error
		if held, err = reserve(); err != nil {
			return RoutineBuildingResult{}, false, err
		}
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
	spent := map[policy.Resource]int64{}
	unpaid := 0
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
		if held != nil && !bunkAffordable(spent, held, v, preview.Stock) {
			unpaid++
			continue
		}
		if costs, known := v.Costs.Value(); known {
			for _, cost := range costs {
				spent[cost.Resource] += cost.Count
			}
		}
		if err := mergeRoutineStock(&stock, preview.Stock, len(selected) == 0); err != nil {
			return RoutineBuildingResult{}, false, err
		}
		selected = append(selected, v)
	}
	if unpaid > 0 {
		clockSchedulerLog("%s: %s: enclosure budget %v holds back %d of %d bunks", r.goal, method, held, unpaid, len(anchors))
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

// enclosureReserve is the material the ring around the bunks costs: the
// native previews of the best placeable layout, summed. A site with no
// placeable ring reserves nothing; the ring step reports that itself.
func (r *RoutineBuildingPlanner) enclosureReserve(call context.Context, s shelterSite, layouts []policy.StarterLayout) (map[policy.Resource]int64, error) {
	ring, _, reason, err := r.previewFreshShell(call, s.snapshot, s.facts, layouts, s.check)
	if err != nil {
		return nil, err
	}
	held := map[policy.Resource]int64{}
	if reason != "" {
		return held, nil
	}
	for _, v := range ring {
		if costs, known := v.Costs.Value(); known {
			for _, cost := range costs {
				held[cost.Resource] += cost.Count
			}
		}
	}
	return held, nil
}

// bunkAffordable reports whether the bunk's costs fit its own stock scan
// after the enclosure reserve and the rung's earlier bunks; an unknown
// cost or availability never holds a bunk back here.
func bunkAffordable(spent, held map[policy.Resource]int64, v policy.Preview, stock policy.StockObservation) bool {
	available := map[policy.Resource]domain.Fact[int64]{}
	for _, row := range stock.Values {
		available[row.Resource] = row.Available
	}
	costs, _ := v.Costs.Value()
	for _, cost := range costs {
		if have, known := available[cost.Resource].Value(); known && held[cost.Resource]+spent[cost.Resource]+cost.Count > have {
			return false
		}
	}
	return true
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

// bunkAnchor is the cell at the centre of the bunks' bounding box.
func bunkAnchor(cells []domain.Cell) domain.Cell {
	lo, hi := cells[0], cells[0]
	for _, c := range cells[1:] {
		lo.X, lo.Z = min(lo.X, c.X), min(lo.Z, c.Z)
		hi.X, hi.Z = max(hi.X, c.X), max(hi.Z, c.Z)
	}
	return domain.Cell{X: (lo.X + hi.X) / 2, Z: (lo.Z + hi.Z) / 2}
}
