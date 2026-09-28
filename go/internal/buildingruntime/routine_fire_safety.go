package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// RoutineFireSafetySource is the secure-supplies read set minus the
// previews: the colony read for the fire census and the tend pawn read for
// firefighter eligibility (Firefighter work setting, health, job).
type RoutineFireSafetySource interface {
	observation.ColonySource
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	ReadTendPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
}

// fireSafetyNativeWorkTicks bounds one clock window spent letting native
// firefighting run: RimWorld's Firefighter WorkGiver cannot be ordered, so
// the controller's only method is ticks. Short windows keep the emergency
// re-evaluated (fire grew unsafe, firefighter went down) between runs.
const fireSafetyNativeWorkTicks = 600

// RoutineFireSafetyPlanner is MaintainFireSafety's method: no plan and no
// order, only a decision whether the clock may run so colonists fight a
// home fire natively (policy.EvaluateFireSafety), even one ReviewUpkeep calls
// unsafe (#715). A blocked fire (no eligible firefighter) keeps the emergency
// hold and the clock paused.
type RoutineFireSafetyPlanner struct {
	reviewer *RoutineReviewer
	native   RoutineFireSafetySource
}

type RoutineFireSafetyResult struct {
	Reason          RoutineBuildingReason
	Outcome         policy.FireSafetyOutcome
	NativeWorkTicks uint32
}

func NewRoutineFireSafetyPlanner(reviewer *RoutineReviewer, native RoutineFireSafetySource) (*RoutineFireSafetyPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoutineFireSafetyPlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoutineFireSafetyPlanner{reviewer, native}, nil
}

func (r *RoutineFireSafetyPlanner) step(call, epoch context.Context) (RoutineFireSafetyResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineFireSafetyResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0 {
		return RoutineFireSafetyResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineFireSafetyResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutineFireSafetyResult{Reason: BuildingMethodNoReview}, nil
	}
	_, workable, err := p.journal.Workable(call, review, policy.MaintainFireSafety)
	if err != nil {
		return RoutineFireSafetyResult{}, err
	}
	if !workable {
		return RoutineFireSafetyResult{Reason: BuildingMethodNoDeficit, Outcome: policy.FireSafetyRecovered}, nil
	}
	identity, _, err := r.native.Identity(call)
	if err != nil {
		return RoutineFireSafetyResult{}, err
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		return RoutineFireSafetyResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineFireSafetyResult{}, fmt.Errorf("%w: step: !routineBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	reading, err := r.reviewer.observeColony(call, r.native, expected, nil)
	if err != nil {
		return RoutineFireSafetyResult{}, err
	}
	upkeepReview, err := policy.ReviewUpkeep(reading.Projection.Facts.Upkeep, policy.UpkeepHistory{}, nil)
	if err != nil {
		return RoutineFireSafetyResult{}, err
	}
	active, known := false, false
	for _, need := range upkeepReview.Needs {
		if need.Goal == policy.MaintainFireSafety {
			active = need.Active
			_, known = need.Targets.Value()
		}
	}
	if !active {
		return RoutineFireSafetyResult{Reason: BuildingMethodNoDeficit, Outcome: policy.FireSafetyRecovered}, nil
	}
	identityRef := boundary.Identity(state.Snapshot)
	emergency, _, err := r.native.ReadEmergency(call, identityRef)
	if err != nil {
		return RoutineFireSafetyResult{}, err
	}
	if _, err = boundary.Context(emergency.Context, state.Snapshot); err != nil || emergency.Context.GetTick() < int64(review.Tick) {
		return RoutineFireSafetyResult{}, fmt.Errorf("%w: step: err != nil || emergency.Context.GetTick() < int64(review.Tick)", ErrControl)
	}
	complete, completeKnown := emergency.Facts.ColonistsComplete.Value()
	if !completeKnown || !complete || len(emergency.Facts.Colonists) == 0 {
		return RoutineFireSafetyResult{Reason: BuildingMethodUnknown, Outcome: policy.FireSafetyUnknown}, nil
	}
	ids := make([]string, 0, len(emergency.Facts.Colonists))
	for _, pawn := range emergency.Facts.Colonists {
		ids = append(ids, string(pawn.ID))
	}
	reply, _, err := r.native.ReadTendPawns(call, identityRef, ids)
	if err != nil {
		return RoutineFireSafetyResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoutineFireSafetyResult{}, fmt.Errorf("%w: step: observed == nil", ErrControl)
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil {
		return RoutineFireSafetyResult{}, fmt.Errorf("%w: step: err != nil", ErrControl)
	}
	if len(observed.Pawns) != len(ids) {
		return RoutineFireSafetyResult{}, fmt.Errorf("%w: step: len(observed.Pawns) != len(ids)", ErrControl)
	}
	var pawns []policy.FireSafetyPawnFacts
	seen := map[string]bool{}
	for _, row := range observed.Pawns {
		if row == nil || row.Pawn == nil || seen[row.Pawn.GetId()] {
			return RoutineFireSafetyResult{}, fmt.Errorf("%w: step: row == nil || row.Pawn == nil || seen[row.Pawn.GetId()]", ErrControl)
		}
		seen[row.Pawn.GetId()] = true
		pawns = append(pawns, fireSafetyPawnFacts(domain.PawnID(row.Pawn.GetId()), row))
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineFireSafetyResult{}, err
	}
	if p.session.State() != state {
		return RoutineFireSafetyResult{}, fmt.Errorf("%w: step: p.session.State() != state", ErrControl)
	}
	outcome := policy.EvaluateFireSafety(active, known, pawns)
	switch outcome {
	case policy.FireSafetyWaitingForNative:
		return RoutineFireSafetyResult{Reason: BuildingMethodExistingWork, Outcome: outcome, NativeWorkTicks: fireSafetyNativeWorkTicks}, nil
	case policy.FireSafetyUnknown:
		return RoutineFireSafetyResult{Reason: BuildingMethodUnknown, Outcome: outcome}, nil
	default:
		return RoutineFireSafetyResult{Reason: BuildingMethodRefused, Outcome: outcome}, nil
	}
}

func fireSafetyPawnFacts(pawn domain.PawnID, row *n.PawnState) policy.FireSafetyPawnFacts {
	facts := policy.FireSafetyPawnFacts{Pawn: pawn, Dead: boundary.FactBool(row.Dead), Downed: boundary.FactBool(row.Downed), Drafted: boundary.FactBool(row.Drafted), MentalState: boundary.FactPresence(row.MentalState, row.Issues, "mental_state")}
	if row.Job != nil && !boundary.IssueField(row.Job.Issues, "player_forced") {
		facts.PlayerForced = boundary.FactBool(row.Job.PlayerForced)
	}
	if health := row.Health; health != nil && !boundary.IssueField(health.Issues, "health") {
		facts.NeedsTend, facts.Bleeding = boundary.FactBool(health.NeedsTend), boundary.FactBool(health.Bleeding)
	}
	if settings := row.Settings; settings != nil && !boundary.IssueField(settings.Issues, "work") {
		facts.FirefightingEnabled = workTypeEnabled(settings.Work, "Firefighter")
	}
	return facts
}

// workTypeEnabled reports whether one named work type is enabled with a
// non-zero priority; unknown when the setting is not listed or incomplete.
func workTypeEnabled(work []*n.WorkSetting, defName string) domain.Fact[bool] {
	for _, w := range work {
		if w == nil || w.GetDefName() != defName {
			continue
		}
		if w.Disabled == nil || w.Priority == nil {
			return domain.Unknown[bool]()
		}
		return domain.Known(!w.GetDisabled() && w.GetPriority() > 0)
	}
	return domain.Unknown[bool]()
}
