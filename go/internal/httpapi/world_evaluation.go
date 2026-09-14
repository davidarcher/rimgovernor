package httpapi

import (
	"context"
	"net/http"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// WorldEvaluation is the read-only advisory surface this route fronts. It
// never accepts a request body: unlike ClockReview, evaluate_world never
// writes anything, so there is no Acknowledge counterpart here.
type WorldEvaluation interface {
	Read(context.Context) (policy.WorldEvaluationReport, error)
}

type worldRouteDTO struct {
	DestinationMapID    int32 `json:"destinationMapId"`
	Reachable           bool  `json:"reachable"`
	EstimatedTicks      int64 `json:"estimatedTicks,string"`
	EstimatedTicksKnown bool  `json:"estimatedTicksKnown"`
}
type worldTradeRequestDTO struct {
	Resource        string `json:"resource"`
	Count           int64  `json:"count,string"`
	DestinationTile int32  `json:"destinationTile"`
}
type worldCaravanReportDTO struct {
	ID               string         `json:"id"`
	RecoveryRequired bool           `json:"recoveryRequired"`
	Healthy          bool           `json:"healthy"`
	FoodDays         float64        `json:"foodDays"`
	FoodDaysKnown    bool           `json:"foodDaysKnown"`
	ReachableHome    *worldRouteDTO `json:"reachableHome"`
	Recommendation   string         `json:"recommendation"`
}
type worldQuestReportDTO struct {
	ID                string                 `json:"id"`
	State             string                 `json:"state"`
	NativeEligible    bool                   `json:"nativeEligible"`
	ResourceDeficits  map[string]int64       `json:"resourceDeficits"`
	ResourceScope     string                 `json:"resourceScope"`
	CarriedCandidates []string               `json:"carriedCandidates"`
	Objectives        []worldTradeRequestDTO `json:"objectives"`
	Recommendation    string                 `json:"recommendation"`
}
type worldEvaluationPolicyDTO struct {
	TravelFoodMarginDays float64 `json:"travelFoodMarginDays"`
}
type worldEvaluationDTO struct {
	Readable bool                     `json:"readable"`
	Reason   string                   `json:"reason,omitempty"`
	Caravans []worldCaravanReportDTO  `json:"caravans"`
	Quests   []worldQuestReportDTO    `json:"quests"`
	Policy   worldEvaluationPolicyDTO `json:"policy"`
	Scope    string                   `json:"scope"`
}

func projectWorldRoute(v *policy.WorldEvaluationRouteFact) *worldRouteDTO {
	if v == nil {
		return nil
	}
	return &worldRouteDTO{DestinationMapID: v.DestinationMapID, Reachable: v.Reachable, EstimatedTicks: v.EstimatedTicks, EstimatedTicksKnown: v.EstimatedTicksKnown}
}
func projectWorldEvaluation(v policy.WorldEvaluationReport) worldEvaluationDTO {
	out := worldEvaluationDTO{
		Readable: v.Readable, Reason: v.Reason,
		Caravans: []worldCaravanReportDTO{}, Quests: []worldQuestReportDTO{},
		Policy: worldEvaluationPolicyDTO{TravelFoodMarginDays: v.Policy.TravelFoodMarginDays},
		Scope:  v.Scope,
	}
	for _, caravan := range v.Caravans {
		out.Caravans = append(out.Caravans, worldCaravanReportDTO{
			ID: caravan.ID, RecoveryRequired: caravan.RecoveryRequired, Healthy: caravan.Healthy,
			FoodDays: caravan.FoodDays, FoodDaysKnown: caravan.FoodDaysKnown,
			ReachableHome: projectWorldRoute(caravan.ReachableHome), Recommendation: caravan.Recommendation,
		})
	}
	for _, quest := range v.Quests {
		objectives := []worldTradeRequestDTO{}
		for _, row := range quest.Objectives {
			objectives = append(objectives, worldTradeRequestDTO{Resource: row.Resource, Count: row.Count, DestinationTile: row.DestinationTile})
		}
		deficits := quest.ResourceDeficits
		if deficits == nil {
			deficits = map[string]int64{}
		}
		carried := quest.CarriedCandidates
		if carried == nil {
			carried = []string{}
		}
		out.Quests = append(out.Quests, worldQuestReportDTO{
			ID: quest.ID, State: quest.State, NativeEligible: quest.NativeEligible,
			ResourceDeficits: deficits, ResourceScope: quest.ResourceScope,
			CarriedCandidates: carried, Objectives: objectives, Recommendation: quest.Recommendation,
		})
	}
	return out
}
func (s *Server) handleWorldEvaluation(ctx context.Context, w http.ResponseWriter, r *http.Request) {
	if s.config.WorldEvaluation == nil {
		s.failure(w, r, 404, "not_found", "World evaluation is not enabled")
		return
	}
	report, err := s.config.WorldEvaluation.Read(ctx)
	if err != nil {
		status, failure := playerFailure(err)
		s.write(w, r, status, failure)
		return
	}
	s.write(w, r, 200, projectWorldEvaluation(report))
}
