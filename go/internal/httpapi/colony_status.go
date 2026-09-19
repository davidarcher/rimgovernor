package httpapi

import (
	"context"
	"net/http"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ColonyStatus is the read-only live colony census this route fronts
// (buildingruntime.ColonyStatus): the food stock and runway the routine
// reviewer judges from plus every living home colonist's state, so a
// sustained run's harness can sample the colony beside the goal it watches
// (issue #261). It never accepts a request body.
type ColonyStatus interface {
	Read(context.Context) (buildingruntime.ColonyStatusReport, error)
}

// colonyStatusDTO is one census; unknown facts are null. downed and
// moodMean are derived from the roster (moodMean over the colonists whose
// mood was readable, null when none was).
type colonyStatusDTO struct {
	Tick                 domain.Tick           `json:"tick"`
	RosterTick           domain.Tick           `json:"rosterTick"`
	Colonists            *int64                `json:"colonists"`
	Workers              *int64                `json:"workers"`
	FoodNutrition        *float64              `json:"foodNutrition"`
	NutritionPerDay      *float64              `json:"nutritionPerDay"`
	FoodRunwayDays       *float64              `json:"foodRunwayDays"`
	PendingFoodNutrition *float64              `json:"pendingFoodNutrition"`
	FoodCorpses          int                   `json:"foodCorpses"`
	Downed               int                   `json:"downed"`
	MoodMean             *float64              `json:"moodMean"`
	Pawns                []colonyStatusPawnDTO `json:"pawns"`
}
type colonyStatusPawnDTO struct {
	ID     domain.PawnID `json:"id"`
	Label  string        `json:"label"`
	Downed *bool         `json:"downed"`
	Mood   *float64      `json:"mood"`
	Food   *float64      `json:"food"`
}

func factPointer[T any](fact domain.Fact[T]) *T {
	v, known := fact.Value()
	if !known {
		return nil
	}
	return &v
}

func projectColonyStatus(v buildingruntime.ColonyStatusReport) colonyStatusDTO {
	out := colonyStatusDTO{
		Tick: v.Tick, RosterTick: v.RosterTick, Colonists: factPointer(v.Colonists), Workers: factPointer(v.Workers),
		FoodNutrition: factPointer(v.FoodNutrition), NutritionPerDay: factPointer(v.NutritionPerDay),
		FoodRunwayDays: factPointer(v.FoodRunwayDays), PendingFoodNutrition: factPointer(v.PendingFoodNutrition),
		FoodCorpses: v.FoodCorpses, Pawns: []colonyStatusPawnDTO{},
	}
	moodSum, moodCount := 0.0, 0
	for _, pawn := range v.Pawns {
		row := colonyStatusPawnDTO{ID: pawn.ID, Label: pawn.Label, Downed: factPointer(pawn.Downed), Mood: factPointer(pawn.Mood), Food: factPointer(pawn.Food)}
		if row.Downed != nil && *row.Downed {
			out.Downed++
		}
		if row.Mood != nil {
			moodSum += *row.Mood
			moodCount++
		}
		out.Pawns = append(out.Pawns, row)
	}
	if moodCount > 0 {
		mean := moodSum / float64(moodCount)
		out.MoodMean = &mean
	}
	return out
}

func (s *Server) handleColonyStatus(ctx context.Context, w http.ResponseWriter, r *http.Request) {
	if s.config.ColonyStatus == nil {
		s.failure(w, r, 404, "not_found", "Colony status is not enabled")
		return
	}
	report, err := s.config.ColonyStatus.Read(ctx)
	if err != nil {
		// A diagnostic read names its cause: the harness records the body
		// in the timeline sample, and "unavailable" alone cannot be acted on.
		s.failure(w, r, 503, "unavailable", "Colony status read failed: "+err.Error())
		return
	}
	s.write(w, r, 200, projectColonyStatus(report))
}
