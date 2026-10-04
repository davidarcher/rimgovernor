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
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// RoundsRepairSource reuses the generic colony read for the damaged
// structure census (cell included) and the existing tend pawn read (already
// requests combat+work+care details) for repairer eligibility: dead/downed/
// drafted/mental state, health, existing job and the Construction work
// setting. No new native call is introduced for this slice.
type RoundsRepairSource interface {
	observation.ColonySource
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	ReadTendPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
}

type RoundsRepairPlanner struct {
	reviewer *Rounder
	native   RoundsRepairSource
}
type RoundsRepairResult struct {
	Verdict
	Plan domain.PlanID
}

func NewRoundsRepairPlanner(reviewer *Rounder, native RoundsRepairSource) (*RoundsRepairPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoundsRepairPlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoundsRepairPlanner{reviewer, native}, nil
}
func (r *RoundsRepairPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsRepairResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsRepairResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0 {
		return RoundsRepairResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsRepairResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoundsRepairResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainEssentialRepairs)
	if err != nil {
		return RoundsRepairResult{}, err
	}
	// A repair the colonists already made (the goal recovered, or the
	// structure is ineligible) is settled here, before the need gate:
	// once the goal recovers the gate returns early, and the open
	// method would hold the goal's development commitment forever.
	if err = cancelSettledRepairMethods(call, p.journal, goal); err != nil {
		return RoundsRepairResult{}, err
	}
	if !workable {
		return RoundsRepairResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	// MaintainEssentialRepairs competes for the same bounded concurrent-project
	// capacity as comfort/expansion/other priority>=3 autopilot goals; only act
	// while this review's arbitration actually selected it.
	selected := false
	for _, row := range review.Development.Rows {
		selected = selected || row.Goal == policy.MaintainEssentialRepairs && row.Selected
	}
	if !selected {
		return RoundsRepairResult{Verdict: awaitingSlot(string(policy.MaintainEssentialRepairs))}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoundsRepairResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoundsRepairResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	identity, _, err := r.native.Identity(call)
	if err != nil {
		return RoundsRepairResult{}, err
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		return RoundsRepairResult{}, err
	}
	if !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoundsRepairResult{}, fmt.Errorf("%w: step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	started := r.reviewer.clock.Now()
	reading, err := r.reviewer.observeColony(call, r.native, expected, nil)
	if err != nil {
		return RoundsRepairResult{}, err
	}
	upkeepReview, err := policy.ReviewUpkeep(reading.Projection.Facts.Upkeep, policy.UpkeepHistory{}, nil)
	if err != nil {
		return RoundsRepairResult{}, err
	}
	var targetIDs []string
	for _, need := range upkeepReview.Needs {
		if need.Goal == policy.MaintainEssentialRepairs {
			targetIDs, _ = need.Targets.Value()
		}
	}
	if len(targetIDs) == 0 {
		return RoundsRepairResult{Verdict: waitFor(WaitMethodUsed, "essential_repairs")}, nil
	}
	byID := map[string]policy.UpkeepStructure{}
	if rows, known := reading.Projection.Facts.Upkeep.Structures.Value(); known {
		for _, row := range rows {
			byID[row.ID] = row
		}
	}
	var structures []policy.UpkeepStructure
	for _, id := range targetIDs {
		if structure, ok := byID[id]; ok {
			structures = append(structures, structure)
		}
	}
	if len(structures) > 8 {
		structures = structures[:8]
	}
	identityRef := boundary.Identity(state.Snapshot)
	emergency, _, err := r.native.ReadEmergency(call, identityRef)
	if err != nil {
		return RoundsRepairResult{}, err
	}
	if _, err = boundary.Context(emergency.Context, state.Snapshot); err != nil || emergency.Context.GetTick() < int64(review.Tick) {
		return RoundsRepairResult{}, fmt.Errorf("%w: step: err != nil || emergency.Context.GetTick() < int64(review.Tick)", ErrControl)
	}
	complete, known := emergency.Facts.ColonistsComplete.Value()
	if !known || !complete || len(emergency.Facts.Colonists) == 0 {
		return RoundsRepairResult{Verdict: waitFor(WaitMethodUsed, "colonist_census")}, nil
	}
	ids := make([]string, 0, len(emergency.Facts.Colonists))
	for _, pawn := range emergency.Facts.Colonists {
		ids = append(ids, string(pawn.ID))
	}
	reply, _, err := r.native.ReadTendPawns(call, identityRef, ids)
	if err != nil {
		return RoundsRepairResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoundsRepairResult{}, fmt.Errorf("%w: step: observed == nil", ErrControl)
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil {
		return RoundsRepairResult{}, fmt.Errorf("%w: step: err != nil", ErrControl)
	}
	if len(observed.Pawns) != len(ids) {
		return RoundsRepairResult{}, fmt.Errorf("%w: step: len(observed.Pawns) != len(ids)", ErrControl)
	}
	var pawns []policy.RepairCandidateFacts
	seen := map[string]bool{}
	for _, row := range observed.Pawns {
		if row == nil || row.Pawn == nil || seen[row.Pawn.GetId()] {
			return RoundsRepairResult{}, fmt.Errorf("%w: step: row == nil || row.Pawn == nil || seen[row.Pawn.GetId()]", ErrControl)
		}
		seen[row.Pawn.GetId()] = true
		pawn := domain.PawnID(row.Pawn.GetId())
		facts := repairCandidateFacts(pawn, row)
		pawns = append(pawns, facts)
	}
	structure, pawn, ok := policy.SelectRepair(structures, pawns)
	if ok && !arbiter.tryClaim([]domain.PawnID{pawn}, "repair-structure:"+structure.ID) {
		ok = false
	}
	if !ok {
		return RoundsRepairResult{Verdict: waitFor(WaitMethodUsed, "repair_pairing")}, nil
	}
	repair, err := domain.NewRepair(pawn, structure.ID, structure.Cell)
	if err != nil {
		return RoundsRepairResult{}, err
	}
	// Keyed by structure and attempt count, not pawn: a fresh attempt after an
	// interrupted or refused try picks whichever repairer is currently best.
	prefix := fmt.Sprintf("repair-%s-", structure.ID)
	attempt := medicalAttemptCount(goal.History, goal.Standard.Episode, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoundsRepairResult{Verdict: refuse(RefusalRetriesSpent, "maxMedicalAttemptsPerPatient", "")}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	id := domain.MintPlanID()
	action, err := domain.NewRepairAction(domain.ActionID(fmt.Sprintf("%s-0", id)), repair)
	if err != nil {
		return RoundsRepairResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsRepairResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsRepairResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoundsRepairResult{}, fmt.Errorf("%w: step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitMethod(call, goal.Standard.ID, goal.Revision, method, plan); err != nil {
		return RoundsRepairResult{}, err
	}
	return RoundsRepairResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}

func repairCandidateFacts(pawn domain.PawnID, row *n.PawnState) policy.RepairCandidateFacts {
	facts := policy.RepairCandidateFacts{Pawn: pawn, Dead: boundary.FactBool(row.Dead), Downed: boundary.FactBool(row.Downed), Drafted: boundary.FactBool(row.Drafted), MentalState: boundary.FactPresence(row.MentalState, row.Issues, "mental_state")}
	if row.Job != nil && !boundary.IssueField(row.Job.Issues, "player_forced") {
		facts.PlayerForced = boundary.FactBool(row.Job.PlayerForced)
	}
	if health := row.Health; health != nil && !boundary.IssueField(health.Issues, "health") {
		facts.NeedsTend, facts.Bleeding = boundary.FactBool(health.NeedsTend), boundary.FactBool(health.Bleeding)
	}
	if settings := row.Settings; settings != nil && !boundary.IssueField(settings.Issues, "work") {
		facts.ConstructionEnabled = constructionWorkEnabled(settings.Work)
	}
	return facts
}

func constructionWorkEnabled(work []*n.WorkSetting) domain.Fact[bool] {
	for _, w := range work {
		if w == nil || w.GetDefName() != "Construction" {
			continue
		}
		if w.Disabled == nil || w.Priority == nil {
			return domain.Unknown[bool]()
		}
		return domain.Known(!w.GetDisabled() && w.GetPriority() > 0)
	}
	return domain.Unknown[bool]()
}
