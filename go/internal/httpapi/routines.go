package httpapi

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// RoutineProvider reports which composed routine planner families this
// process wired up at startup and the durable review cursor's progress, for
// read-only runtime diagnostics. Implementations must not trigger native
// calls or mutate state.
type RoutineProvider interface {
	RoutineStatus(context.Context) (RoutineStatus, error)
}

// RoutineStatus is a runtime snapshot of the composed routine runtime.
// ActiveFamilies names every "--routine-*-plans" flag this process enabled
// (composed default or explicit), regardless of whether it currently has
// pending work; LastReviewTick is the durable review cursor's most recent
// reviewed tick, known once at least one review has run.
type RoutineStatus struct {
	ReviewsEnabled  bool
	MethodsEnabled  bool
	ActiveFamilies  []string
	LastReviewTick  domain.Tick
	LastReviewKnown bool
}

type routineStatusDTO struct {
	ReviewsEnabled bool         `json:"reviewsEnabled"`
	MethodsEnabled bool         `json:"methodsEnabled"`
	ActiveFamilies []string     `json:"activeFamilies"`
	LastReviewTick *domain.Tick `json:"lastReviewTick"`
}

func routineStatus(v RoutineStatus) routineStatusDTO {
	families := v.ActiveFamilies
	if families == nil {
		families = []string{}
	}
	result := routineStatusDTO{ReviewsEnabled: v.ReviewsEnabled, MethodsEnabled: v.MethodsEnabled, ActiveFamilies: families}
	if v.LastReviewKnown {
		tick := v.LastReviewTick
		result.LastReviewTick = &tick
	}
	return result
}
