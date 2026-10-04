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

// RoundsNamingSource is the native colony census RoundsNamingPlanner reads
// to find the exact observed window/suggestions in the initial
// faction/settlement naming dialog, the same ReadColonyFacts call
// Rounder itself uses to raise the ConfirmColonyNames goal
// (observation.colony.go's own r.Facts.ColonyNaming derivation).
type RoundsNamingSource interface {
	ReadColonyFacts(context.Context, *c.Identity, bool) (*o.ColonyFactsReply, bridge.Result, error)
}
type RoundsNamingPlanner struct {
	reviewer *Rounder
	native   RoundsNamingSource
}
type RoundsNamingResult struct {
	Verdict
	Plan domain.PlanID
}

func NewRoundsNamingPlanner(reviewer *Rounder, native RoundsNamingSource) (*RoundsNamingPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoundsNamingPlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoundsNamingPlanner{reviewer, native}, nil
}
func (r *RoundsNamingPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsNamingResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsNamingResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoundsNamingResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsNamingResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoundsNamingResult{Verdict: BuildingReasonNoReview}, nil
	}
	incident, found, err := incidentDeficit(call, p.journal, review, policy.ConfirmColonyNames)
	if err != nil {
		return RoundsNamingResult{}, err
	}
	if !found {
		return RoundsNamingResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	if open, err := incidentOpenWork(call, p.journal, incident); err != nil || open {
		return RoundsNamingResult{Verdict: BuildingReasonExistingWork}, err
	}
	identity := boundary.Identity(state.Snapshot)
	reply, _, err := r.reviewer.colonyFacts(call, r.native, identity, false)
	if err != nil {
		return RoundsNamingResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoundsNamingResult{}, fmt.Errorf("%w: step: observed == nil", ErrControl)
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil || observed.Context.GetTick() < int64(review.Tick) {
		return RoundsNamingResult{}, fmt.Errorf("%w: step: err != nil || observed.Context.GetTick() < int64(review.Tick)", ErrControl)
	}
	naming := observed.Naming
	if naming == nil || naming.WindowId == nil || naming.FactionName == nil || naming.SettlementName == nil {
		return RoundsNamingResult{Verdict: fieldUnavailable("naming_dialog")}, nil
	}
	windowID, factionName, settlementName := naming.GetWindowId(), naming.GetFactionName(), naming.GetSettlementName()
	digestNext := sha256.Sum256([]byte(fmt.Sprintf("%d/%s/%s", windowID, factionName, settlementName)))
	method := domain.MethodID(fmt.Sprintf("naming-%x", digestNext[:16]))
	if slices.ContainsFunc(incident.Methods, func(m store.IncidentMethod) bool { return m.Method == method }) {
		return RoundsNamingResult{Verdict: waitFor(WaitMethodUsed, "naming_method")}, nil
	}
	id := domain.MintPlanID()
	value, err := domain.NewNamingConfirmation(windowID, factionName, settlementName)
	if err != nil {
		return RoundsNamingResult{}, err
	}
	action, err := domain.NewNamingConfirmationAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
	if err != nil {
		return RoundsNamingResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsNamingResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsNamingResult{}, err
	}
	if p.session.State() != state {
		return RoundsNamingResult{}, fmt.Errorf("%w: step: p.session.State() != state", ErrControl)
	}
	if _, err = p.journal.CommitIncidentMethod(call, incident.Incident.ID, method, "", plan); err != nil {
		return RoundsNamingResult{}, err
	}
	return RoundsNamingResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}
