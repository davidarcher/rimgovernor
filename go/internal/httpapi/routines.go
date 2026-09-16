package httpapi

import (
	"context"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// RoutineProvider reports which composed routine planner families this
// process wired up at startup and the durable review cursor's progress, for
// read-only runtime diagnostics. Implementations must not trigger native
// calls or mutate state.
type RoutineProvider interface {
	RoutineStatus(context.Context) (RoutineStatus, error)
}

// RoutineStatus is a runtime snapshot of the composed routine runtime.
// ActiveFamilies names every routine planner family this process enabled
// (composed default or explicit), regardless of whether it currently has
// pending work; LastReviewTick is the durable review cursor's most recent
// reviewed tick, known once at least one review has run. Development is the
// ranking the last review recorded, nil until a review has run.
type RoutineStatus struct {
	ReviewsEnabled  bool
	MethodsEnabled  bool
	ActiveFamilies  []string
	LastReviewTick  domain.Tick
	LastReviewKnown bool
	Development     *policy.DevelopmentState
}

type routineStatusDTO struct {
	ReviewsEnabled bool                   `json:"reviewsEnabled"`
	MethodsEnabled bool                   `json:"methodsEnabled"`
	ActiveFamilies []string               `json:"activeFamilies"`
	LastReviewTick *domain.Tick           `json:"lastReviewTick"`
	Development    *routineDevelopmentDTO `json:"development"`
}

// routineDevelopmentDTO is the recorded development ranking: bounded
// admission (capacity, labor) and every optional goal's ordering evidence and
// deferral reason. Unknown facts are null; labor rows are sorted by work type.
type routineDevelopmentDTO struct {
	Tick      domain.Tick                `json:"tick"`
	Workers   *int                       `json:"workers"`
	Labor     []routineLaborDTO          `json:"labor"`
	Capacity  int                        `json:"capacity"`
	Committed []domain.GoalID            `json:"committed"`
	Rows      []routineDevelopmentRowDTO `json:"rows"`
}
type routineLaborDTO struct {
	Work policy.WorkType `json:"work"`
	Free int             `json:"free"`
}
type routineDevelopmentRowDTO struct {
	Goal         domain.GoalID            `json:"goal"`
	Score        float64                  `json:"score"`
	Deficit      *float64                 `json:"deficit"`
	Risk         *float64                 `json:"risk"`
	WaitingSince domain.Tick              `json:"waitingSince"`
	Selected     bool                     `json:"selected"`
	Committed    bool                     `json:"committed"`
	Reason       policy.DevelopmentReason `json:"reason"`
	Bottleneck   policy.WorkType          `json:"bottleneck"`
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
	if v.Development != nil {
		dto := routineDevelopment(*v.Development)
		result.Development = &dto
	}
	return result
}

func routineDevelopment(s policy.DevelopmentState) routineDevelopmentDTO {
	dto := routineDevelopmentDTO{Tick: s.Tick, Capacity: s.Capacity, Labor: []routineLaborDTO{}, Committed: []domain.GoalID{}, Rows: []routineDevelopmentRowDTO{}}
	if v, k := s.Workers.Value(); k {
		dto.Workers = &v
	}
	if labor, k := s.Labor.Value(); k {
		for w, n := range labor {
			dto.Labor = append(dto.Labor, routineLaborDTO{Work: w, Free: n})
		}
		sort.Slice(dto.Labor, func(i, j int) bool { return dto.Labor[i].Work < dto.Labor[j].Work })
	}
	dto.Committed = append(dto.Committed, s.Committed...)
	for _, row := range s.Rows {
		v := routineDevelopmentRowDTO{Goal: row.Goal, Score: row.Score, WaitingSince: row.WaitingSince, Selected: row.Selected, Committed: row.Committed, Reason: row.Reason, Bottleneck: row.Bottleneck}
		if d, k := row.Deficit.Value(); k {
			v.Deficit = &d
		}
		if r, k := row.Risk.Value(); k {
			v.Risk = &r
		}
		dto.Rows = append(dto.Rows, v)
	}
	return dto
}
