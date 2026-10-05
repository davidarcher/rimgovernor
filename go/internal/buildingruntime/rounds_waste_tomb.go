package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// Staging the tomb (#832): MaintainWaste raises the planned tomb and
// places one sarcophagus at a time while a dead colonist has none waiting
// (policy.NextTombStep). With every tomb full the layout review grows
// another; a plain grave stands in when no sarcophagus can be had (#857).
// Vanilla haulers inter the body.

// tombMethod names a tomb step's method: the shell once per room, each
// sarcophagus slot once, per Episode.
func tombMethod(step policy.TombStep) domain.MethodID {
	if step.Kind == policy.TombPlace {
		return domain.MethodID(fmt.Sprintf("tomb-place-%d-%d-%s", step.Room.Interior.X, step.Room.Interior.Z, step.Piece.Slot))
	}
	return domain.MethodID(fmt.Sprintf("tomb-shell-%d-%d", step.Room.Interior.X, step.Room.Interior.Z))
}

// stageTomb answers a due tomb step; handled is false when none is due.
func (r *RoundsWastePlanner) stageTomb(call, epoch context.Context, state ControlState, review store.Rounds, goal store.StandardState, arbiter *stepArbiter, expected observation.Identity) (RoundsWasteResult, bool, error) {
	source, ok := r.native.(observation.RoundsSource)
	if !ok || r.building == nil {
		return RoundsWasteResult{}, false, nil
	}
	reading, err := r.reviewer.observeRooms(call, source, expected, domain.Unknown[[]policy.ConstructionClaim](), wasteDefinitions...)
	if err != nil {
		return RoundsWasteResult{}, false, err
	}
	step := tombStep(reading.Projection)
	if step.Kind == policy.TombNone {
		if morgue, owed := plannedMorgue(reading.Projection); owed {
			clockSchedulerLog("%s: morgue shell for a waiting stranger corpse", goal.OwnerID())
			result, err := r.building.shellRoom(call, epoch, state, review, goal, reading.ColonyReading, morgue, plannedRoomMethod(morgue), "")
			// A shell already tried this epoch, or refused, leaves the
			// burn to go on.
			if err != nil || !result.Verdict.skipsToPlacement() {
				return RoundsWasteResult{Verdict: result.Verdict}, true, err
			}
		}
		return r.stageDisposal(call, epoch, state, review, goal, arbiter, reading)
	}
	clockSchedulerLog("%s: tomb %s (dead %d, empty %d)", goal.OwnerID(), step.Kind, step.Dead, step.Empty)
	var result RoundsBuildingResult
	switch step.Kind {
	case policy.TombShell:
		result, err = r.building.shellRoom(call, epoch, state, review, goal, reading.ColonyReading, step.Room, tombMethod(step), "")
	case policy.TombPlace:
		result, err = r.building.placePiece(call, epoch, state, review, goal, reading, step.Piece, tombMethod(step))
	case policy.TombFull:
		// The layout review grows another tomb; a grave only once a
		// replan found no room for one; the burn goes on meanwhile.
		if !r.reviewer.tombGrowthRefused(reading.Projection.Identity.Tick) {
			return r.stageDisposal(call, epoch, state, review, goal, arbiter, reading)
		}
		result, err = r.placeGrave(call, epoch, state, review, goal, reading, step)
	case policy.TombGrave:
		result, err = r.placeGrave(call, epoch, state, review, goal, reading, step)
	}
	return RoundsWasteResult{Verdict: result.Verdict}, true, err
}

// graveSiteTries bounds the free sites a grave previews.
const graveSiteTries = 4

// placeGrave places a plain grave (#857) on the free 1x2 site nearest the
// fields anchor, trying the next site while native refuses one.
func (r *RoundsWastePlanner) placeGrave(call, epoch context.Context, state ControlState, review store.Rounds, goal store.WorkOwner, reading observation.RoundsReading, step policy.TombStep) (RoundsBuildingResult, error) {
	facts := reading.Projection
	anchor, planned := fieldAnchor(facts)
	if !planned {
		return RoundsBuildingResult{Verdict: BuildingNoLayoutPlan}, nil
	}
	sites, err := policy.FreeSites(policy.PenEnclosureRequest{Bounds: facts.Bounds, Anchor: anchor, Cells: facts.Cells, Protected: nil}, 1, 2)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	method := domain.MethodID(fmt.Sprintf("tomb-grave-%d", step.Graves))
	result := RoundsBuildingResult{Verdict: noSpace("grave_site")}
	for i, site := range sites {
		if i == graveSiteTries {
			break
		}
		piece := policy.NewInteriorPiece("grave", policy.GraveDefinition, domain.Cell{X: 1, Z: 2}, domain.North, domain.Cell{X: site.X, Z: site.Z})
		if result, err = r.building.placePiece(call, epoch, state, review, goal, reading, piece, method); err != nil || !result.Verdict.Is(RefusalNoSpace) {
			return result, err
		}
	}
	return result, nil
}

// placePiece previews and admits one interior piece: an interior piece (a
// sarcophagus, bed, throne...).
func (b *RoundsBuildingPlanner) placePiece(call, epoch context.Context, state ControlState, review store.Rounds, goal store.WorkOwner, reading observation.RoundsReading, piece policy.InteriorPiece, method domain.MethodID) (RoundsBuildingResult, error) {
	p := b.reviewer.player
	facts := reading.Projection
	if _, err := p.journal.LoadOwnerMethod(call, goal, method); err == nil {
		return RoundsBuildingResult{Verdict: waitFor(WaitMethodUsed, "interior_piece")}, nil
	}
	stuff := facts.BuildStuff(piece.Def)
	snapshot := state.Snapshot
	snapshot.Plan = domain.MintPlanID()
	snapshot.Revision = 1
	check := func() error {
		if err := p.current(call, epoch); err != nil {
			return err
		}
		if p.session.State() != state {
			return fmt.Errorf("%w: placePiece: p.session.State() != state", ErrControl)
		}
		return nil
	}
	if err := check(); err != nil {
		return RoundsBuildingResult{}, err
	}
	building, err := domain.NewBuilding(piece.Def, piece.Anchor(), piece.Rot, stuff)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-0", snapshot.Plan)), building)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	preview, _, err := b.native.PreviewBuilding(call, action, snapshot)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	v := preview.Preview
	can, ck := v.CanPlace.Value()
	safe, sk := v.SafeToPlace.Value()
	if !ck || !can || !sk || !safe {
		clockSchedulerLog("%s: %s %s refused at %d,%d", goal.OwnerID(), piece.Def, piece.Slot, piece.Anchor().X, piece.Anchor().Z)
		return RoundsBuildingResult{Verdict: noSpace(piece.Slot)}, nil
	}
	stock := policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}
	if err := mergeRoundsStock(&stock, preview.Stock, true); err != nil {
		return RoundsBuildingResult{}, err
	}
	return b.admitPreviews(call, epoch, roundsAdmission{state: state, review: review, owner: goal, facts: facts, method: method, snapshot: snapshot, selected: []policy.Preview{v}, stock: stock, purpose: policy.Shelter})
}
