package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// Burning the incinerator (#1816): once the room holds a batch
// (policy.BurnStoredCells interior cells in use) MaintainIncineration picks a
// burner, checks a firefighting standby, and commits one plan of equip (a
// loose molotov, unless the burner holds one), the plan's draft and the
// ignite at the room's centre. The burner is drafted and armed with the
// same equip and draft actions the fight loadout uses; the combat planner's
// undraft releases it when the plan settles, and the equip planner re-arms
// it. A burned room leaves ash, which the planner then has cleaned with the
// ordinary Clean action. Nothing here extends the wire: ignite (#1815)
// already refuses an occupied room natively.

// incineratorStored counts the interior cells holding things.
func incineratorStored(facts observation.ColonyProjection, interior policy.Rectangle) int {
	in := map[domain.Cell]bool{}
	for _, c := range policy.RectangleCells(interior) {
		in[c] = true
	}
	stored := 0
	for _, cell := range facts.Cells {
		if empty, known := cell.StorageEmpty.Value(); in[cell.Cell] && known && !empty {
			stored++
		}
	}
	return stored
}

// fireBurning is false only when the fire census is read and empty.
func fireBurning(facts observation.ColonyProjection) bool {
	fires, known := facts.Facts.Upkeep.Fires.Value()
	return !known || len(fires) > 0
}

// stageBurn answers a due ash cleanup or burn; handled is false when
// neither is due.
func (r *RoundsIncinerationPlanner) stageBurn(call, epoch context.Context, state ControlState, review store.Rounds, goal store.StandardState, arbiter *stepArbiter, reading observation.RoundsReading) (RoundsIncinerationResult, bool, error) {
	room := standingIncinerator(reading.Projection)
	if room == nil {
		return RoundsIncinerationResult{}, false, nil
	}
	filth, _ := reading.Projection.Facts.Upkeep.Filth.Value()
	ash := policy.IncineratorAsh(filth, room.Interior)
	burning := fireBurning(reading.Projection)
	stored := incineratorStored(reading.Projection, room.Interior)
	cleaning := len(ash) > 0 && !burning
	if !cleaning && (stored < policy.BurnStoredCells || burning) {
		return RoundsIncinerationResult{}, false, nil
	}
	rows, ok, err := colonistRows(call, r.native, state, review)
	if err != nil {
		return RoundsIncinerationResult{}, true, err
	}
	if !ok {
		return RoundsIncinerationResult{Verdict: waitFor(WaitMethodUsed, "colonist_rows")}, true, nil
	}
	if cleaning {
		return r.cleanAsh(call, epoch, state, goal, arbiter, ash, rows)
	}
	return r.burnRoom(call, epoch, state, goal, arbiter, *room, stored, rows)
}

// cleanAsh sends the lowest-ID eligible cleaner at the first ash.
func (r *RoundsIncinerationPlanner) cleanAsh(call, epoch context.Context, state ControlState, goal store.StandardState, arbiter *stepArbiter, ash []policy.UpkeepFilth, rows []*n.PawnState) (RoundsIncinerationResult, bool, error) {
	var pawns []policy.CleanCandidateFacts
	for _, row := range rows {
		if row == nil || row.Pawn == nil {
			return RoundsIncinerationResult{}, true, fmt.Errorf("%w: cleanAsh: row == nil || row.Pawn == nil", ErrControl)
		}
		pawns = append(pawns, cleanCandidateFacts(domain.PawnID(row.Pawn.GetId()), row))
	}
	target, pawn, ok := policy.SelectClean(ash, pawns)
	if ok && !arbiter.tryClaim([]domain.PawnID{pawn}, "clean-target:"+target.ID) {
		ok = false
	}
	if !ok {
		return RoundsIncinerationResult{Verdict: waitFor(WaitMethodUsed, "clean_target")}, true, nil
	}
	clean, err := domain.NewClean(pawn, target.ID, target.Cell)
	if err != nil {
		return RoundsIncinerationResult{}, true, err
	}
	prefix := fmt.Sprintf("burn-ash-%s-", target.ID)
	attempt := medicalAttemptCount(goal.History, goal.Standard.Episode, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoundsIncinerationResult{Verdict: refuse(RefusalRetriesSpent, "maxMedicalAttemptsPerPatient", "")}, true, nil
	}
	id := domain.MintPlanID()
	action, err := domain.NewCleanAction(domain.ActionID(fmt.Sprintf("%s-0", id)), clean)
	if err != nil {
		return RoundsIncinerationResult{}, true, err
	}
	return r.commitBurnPlan(call, epoch, state, goal, domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt)), id, []domain.Action{action}, nil)
}

// burnRoom plans one burn of a full incinerator.
func (r *RoundsIncinerationPlanner) burnRoom(call, epoch context.Context, state ControlState, goal store.StandardState, arbiter *stepArbiter, room policy.PlannedRoom, stored int, rows []*n.PawnState) (RoundsIncinerationResult, bool, error) {
	identity := boundary.Identity(state.Snapshot)
	things, err := frameThings(call, r.native, identity)
	if err != nil {
		return RoundsIncinerationResult{}, true, err
	}
	catalog, err := pawnCatalog(call, r.native, identity)
	if err != nil {
		return RoundsIncinerationResult{}, true, err
	}
	request := policy.BurnRequest{Interior: room.Interior, Stored: stored}
	for _, row := range rows {
		if row == nil || row.Pawn == nil {
			return RoundsIncinerationResult{}, true, fmt.Errorf("%w: burnRoom: row == nil || row.Pawn == nil", ErrControl)
		}
		pawn := domain.PawnID(row.Pawn.GetId())
		arm, err := equipCandidatePawnFacts(row, catalog, things)
		if err != nil {
			return RoundsIncinerationResult{}, true, err
		}
		request.Pawns = append(request.Pawns, policy.BurnCandidate{Arm: arm, Fire: fireSafetyPawnFacts(pawn, row), Holds: primaryDef(row, things) == policy.MolotovDef})
	}
	if source, ok := r.native.(loadoutWeaponSource); ok {
		bounds, _, err := source.ReadMapBounds(call, identity, domain.Cell{})
		if err != nil {
			return RoundsIncinerationResult{}, true, err
		}
		if _, err = boundary.Context(bounds.Context, state.Snapshot); err != nil || bounds.Bounds.Width <= 0 || bounds.Bounds.Height <= 0 {
			return RoundsIncinerationResult{}, true, fmt.Errorf("%w: burnRoom: bounds", ErrControl)
		}
		read, _, err := source.ReadEquipWeapons(call, identity, domain.Cell{}, domain.Cell{X: bounds.Bounds.Width - 1, Z: bounds.Bounds.Height - 1})
		if err != nil {
			return RoundsIncinerationResult{}, true, err
		}
		if _, err = boundary.Context(read.Context, state.Snapshot); err != nil {
			return RoundsIncinerationResult{}, true, fmt.Errorf("%w: burnRoom: context", ErrControl)
		}
		request.Molotovs = molotovs(read.Targets)
	}
	order, verdict := policy.PlanBurn(request)
	if verdict != policy.BurnReady {
		return RoundsIncinerationResult{Verdict: refuse(RefusalNoWorker, "incinerator_"+string(verdict), "")}, true, nil
	}
	var claims []string
	if order.Molotov != nil {
		claims = append(claims, "equip-weapon:"+order.Molotov.Thing)
	}
	if !arbiter.tryClaim([]domain.PawnID{order.Burner}, claims...) {
		return RoundsIncinerationResult{Verdict: waitFor(WaitMethodUsed, "burn_claim")}, true, nil
	}
	id := domain.MintPlanID()
	var actions []domain.Action
	var dependencies []domain.ActionDependency
	var previous domain.ActionID
	chain := func(action domain.Action) {
		if previous != "" {
			dependencies = append(dependencies, domain.ActionDependency{Action: action.ID(), Requires: previous})
		}
		previous = action.ID()
		actions = append(actions, action)
	}
	if order.Molotov != nil {
		equip, err := domain.NewEquip(order.Burner, order.Molotov.Thing, order.Molotov.Definition, order.Molotov.Cell)
		if err != nil {
			return RoundsIncinerationResult{}, true, err
		}
		action, err := domain.NewEquipAction(domain.ActionID(fmt.Sprintf("%s-equip", id)), equip)
		if err != nil {
			return RoundsIncinerationResult{}, true, err
		}
		chain(action)
	}
	draft, err := domain.NewOwnedDraft(order.Burner)
	if err != nil {
		return RoundsIncinerationResult{}, true, err
	}
	draftAction, err := domain.NewOwnedDraftAction(domain.ActionID(fmt.Sprintf("%s-draft", id)), draft)
	if err != nil {
		return RoundsIncinerationResult{}, true, err
	}
	chain(draftAction)
	ignite, err := domain.NewIgnite(order.Burner, order.Target)
	if err != nil {
		return RoundsIncinerationResult{}, true, err
	}
	igniteAction, err := domain.NewIgniteAction(domain.ActionID(fmt.Sprintf("%s-ignite", id)), ignite)
	if err != nil {
		return RoundsIncinerationResult{}, true, err
	}
	chain(igniteAction)
	return r.commitBurnPlan(call, epoch, state, goal, nextWaveMethod(goal, "burn-wave-"), id, actions, dependencies)
}

// molotovs are the loose molotovs among the map's loose weapons.
func molotovs(targets []bridge.EquipCandidate) []policy.EquipCandidateWeapon {
	var out []policy.EquipCandidateWeapon
	for _, w := range targets {
		if w.Definition == policy.MolotovDef {
			out = append(out, policy.EquipCandidateWeapon{Thing: w.Thing, Definition: w.Definition, Cell: w.Cell, BiocodedTo: w.BiocodedTo, Biocoded: w.Biocoded})
		}
	}
	return out
}

func (r *RoundsIncinerationPlanner) commitBurnPlan(call, epoch context.Context, state ControlState, goal store.StandardState, method domain.MethodID, id domain.PlanID, actions []domain.Action, dependencies []domain.ActionDependency) (RoundsIncinerationResult, bool, error) {
	p := r.reviewer.player
	plan, err := domain.NewPlan(id, 1, actions, dependencies...)
	if err != nil {
		return RoundsIncinerationResult{}, true, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsIncinerationResult{}, true, err
	}
	if p.session.State() != state {
		return RoundsIncinerationResult{}, true, fmt.Errorf("%w: commitBurnPlan: p.session.State() != state", ErrControl)
	}
	if _, err = p.journal.CommitMethod(call, goal.Standard.ID, goal.Revision, method, plan); err != nil {
		return RoundsIncinerationResult{}, true, err
	}
	return RoundsIncinerationResult{Verdict: BuildingReasonAdmitted, Plan: id}, true, nil
}
