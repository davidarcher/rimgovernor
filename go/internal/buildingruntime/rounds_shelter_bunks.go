package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// The initial shelter is raised around its bunks. A fresh starter
// shell is three rungs under the one goal, each a method of its epoch:
//
//  1. shelter-spots: sleeping spots on the shelter template's bunk slots
//     (policy.PlanShelterBunks), placed before anything is roofed.
//     They are an interim only: a pawn on a spot still sleeps on the ground.
//  2. shelter-beds: the beds, the first construction on the site, on the
//     same slots, which keep off the ring's corners, the entrance aisle and
//     the starter storage patch. The native refuses a bed blueprint over a
//     standing spot, so the standing spots are deleted first (the clearing
//     method, upgradeBunks; a bedroll is packed to storage instead) and the
//     beds go on the freed cells.
//  3. the ring itself, at the next review whether or not the beds stand
//     : the planned room's ring through one reconcile. The
//     ring stands on the plan's footprint, which the bunk slots lie inside
//     and keep off the corners of.
//
// A room whose ring is begun or standing skips the rungs: its gaps are
// closed first and the room is furnished by the indoor step and the
// sleeping family as before. A bed rung refused whole (no wood at all)
// yields to the ring rather than holding the colony outdoors; the sleeping
// family furnishes the room later.
const (
	shelterSpotsMethod         domain.MethodID = "shelter-spots"
	shelterBedrollsMethod      domain.MethodID = "shelter-bedrolls"
	shelterBedsMethod          domain.MethodID = "shelter-beds"
	shelterClearBedrollsMethod domain.MethodID = "shelter-clear-bedrolls"
	shelterClearBedsMethod     domain.MethodID = "shelter-clear-beds"
	shelterBedDefinition                       = "Bed"
)

// isShelterBunkMethod reports a bunk rung or clearing step under the
// initial shelter goal (store.shelterBunkMethods names the same set).
func isShelterBunkMethod(m domain.MethodID) bool {
	switch m {
	case shelterSpotsMethod, shelterBedrollsMethod, shelterBedsMethod, shelterClearBedrollsMethod, shelterClearBedsMethod:
		return true
	}
	return false
}

// shelterRung is the placement method and the clearing method of a bunk
// definition; a spot has no clearing step.
func shelterRung(definition string) (place, clear domain.MethodID) {
	switch definition {
	case policy.SleepingBedrollDefinition:
		return shelterBedrollsMethod, shelterClearBedrollsMethod
	case shelterBedDefinition:
		return shelterBedsMethod, shelterClearBedsMethod
	}
	return shelterSpotsMethod, ""
}

// bunkRank orders the ladder: spot, bedroll, bed.
func bunkRank(definition string) int {
	switch definition {
	case policy.SleepingBedrollDefinition:
		return 1
	case shelterBedDefinition:
		return 2
	}
	return 0
}

// shelterBunk is one bunk slot: the anchor and rotation a building order
// names (the footprint is policy.BunkRect).
type shelterBunk struct {
	anchor domain.Cell
	rot    domain.Rotation
}

func (b shelterBunk) rect() policy.Rectangle { return policy.BunkRect(b.anchor, b.rot) }

// shelterBunkSlots are the template's bunk slots for the site as orders.
func shelterBunkSlots(room policy.PlannedRoom, facts observation.ColonyProjection, occupants int) []shelterBunk {
	var out []shelterBunk
	plan, _ := facts.LayoutPlan.Value()
	for _, p := range policy.PlanShelterBunks(room, plan.RoomRock(room, facts.Cells).Dig, facts.Shapes, occupants, shelterCampfires(facts), shelterCoolers(facts), nil) {
		out = append(out, shelterBunk{p.Anchor(), p.Rot})
	}
	return out
}

// shelterBeds is the bed rung's definition and slots on the ladder:
// Bed when it is buildable, else as many bedrolls as the stock of
// their first stocked stuff covers, else Bed (which admitBunks refuses).
func shelterBeds(facts observation.ColonyProjection, slots []shelterBunk) (string, []shelterBunk) {
	if available, known := facts.DefinitionAvailable(shelterBedDefinition).Value(); known && available {
		return shelterBedDefinition, slots
	}
	if available, _ := facts.DefinitionAvailable(policy.SleepingBedrollDefinition).Value(); available {
		if _, count, ok := facts.StockedStuff(policy.SleepingBedrollDefinition); ok {
			return policy.SleepingBedrollDefinition, slots[:min(int64(len(slots)), count)]
		}
	}
	return shelterBedDefinition, slots
}

// IsShellMethod reports a planned room's ring method: a build wave of a
// "<role>-shell-<x>-<z>" reconcile (plannedRoomMethod), for acceptance tooling
// reading the journal.
func IsShellMethod(method domain.MethodID) bool {
	return store.IsRoomShellMethod(method)
}

// ShelterSpotsMethod and ShelterBedsMethod name the bunk rungs' methods
// under the initial shelter goal.
func ShelterSpotsMethod() domain.MethodID { return shelterSpotsMethod }
func ShelterBedsMethod() domain.MethodID  { return shelterBedsMethod }

// ShelterClearBedsMethod names the spot deletion the bed rung runs first.
func ShelterClearBedsMethod() domain.MethodID { return shelterClearBedsMethod }

// shelterSite is one review's context for siting the initial shelter.
type shelterSite struct {
	state  ControlState
	review store.Rounds
	owner  store.WorkOwner
	facts  observation.ColonyProjection
	read   observation.ColonyReading
	check  func() error
}

// shelterBunkRecord is what the Episode already placed: the slots each
// bound bunk method names, and whether that method is still open.
type shelterBunkRecord struct {
	placed map[domain.MethodID][]shelterBunk
	open   map[domain.MethodID]bool
}

func (b shelterBunkRecord) bound(m domain.MethodID) bool { _, ok := b.placed[m]; return ok }

// anyBound reports a placement rung of the ladder already bound.
func (b shelterBunkRecord) anyBound() bool {
	return b.bound(shelterSpotsMethod) || b.bound(shelterBedrollsMethod) || b.bound(shelterBedsMethod)
}

// shelterBunks reads the bunk methods bound under the goal's epoch.
func (r *RoundsBuildingPlanner) shelterBunks(call context.Context, goal store.WorkOwner, census domain.Fact[policy.CurrentConstruction]) (shelterBunkRecord, error) {
	journal := r.reviewer.player.journal
	record := shelterBunkRecord{placed: map[domain.MethodID][]shelterBunk{}, open: map[domain.MethodID]bool{}}
	for _, method := range []domain.MethodID{shelterSpotsMethod, shelterBedrollsMethod, shelterBedsMethod, shelterClearBedrollsMethod, shelterClearBedsMethod} {
		m, err := journal.LoadOwnerMethod(call, goal, method)
		if errors.Is(err, store.ErrNotFound) {
			continue
		}
		if err != nil {
			return record, err
		}
		plan, err := journal.LoadPlan(call, m.Plan)
		if err != nil {
			return record, err
		}
		var bunks []shelterBunk
		for _, action := range plan.Spec.Actions() {
			if b, ok := action.Building(); ok {
				bunks = append(bunks, shelterBunk{b.Cell(), b.Rotation()})
			}
		}
		record.placed[method] = bunks
		record.open[method] = store.PlanWorkOpen(plan, census)
	}
	return record, nil
}

// stepShelterRoom raises the layout plan's shelter room for this review.
// The owed ring goes through one reconcile of the planned room,
// after the initial shelter's bunk rungs when no ring is begun: a bunk rung
// admitted (or refused) this review is the result. A standing ring waits on
// its roof: the window the completed wave lends, else the earlier-shell
// wait. roofingOnly is an indoor
// furnishing still waiting on its roof, which holds the rungs and the ring
// alike.
func (r *RoundsBuildingPlanner) stepShelterRoom(call, epoch context.Context, s shelterSite, roofingOnly bool) (RoundsBuildingResult, error) {
	plan, known := s.facts.LayoutPlan.Value()
	if !known {
		return RoundsBuildingResult{Verdict: noSpace("planned_shell_room")}, nil
	}
	var room policy.PlannedRoom
	planned := false
	for _, candidate := range plan.AllRooms() {
		if candidate.Role == policy.PlannedShelter {
			room, planned = candidate, true
			break
		}
	}
	if !planned {
		return RoundsBuildingResult{Verdict: noSpace("planned_shell_room")}, nil
	}
	name := string(plannedRoomMethod(room))
	ring, err := r.shelterRingMethods(call, s.owner, name)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	if _, owed := plannedRoomOwed(s.facts, policy.PlannedShelter); !owed {
		// The ring stands: nothing to raise while it waits on its roof.
		if len(ring) == 0 {
			return RoundsBuildingResult{Verdict: BuildingShellBlocked}, nil
		}
		var ticks uint32
		for _, built := range ring {
			ticks = max(ticks, shelterNativeWorkTicks(built, s.state.Snapshot, s.facts.Identity.Tick))
		}
		if err := s.check(); err != nil {
			return RoundsBuildingResult{}, err
		}
		return RoundsBuildingResult{Verdict: waitFor(WaitMethodUsed, "shelter_method"), NativeWorkTicks: ticks}, nil
	}
	if roofingOnly {
		return RoundsBuildingResult{Verdict: waitFor(WaitMethodUsed, "roofing_method")}, nil
	}
	// The bunks stand on the room once its rock is dug (the ring's reconcile
	// mines it before any wall).
	if r.phase == policy.HousingShelter && len(ring) == 0 && len(plan.RoomRock(room, s.facts.Cells).Dig) == 0 {
		if result, handled, err := r.stepShelterBunks(call, epoch, s, room); err != nil || handled {
			return result, err
		}
	}
	return r.reconcileRoom(call, epoch, s.state, s.review, s.owner, observation.RoundsReading{ColonyReading: s.read}, nil, roomReconcile{ringOnly: true, room: room, name: name, reason: "starter shelter"})
}

// shelterRingMethods are the plans of the ring's build waves bound under the
// goal's epoch, retired ones included: the record of a ring begun.
func (r *RoundsBuildingPlanner) shelterRingMethods(call context.Context, goal store.WorkOwner, name string) ([]store.PlanState, error) {
	var ring []store.PlanState
	seen := map[domain.PlanID]bool{}
	for _, m := range slices.Concat(goal.OwnerMethods(), goal.OwnerHistory()) {
		if !strings.HasPrefix(string(m.Method), name+"-build-") || seen[m.Plan] {
			continue
		}
		seen[m.Plan] = true
		plan, err := r.reviewer.player.journal.LoadPlan(call, m.Plan)
		if err != nil {
			return nil, err
		}
		ring = append(ring, plan)
	}
	return ring, nil
}

// stepShelterBunks places the initial shelter's bunk rungs on the planned
// room's slots. handled is false when the rungs have nothing more to place,
// so the ring is raised this review.
func (r *RoundsBuildingPlanner) stepShelterBunks(call, epoch context.Context, s shelterSite, room policy.PlannedRoom) (RoundsBuildingResult, bool, error) {
	record, err := r.shelterBunks(call, s.owner, s.facts.Facts.CurrentConstruction)
	if err != nil {
		return RoundsBuildingResult{}, false, err
	}
	indoor := *r
	indoor.shelter, indoor.definition = false, "SleepingSpot"
	owed, _, reason := indoor.selection(s.facts)
	if !reason.IsZero() {
		return RoundsBuildingResult{Verdict: reason}, true, nil
	}
	// The slots are shared: beds stand where the spots were placed.
	slots := record.placed[shelterSpotsMethod]
	if !record.anyBound() {
		slots = shelterBunkSlots(room, s.facts, int(owed))
		result, admitted, err := r.admitBunks(call, epoch, s, shelterSpotsMethod, "SleepingSpot", slots)
		if err != nil || admitted {
			return result, true, err
		}
	}
	// The upgrade deletes the spots and places the bed (or bedroll) on the
	// freed slots, never over a standing spot.
	definition, bunks := shelterBeds(s.facts, slots)
	if place, _ := shelterRung(definition); !record.bound(place) {
		result, err := r.upgradeBunks(call, epoch, s, record, definition, bunks)
		if err != nil || result != nil {
			if result == nil {
				result = &RoundsBuildingResult{}
			}
			return *result, true, err
		}
	}
	return RoundsBuildingResult{}, false, nil
}

// upgradeBunks swaps the standing lower-rung bunks on the bed rung's slots
// for the target definition: a sleeping spot is free and is
// deconstructed, a bedroll is packed to storage (uninstalled), each as one
// clearing method, and the bed or bedroll is placed on the freed cells once
// none stands. It returns nil to leave the rung to a later review and go on
// to the ring (nothing to place, the definition refused, a clearing that
// did not take), or the handled result: the clearing or the bunks admitted,
// or a hold while a lower rung or the clearing is still open.
func (r *RoundsBuildingPlanner) upgradeBunks(call, epoch context.Context, s shelterSite, record shelterBunkRecord, definition string, bunks []shelterBunk) (*RoundsBuildingResult, error) {
	place, clear := shelterRung(definition)
	if len(bunks) == 0 || !definitionsGate(s.facts, []string{definition}, false).IsZero() {
		return nil, nil
	}
	for m, open := range record.open {
		if open && m != place {
			return &RoundsBuildingResult{Verdict: BuildingBunksOpen}, nil
		}
	}
	obs, known := s.facts.Facts.Sleeping.Value()
	if !known {
		return nil, nil
	}
	var standing []policy.SleepingBed
	for _, b := range obs.Beds {
		if (b.Definition == policy.SleepingSpotDefinition || b.Definition == policy.SleepingBedrollDefinition) && bunkRank(string(b.Definition)) < bunkRank(definition) && onBunkSlot(b.Cell, bunks) {
			standing = append(standing, b)
		}
	}
	if len(standing) == 0 {
		result, admitted, err := r.admitBunks(call, epoch, s, place, definition, bunks)
		if err != nil || !admitted {
			return nil, err
		}
		return &result, nil
	}
	if record.bound(clear) {
		// Cleared once per Episode: a clearing that closed with pieces still
		// standing leaves the upgrade to the sleeping family.
		return nil, nil
	}
	snapshot := s.state.Snapshot
	snapshot.Plan = domain.MintPlanID()
	snapshot.Revision = 1
	actions := make([]domain.Action, 0, len(standing))
	for i, b := range standing {
		id := domain.ActionID(fmt.Sprintf("%s-%d", snapshot.Plan, i))
		var action domain.Action
		var err error
		if b.Definition == policy.SleepingSpotDefinition {
			var cut domain.Deconstruction
			if cut, err = domain.NewDeconstruction(b.ID, string(b.Definition), b.Cell); err == nil {
				action, err = domain.NewDeconstructionAction(id, cut)
			}
		} else {
			var pack domain.MoveBuilding
			if pack, err = domain.NewMoveBuilding(b.ID, string(b.Definition), b.Cell, domain.South); err == nil {
				action, err = domain.NewUninstallBuildingAction(id, pack)
			}
		}
		if err != nil {
			return nil, err
		}
		actions = append(actions, action)
	}
	plan, err := domain.NewPlan(snapshot.Plan, 1, actions)
	if err != nil {
		return nil, err
	}
	step := excavationStep{state: s.state, review: s.review, owner: s.owner, facts: s.facts, read: s.read}
	result, err := r.admitExcavation(call, epoch, step, snapshot, clear, plan, nil, policy.StockObservation{Snapshot: snapshot, Tick: s.facts.Identity.Tick}, s.check)
	if err != nil || result.Verdict != BuildingReasonAdmitted {
		return nil, err
	}
	return &result, nil
}

// onBunkSlot reports a bed's head cell inside one of the slots' cells.
func onBunkSlot(cell domain.Cell, bunks []shelterBunk) bool {
	for _, b := range bunks {
		if slices.Contains(policy.BunkCells(b.anchor, b.rot), cell) {
			return true
		}
	}
	return false
}

// admitBunks previews the bunks natively and admits the placeable ones as
// the rung's method. It reports admitted=false, without error, when the
// rung has nothing to place (no candidate, definition unavailable, none
// placeable, or the method refused whole), so the next rung is tried in
// the same review.
func (r *RoundsBuildingPlanner) admitBunks(call, epoch context.Context, s shelterSite, method domain.MethodID, definition string, bunks []shelterBunk) (RoundsBuildingResult, bool, error) {
	if len(bunks) == 0 {
		return RoundsBuildingResult{}, false, nil
	}
	if gate := definitionsGate(s.facts, []string{definition}, false); !gate.IsZero() {
		return RoundsBuildingResult{}, false, nil
	}
	stuff := bedStuff(s.facts, definition)
	snapshot := s.state.Snapshot
	snapshot.Plan = domain.MintPlanID()
	snapshot.Revision = 1
	actions := make([]domain.Action, 0, len(bunks))
	for i, bunk := range bunks {
		b, err := domain.NewBuilding(definition, bunk.anchor, bunk.rot, stuff)
		if err != nil {
			return RoundsBuildingResult{}, false, err
		}
		action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", snapshot.Plan, i)), b, domain.TierSurvive)
		if err != nil {
			return RoundsBuildingResult{}, false, err
		}
		actions = append(actions, action)
	}
	if err := s.check(); err != nil {
		return RoundsBuildingResult{}, false, err
	}
	var previews []bridge.BuildingPreview
	if batch, ok := r.native.(shellBatchPreviewer); ok {
		var err error
		if previews, _, err = batch.PreviewBuildings(call, actions, snapshot); err != nil {
			return RoundsBuildingResult{}, false, err
		}
	} else {
		for _, action := range actions {
			preview, _, err := r.native.PreviewBuilding(call, action, snapshot)
			if err != nil {
				return RoundsBuildingResult{}, false, err
			}
			previews = append(previews, preview)
		}
	}
	if len(previews) != len(actions) {
		return RoundsBuildingResult{}, false, fmt.Errorf("%w: admitBunks: len(previews) != len(actions)", ErrControl)
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
		if !canKnown || !can || !safeKnown || !safe || !footprintKnown || !sameBunkFootprint(bunks[i].rect(), footprint) {
			continue
		}
		if err := mergeRoundsStock(&stock, preview.Stock, len(selected) == 0); err != nil {
			return RoundsBuildingResult{}, false, err
		}
		selected = append(selected, v)
	}
	if len(selected) == 0 {
		return RoundsBuildingResult{}, false, nil
	}
	result, err := r.admitPreviews(call, epoch, roundsAdmission{state: s.state, review: s.review, owner: s.owner, facts: s.facts, method: method, snapshot: snapshot, selected: selected, stock: stock, purpose: policy.Rounds})
	if err != nil {
		return result, false, err
	}
	return result, result.Verdict == BuildingReasonAdmitted, nil
}

// sameBunkFootprint reports whether the native footprint is exactly the
// cells of the bunk slot's rectangle: the anchor and rotation the slot names
// took the footprint the template planned.
func sameBunkFootprint(want policy.Rectangle, footprint []domain.Cell) bool {
	if int64(len(footprint)) != int64(want.Width)*int64(want.Height) {
		return false
	}
	for _, c := range footprint {
		if c.X < want.X || c.X >= want.X+want.Width || c.Z < want.Z || c.Z >= want.Z+want.Height {
			return false
		}
	}
	return len(footprint) == 2 && footprint[0] != footprint[1]
}
