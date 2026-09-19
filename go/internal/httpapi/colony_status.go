package httpapi

import (
	"context"
	"net/http"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
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
// mood was readable, null when none was). raidPoints and the wealth split
// are the census's threat section (#395).
type colonyStatusDTO struct {
	FoodPlan             *foodPlanDTO          `json:"foodPlan"`
	FoodPlanTick         *domain.Tick          `json:"foodPlanTick"`
	Tick                 domain.Tick           `json:"tick"`
	RosterTick           domain.Tick           `json:"rosterTick"`
	Colonists            *int64                `json:"colonists"`
	Workers              *int64                `json:"workers"`
	FoodNutrition        *float64              `json:"foodNutrition"`
	NutritionPerDay      *float64              `json:"nutritionPerDay"`
	FoodRunwayDays       *float64              `json:"foodRunwayDays"`
	PendingFoodNutrition *float64              `json:"pendingFoodNutrition"`
	FoodCorpses          int                   `json:"foodCorpses"`
	RaidPoints           *float64              `json:"raidPoints"`
	WealthTotal          *float64              `json:"wealthTotal"`
	WealthItems          *float64              `json:"wealthItems"`
	WealthBuildings      *float64              `json:"wealthBuildings"`
	WealthPawns          *float64              `json:"wealthPawns"`
	Shrines              []colonyShrineDTO     `json:"shrines"`
	Downed               int                   `json:"downed"`
	MoodMean             *float64              `json:"moodMean"`
	Pawns                []colonyStatusPawnDTO `json:"pawns"`
}

// colonyShrineDTO is one ancient shrine (#456): the casket group, whether
// its room is still sealed and, once seen inside, whether guards survive.
type colonyShrineDTO struct {
	ID            string `json:"id"`
	Sealed        bool   `json:"sealed"`
	InHome        bool   `json:"inHome"`
	Caskets       int    `json:"caskets"`
	FilledCaskets int    `json:"filledCaskets"`
	GuardsKnown   bool   `json:"guardsKnown"`
	GuardsAlive   bool   `json:"guardsAlive"`
	BreachWalls   int    `json:"breachWalls"`
	// Ready and Reason are the breach judgement (#457); Reason is empty when
	// ready and null when the judgement was not made.
	Ready  *bool   `json:"ready"`
	Reason *string `json:"reason"`
	Wall   *string `json:"wall"`
	Squad  int     `json:"squad"`
	Traps  int     `json:"traps"`
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
		FoodCorpses: v.FoodCorpses, RaidPoints: factPointer(v.Threat.RaidPoints), WealthTotal: factPointer(v.Threat.WealthTotal),
		WealthItems: factPointer(v.Threat.WealthItems), WealthBuildings: factPointer(v.Threat.WealthBuildings), WealthPawns: factPointer(v.Threat.WealthPawns),
		Pawns: []colonyStatusPawnDTO{},
	}
	out.FoodPlanTick = factPointer(v.FoodPlanTick)
	if shrines, known := v.Shrines.Value(); known {
		out.Shrines = []colonyShrineDTO{}
		for _, shrine := range shrines {
			row := colonyShrineDTO{ID: shrine.ID, Sealed: shrine.Sealed, InHome: shrine.InHome, Caskets: len(shrine.Caskets), FilledCaskets: shrine.FilledCaskets(), GuardsKnown: shrine.GuardsKnown, GuardsAlive: shrine.GuardsAlive(), BreachWalls: len(shrine.BreachWalls)}
			for _, judged := range v.ShrineReadiness {
				if judged.Shrine != shrine.ID {
					continue
				}
				ready, reason, wall := judged.Readiness.Ready, judged.Readiness.Reason, judged.Readiness.Wall.EntityID
				row.Ready, row.Reason, row.Squad, row.Traps = &ready, &reason, len(judged.Readiness.Squad), judged.Readiness.Traps
				if wall != "" {
					row.Wall = &wall
				}
			}
			out.Shrines = append(out.Shrines, row)
		}
	}
	if plan, known := v.FoodPlan.Value(); known {
		out.FoodPlan = projectFoodPlan(plan)
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

type foodPlanDTO struct {
	Portfolio       []foodPlanEntryDTO `json:"portfolio"`
	Unknown         []foodPlanEntryDTO `json:"unknown"`
	DeliveredPerDay float64            `json:"deliveredPerDay"`
	DemandPerDay    float64            `json:"demandPerDay"`
	GapPerDay       float64            `json:"gapPerDay"`
	Explain         string             `json:"explain"`
}

type foodPlanEntryDTO struct {
	Kind            policy.FoodChannelKind  `json:"kind"`
	ID              string                  `json:"id"`
	Decision        policy.FoodPlanDecision `json:"decision"`
	Reason          string                  `json:"reason"`
	NutritionPerDay *float64                `json:"nutritionPerDay"`
	WorkPerDay      *float64                `json:"workPerDay"`
	LeadDays        *float64                `json:"leadDays"`
	Open            *bool                   `json:"open"`
	DeliveredPerDay float64                 `json:"deliveredPerDay"`
	Terms           []policy.FoodPlanTerm   `json:"terms"`
}

func projectFoodPlan(p policy.FoodPlan) *foodPlanDTO {
	project := func(rows []policy.FoodPlanEntry) []foodPlanEntryDTO {
		out := make([]foodPlanEntryDTO, 0, len(rows))
		for _, row := range rows {
			c := row.Channel
			out = append(out, foodPlanEntryDTO{Kind: c.Kind, ID: c.ID, Decision: row.Decision, Reason: row.Reason,
				NutritionPerDay: factPointer(c.NutritionPerDay), WorkPerDay: factPointer(c.WorkPerDay), LeadDays: factPointer(c.LeadDays),
				Open: factPointer(c.Open), DeliveredPerDay: row.DeliveredPerDay, Terms: row.Terms})
		}
		return out
	}
	return &foodPlanDTO{Portfolio: project(p.Portfolio), Unknown: project(p.Unknown), DeliveredPerDay: p.DeliveredPerDay, DemandPerDay: p.DemandPerDay, GapPerDay: p.GapPerDay, Explain: p.Explain()}
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
