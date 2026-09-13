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
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// RoutineRepairSource reuses the generic colony read for the damaged
// structure census (cell included) and the existing tend pawn read (already
// requests combat+work+care details) for repairer eligibility: dead/downed/
// drafted/mental state, health, existing job and the Construction work
// setting. No new native call is introduced for this slice.
type RoutineRepairSource interface {
	observation.ColonySource
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	ReadTendPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
}

type RoutineRepairPlanner struct {
	reviewer *RoutineReviewer
	native   RoutineRepairSource
}
type RoutineRepairResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

func NewRoutineRepairPlanner(reviewer *RoutineReviewer, native RoutineRepairSource) (*RoutineRepairPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	return &RoutineRepairPlanner{reviewer, native}, nil
}
func (r *RoutineRepairPlanner) Step(ctx context.Context) (RoutineRepairResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineRepairResult{}, err
	}
	defer done()
	return r.step(call, epoch)
}
func (r *RoutineRepairPlanner) step(call, epoch context.Context) (RoutineRepairResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineRepairResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0 {
		return RoutineRepairResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineRepairResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutineRepairResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainEssentialRepairs {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutineRepairResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineRepairResult{Reason: BuildingMethodNoDeficit}, nil
	}
	// MaintainEssentialRepairs competes for the same bounded concurrent-project
	// capacity as comfort/expansion/other priority>=3 autopilot goals; only act
	// while this review's arbitration actually selected it.
	selected := false
	for _, row := range review.Development.Rows {
		selected = selected || row.Goal == policy.MaintainEssentialRepairs && row.Selected
	}
	if !selected {
		return RoutineRepairResult{Reason: BuildingMethodRefused}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineRepairResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineRepairResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	identity, _, err := r.native.Identity(call)
	if err != nil {
		return RoutineRepairResult{}, err
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		return RoutineRepairResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineRepairResult{}, ErrControl
	}
	started := r.reviewer.clock.Now()
	reading, err := observation.ObserveColony(call, r.native, r.reviewer.clock, expected, r.reviewer.maxAge, true, nil)
	if err != nil {
		return RoutineRepairResult{}, err
	}
	upkeepReview, err := policy.ReviewUpkeep(reading.Projection.Facts.Upkeep, policy.UpkeepHistory{}, nil)
	if err != nil {
		return RoutineRepairResult{}, err
	}
	var targetIDs []string
	for _, need := range upkeepReview.Needs {
		if need.Goal == policy.MaintainEssentialRepairs {
			targetIDs, _ = need.Targets.Value()
		}
	}
	if len(targetIDs) == 0 {
		return RoutineRepairResult{Reason: BuildingMethodUsed}, nil
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
		return RoutineRepairResult{}, err
	}
	if _, err = boundary.Context(emergency.Context, state.Snapshot); err != nil || emergency.Context.GetTick() < int64(review.Tick) {
		return RoutineRepairResult{}, ErrControl
	}
	complete, known := emergency.Facts.ColonistsComplete.Value()
	if !known || !complete || len(emergency.Facts.Colonists) == 0 {
		return RoutineRepairResult{Reason: BuildingMethodUsed}, nil
	}
	ids := make([]string, 0, len(emergency.Facts.Colonists))
	for _, pawn := range emergency.Facts.Colonists {
		ids = append(ids, string(pawn.ID))
	}
	reply, _, err := r.native.ReadTendPawns(call, identityRef, ids)
	if err != nil {
		return RoutineRepairResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoutineRepairResult{}, ErrControl
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil {
		return RoutineRepairResult{}, ErrControl
	}
	counts := observed.Completeness
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" || counts.Matched == nil || counts.Returned == nil || counts.Unreadable == nil || counts.GetUnreadable() != 0 || counts.GetMatched() != uint64(len(ids)) || counts.GetReturned() != uint64(len(ids)) || len(observed.Pawns) != len(ids) {
		return RoutineRepairResult{}, ErrControl
	}
	preferences, loadErr := p.journal.LoadWorkPreferences(call, state.Snapshot.Plan)
	if loadErr != nil && !errors.Is(loadErr, store.ErrNotFound) {
		return RoutineRepairResult{}, loadErr
	}
	if preferences.Revision != review.WorkPreferenceRevision {
		return RoutineRepairResult{}, ErrControl
	}
	overridden := map[domain.PawnID]bool{}
	for _, o := range preferences.Overrides {
		if o.Work == policy.WorkType("Construction") && o.Priority == 0 {
			overridden[domain.PawnID(o.Pawn)] = true
		}
	}
	var pawns []policy.RepairCandidateFacts
	seen := map[string]bool{}
	for _, row := range observed.Pawns {
		if row == nil || row.Pawn == nil || seen[row.Pawn.GetId()] {
			return RoutineRepairResult{}, ErrControl
		}
		seen[row.Pawn.GetId()] = true
		pawn := domain.PawnID(row.Pawn.GetId())
		facts := repairCandidateFacts(pawn, row)
		if overridden[pawn] {
			facts.ConstructionEnabled = domain.Known(false)
		}
		pawns = append(pawns, facts)
	}
	structure, pawn, ok := policy.SelectRepair(structures, pawns)
	if !ok {
		return RoutineRepairResult{Reason: BuildingMethodUsed}, nil
	}
	repair, err := domain.NewRepair(pawn, structure.ID, structure.Cell)
	if err != nil {
		return RoutineRepairResult{}, err
	}
	// Keyed by structure and attempt count, not pawn: a fresh attempt after an
	// interrupted or refused try picks whichever repairer is currently best.
	prefix := fmt.Sprintf("repair-%s-", structure.ID)
	attempt := medicalAttemptCount(goal.Methods, goal.Goal.Epoch, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoutineRepairResult{Reason: BuildingMethodExhausted}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-repair-%x", digest[:16]))
	action, err := domain.NewRepairAction(domain.ActionID(fmt.Sprintf("%s-0", id)), repair)
	if err != nil {
		return RoutineRepairResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineRepairResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineRepairResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineRepairResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineRepairResult{}, err
	}
	return RoutineRepairResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
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
