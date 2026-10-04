package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"path"
	"slices"

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
)

// shelterBeds is the bed rung's definition and anchors on the ladder
// (#1181): Bed when it is buildable, else as many bedrolls as the stock of
// their first stocked stuff covers, else Bed (which admitBunks refuses).
func shelterBeds(facts observation.ColonyProjection, anchors []domain.Cell) (string, []domain.Cell) {
	if available, known := facts.DefinitionAvailable(shelterBedDefinition).Value(); known && available {
		return shelterBedDefinition, anchors
	}
	if available, _ := facts.DefinitionAvailable(policy.SleepingBedrollDefinition).Value(); available {
		if _, count, ok := facts.StockedStuff(policy.SleepingBedrollDefinition); ok {
			return policy.SleepingBedrollDefinition, anchors[:min(int64(len(anchors)), count)]
		}
	}
	return shelterBedDefinition, anchors
}

// ShellMethodPatterns are the GLOB patterns matching every whole-shell
// method, for acceptance tooling reading the journal (#987).
func ShellMethodPatterns() []string { return slices.Clone(shellMethodPatterns) }

// IsShellMethod reports a whole-shell method (shellMethodPatterns).
func IsShellMethod(method domain.MethodID) bool {
	for _, p := range shellMethodPatterns {
		if ok, _ := path.Match(p, string(method)); ok {
			return true
		}
	}
	return false
}

// ShelterSpotsMethod and ShelterBedsMethod name the bunk rungs' methods
// under the initial shelter goal.
func ShelterSpotsMethod() domain.MethodID { return shelterSpotsMethod }
func ShelterBedsMethod() domain.MethodID  { return shelterBedsMethod }

// shelterSite is one review's context for siting the initial shelter.
type shelterSite struct {
	state     ControlState
	review    store.RoutineReview
	goal      store.WorkOwner
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
func (r *RoutineBuildingPlanner) shelterBunks(call context.Context, goal store.WorkOwner) (shelterBunkRecord, error) {
	journal := r.reviewer.player.journal
	var record shelterBunkRecord
	read := func(method domain.MethodID) ([]domain.Cell, bool, error) {
		bound, err := journal.LoadOwnerMethod(call, goal, method)
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

// stepShelterSite sites the initial shelter for this review on the layout
// plan's storeroom (#1231). It returns the ring's previews for the caller
// to admit as the shell method, or a handled result: an adopted ring's own
// outcome, an excavation stage, the room's plan dig or ruin claims, or a
// bunk rung admitted (or refused) this review.
func (r *RoutineBuildingPlanner) stepShelterSite(call, epoch context.Context, s shelterSite) ([]policy.Preview, policy.StockObservation, Verdict, *RoutineBuildingResult, error) {
	none := policy.StockObservation{}
	if selected, stock, reason, adopted, err := r.adoptShell(call, s.snapshot, s.facts, s.protected, s.check); err != nil || adopted {
		return selected, stock, reason, nil, err
	}
	record, err := r.shelterBunks(call, s.goal)
	if err != nil {
		return nil, none, Verdict{}, nil, err
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
	layout, room, sited, err := r.plannedShell(call, s.facts, protected, free, s.check)
	if err != nil {
		return nil, none, Verdict{}, nil, err
	}
	step := excavationStep{state: s.state, review: s.review, goal: s.goal, facts: s.facts, read: s.read}
	if !sited {
		return nil, none, noSpace("planned_shell_room"), nil, nil
	}
	if len(free) > 0 {
		if _, ok := policy.BunkLayout([]policy.StarterLayout{layout}, record.beds, record.spots); !ok {
			clockSchedulerLog("%s: the planned room does not enclose the bunks placed earlier (beds=%v spots=%v)", r.goal, record.beds, record.spots)
		}
	}
	// The planned room's rock is dug and its ruins claimed before the bunks
	// stand on it; either holds the first roof, and says so in its log.
	if result, handled, err := r.prepareShell(call, epoch, step, layout, room, s.check); err != nil || handled {
		return nil, none, Verdict{}, &result, err
	}
	indoor := *r
	indoor.shelter, indoor.definition = false, "SleepingSpot"
	owed, _, reason := indoor.selection(s.facts)
	if !reason.IsZero() {
		return nil, none, reason, nil, nil
	}
	if !record.spotsBound && !record.bedsBound {
		bunks := policy.PlanShelterBunks(layout, int(owed), int(owed), nil)
		result, admitted, err := r.admitBunks(call, epoch, s, shelterSpotsMethod, "SleepingSpot", bunks.Spots)
		if err != nil || admitted {
			return nil, none, Verdict{}, &result, err
		}
	}
	if !record.bedsBound {
		bunks := policy.PlanShelterBunks(layout, int(owed), 0, record.cells())
		definition, anchors := shelterBeds(s.facts, bunks.Beds)
		result, admitted, err := r.admitBunks(call, epoch, s, shelterBedsMethod, definition, anchors)
		if err != nil || admitted {
			return nil, none, Verdict{}, &result, err
		}
	}
	selected, stock, reason, err := r.previewPlannedRing(call, s.snapshot, s.facts, layout, s.check)
	return selected, stock, reason, nil, err
}

// shellRuinHolds stamps the site cells with the clearance census holds a
// claim honours (#718), so a ring never counts on claiming a ruin the claim
// would leave alone. Without ruins on the site or a known census, the cells
// are returned unchanged.
func (r *RoutineBuildingPlanner) shellRuinHolds(call context.Context, facts observation.ColonyProjection, cells []policy.SiteCell, check func() error) ([]policy.SiteCell, error) {
	ruins := false
	for _, c := range cells {
		ruins = ruins || positiveFact(c.Ruin)
	}
	if !ruins {
		return cells, nil
	}
	source, ok := r.native.(observation.ClearanceSource)
	if !ok {
		return nil, fmt.Errorf("%w: shellRuinHolds: native lacks the clearance census", ErrControl)
	}
	read, err := observation.ObserveClearanceCensus(call, source, facts.Identity, false)
	if err != nil {
		return nil, err
	}
	if err := check(); err != nil {
		return nil, err
	}
	census, known := read.Value()
	if !known {
		return cells, nil
	}
	return policy.ShellRuinHolds(census.Targets, cells), nil
}

// shellClaimReader refreshes a claimable building's CAS token.
type shellClaimReader interface {
	ReadClaimBuildingTarget(context.Context, *c.Identity, string) (bridge.ClaimBuildingTarget, bridge.Result, error)
}

// shellClaimMethod is the per-epoch method that claims the ruin walls on a
// planned room's ring (#718, #1231).
func shellClaimMethod(room domain.Cell) domain.MethodID {
	return domain.MethodID(fmt.Sprintf("shell-claim-%d-%d", room.X, room.Z))
}

// admitShellClaims claims the ruin walls of the ring's kind standing on
// the planned room's ring (#718), once per goal epoch, before the ring,
// which is then raised on the ring's other cells. Home clearance may still
// deconstruct such a ruin first (#1024); the next siting then finds open
// ground there and the ring walls it as any other cell. It reports
// handled=false, without error, when the method is spent or nothing on the
// ring is still claimable, so the ring is raised this review.
func (r *RoutineBuildingPlanner) admitShellClaims(call, epoch context.Context, s excavationStep, layout policy.StarterLayout, check func() error) (RoutineBuildingResult, bool, error) {
	method := shellClaimMethod(domain.Cell{X: layout.Room.X, Z: layout.Room.Z})
	if _, err := r.reviewer.player.journal.LoadOwnerMethod(call, s.goal, method); err == nil {
		return RoutineBuildingResult{}, false, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineBuildingResult{}, false, err
	}
	source, ok := r.native.(observation.ClearanceSource)
	reader, readable := r.native.(shellClaimReader)
	if !ok || !readable {
		return RoutineBuildingResult{}, false, nil
	}
	snapshot := s.state.Snapshot
	snapshot.Revision = 1
	read, err := observation.ObserveClearanceCensus(call, source, s.facts.Identity, false)
	if err != nil {
		return RoutineBuildingResult{}, false, err
	}
	if err := check(); err != nil {
		return RoutineBuildingResult{}, false, err
	}
	census, known := read.Value()
	if !known {
		return RoutineBuildingResult{}, false, nil
	}
	snapshot.Plan = domain.MintPlanID()
	var actions []domain.Action
	for _, target := range policy.ShellClaims(census.Targets, layout.Claimed) {
		current, _, err := reader.ReadClaimBuildingTarget(call, boundary.Identity(snapshot), target.EntityID)
		if err != nil {
			return RoutineBuildingResult{}, false, err
		}
		if current.PlayerOwned {
			continue
		}
		value, err := domain.NewClaimBuilding(target.EntityID)
		if err != nil {
			return RoutineBuildingResult{}, false, err
		}
		action, err := domain.NewClaimBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", snapshot.Plan, len(actions))), value)
		if err != nil {
			return RoutineBuildingResult{}, false, err
		}
		actions = append(actions, action)
	}
	if err := check(); err != nil {
		return RoutineBuildingResult{}, false, err
	}
	if len(actions) == 0 {
		clockSchedulerLog("%s: %s: none of %d ring ruins still claimable", r.goal, method, len(layout.Claimed))
		return RoutineBuildingResult{}, false, nil
	}
	plan, err := domain.NewPlan(snapshot.Plan, 1, actions)
	if err != nil {
		return RoutineBuildingResult{}, false, err
	}
	result, err := r.admitExcavation(call, epoch, s, snapshot, method, plan, nil, policy.StockObservation{Snapshot: snapshot, Tick: s.facts.Identity.Tick}, check)
	if err != nil {
		return result, false, err
	}
	clockSchedulerLog("%s: %s: %d claims reason=%s", r.goal, method, len(actions), result.Verdict)
	return result, result.Verdict == BuildingReasonAdmitted, nil
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
	if gate := definitionsGate(s.facts, []string{definition}, false); !gate.IsZero() {
		clockSchedulerLog("%s: %s: %s is not buildable now: %s", r.goal, method, definition, gate.Text())
		return RoutineBuildingResult{}, false, nil
	}
	stuff := bedStuff(s.facts, definition)
	snapshot := s.state.Snapshot
	snapshot.Plan = domain.MintPlanID()
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
	if len(previews) != len(actions) {
		return RoutineBuildingResult{}, false, fmt.Errorf("%w: admitBunks: len(previews) != len(actions)", ErrControl)
	}
	stock := policy.StockObservation{Snapshot: snapshot, Tick: s.facts.Identity.Tick}
	var selected []policy.Preview
	for i, preview := range previews {
		v := preview.Preview
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
	result, err := r.admitPreviews(call, epoch, routineAdmission{state: s.state, review: s.review, goal: s.goal, facts: s.facts, method: method, snapshot: snapshot, selected: selected, stock: stock, purpose: policy.Routine})
	if err != nil {
		return result, false, err
	}
	clockSchedulerLog("%s: %s: %s x%d reason=%s refused=%d", r.goal, method, definition, len(selected), result.Verdict, len(result.Decision.Refused))
	return result, result.Verdict == BuildingReasonAdmitted, nil
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
