package buildingruntime

import (
	"context"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// routineIncident is the review's open occurrence of a Response kind
// (#1020) and the need the review assessed it at; ok is false when the
// review binds none.
func routineIncident(call context.Context, journal *store.Store, review store.RoutineReview, kind policy.ConcernID) (state store.IncidentState, need domain.NeedState, ok bool, err error) {
	binding, bound := review.Incident(kind)
	if !bound {
		return store.IncidentState{}, "", false, nil
	}
	state, err = journal.LoadIncident(call, binding.Incident)
	if err != nil {
		return store.IncidentState{}, "", false, err
	}
	return state, binding.Need, true, nil
}

// incidentDeficit is routineIncident narrowed to what a planner may
// commit to: an occurrence in deficit that no Safeguard vetoes.
func incidentDeficit(call context.Context, journal *store.Store, review store.RoutineReview, kind policy.ConcernID) (store.IncidentState, bool, error) {
	state, need, ok, err := routineIncident(call, journal, review, kind)
	if err != nil || !ok || need != domain.NeedDeficit || review.VetoIncident(state.Incident) != "" {
		return store.IncidentState{}, false, err
	}
	return state, true, nil
}

// incidentOpenWork reports whether any of the occurrence's methods still
// has open plan work.
func incidentOpenWork(call context.Context, journal *store.Store, state store.IncidentState) (bool, error) {
	for _, method := range state.Methods {
		plan, err := journal.LoadPlan(call, method.Plan)
		if err != nil {
			return false, err
		}
		if store.PlanOpen(plan) {
			return true, nil
		}
	}
	return false, nil
}

// incidentAttemptCount is medicalAttemptCount for an occurrence: each
// occurrence is its own episode.
func incidentAttemptCount(methods []store.IncidentMethod, prefix string) int {
	count := 0
	for _, m := range methods {
		if strings.HasPrefix(string(m.Method), prefix) {
			count++
		}
	}
	return count
}
