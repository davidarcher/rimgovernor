package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// RoutineNamingSource is the native colony census RoutineNamingPlanner reads
// to find the exact observed window/suggestions in the initial
// faction/settlement naming dialog, the same ReadColonyFacts call
// RoutineReviewer itself uses to raise the ConfirmColonyNames goal
// (observation.colony.go's own r.Facts.ColonyNaming derivation).
type RoutineNamingSource interface {
	ReadColonyFacts(context.Context, *c.Identity, bool) (*o.ColonyFactsReply, bridge.Result, error)
}
type RoutineNamingPlanner struct {
	reviewer *RoutineReviewer
	native   RoutineNamingSource
}
type RoutineNamingResult struct {
	Verdict
	Plan domain.PlanID
}

func NewRoutineNamingPlanner(reviewer *RoutineReviewer, native RoutineNamingSource) (*RoutineNamingPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoutineNamingPlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoutineNamingPlanner{reviewer, native}, nil
}
func (r *RoutineNamingPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineNamingResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineNamingResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineNamingResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineNamingResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineNamingResult{Verdict: BuildingReasonNoReview}, nil
	}
	incident, found, err := incidentDeficit(call, p.journal, review, policy.ConfirmColonyNames)
	if err != nil {
		return RoutineNamingResult{}, err
	}
	if !found {
		return RoutineNamingResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	if open, err := incidentOpenWork(call, p.journal, incident); err != nil || open {
		return RoutineNamingResult{Verdict: BuildingReasonExistingWork}, err
	}
	identity := boundary.Identity(state.Snapshot)
	reply, _, err := r.reviewer.colonyFacts(call, r.native, identity, false)
	if err != nil {
		return RoutineNamingResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoutineNamingResult{}, fmt.Errorf("%w: step: observed == nil", ErrControl)
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil || observed.Context.GetTick() < int64(review.Tick) {
		return RoutineNamingResult{}, fmt.Errorf("%w: step: err != nil || observed.Context.GetTick() < int64(review.Tick)", ErrControl)
	}
	naming := observed.Naming
	if naming == nil || naming.WindowId == nil || naming.FactionName == nil || naming.SettlementName == nil {
		return RoutineNamingResult{Verdict: fieldUnavailable("naming_dialog")}, nil
	}
	windowID, factionName, settlementName := naming.GetWindowId(), naming.GetFactionName(), naming.GetSettlementName()
	digestNext := sha256.Sum256([]byte(fmt.Sprintf("%d/%s/%s", windowID, factionName, settlementName)))
	method := domain.MethodID(fmt.Sprintf("naming-%x", digestNext[:16]))
	if slices.ContainsFunc(incident.Methods, func(m store.IncidentMethod) bool { return m.Method == method }) {
		return RoutineNamingResult{Verdict: BuildingReasonUsed}, nil
	}
	id := domain.MintPlanID()
	value, err := domain.NewNamingConfirmation(windowID, factionName, settlementName)
	if err != nil {
		return RoutineNamingResult{}, err
	}
	action, err := domain.NewNamingConfirmationAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
	if err != nil {
		return RoutineNamingResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineNamingResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineNamingResult{}, err
	}
	if p.session.State() != state {
		return RoutineNamingResult{}, fmt.Errorf("%w: step: p.session.State() != state", ErrControl)
	}
	if _, err = p.journal.CommitIncidentMethod(call, incident.Incident.ID, method, "", plan); err != nil {
		return RoutineNamingResult{}, err
	}
	return RoutineNamingResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}
