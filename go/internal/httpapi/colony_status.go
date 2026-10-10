package httpapi

import (
	"context"
	"net/http"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// ColonyStatus is the read-only live colony census this route fronts
// (buildingruntime.ColonyStatus): the food stock and runway the routine
// reviewer judges from plus every living home colonist's state, so a
// sustained run's harness can sample the colony beside the concern it watches.
// It never accepts a request body.
type ColonyStatus interface {
	Read(context.Context) (buildingruntime.ColonyStatusReport, error)
}

// colonyStatusDTO is one census; unknown facts are null. downed and
// moodMean are derived from the roster (moodMean over the colonists whose
// mood was readable, null when none was). raidPoints and the wealth split
// are the census's threat section. playerTechLevel is the faction's
// native TechLevel name and techTier the research-derived construction
// tier, null until a Rounds pass with the research census filed.
type colonyStatusDTO struct {
	FoodPlan             *foodPlanDTO      `json:"foodPlan"`
	FoodPlanTick         *domain.Tick      `json:"foodPlanTick"`
	Tick                 domain.Tick       `json:"tick"`
	RosterTick           domain.Tick       `json:"rosterTick"`
	Colonists            *int64            `json:"colonists"`
	Workers              *int64            `json:"workers"`
	FoodNutrition        *float64          `json:"foodNutrition"`
	NutritionPerDay      *float64          `json:"nutritionPerDay"`
	FoodRunwayDays       *float64          `json:"foodRunwayDays"`
	PendingFoodNutrition *float64          `json:"pendingFoodNutrition"`
	RaidPoints           *float64          `json:"raidPoints"`
	WealthTotal          *float64          `json:"wealthTotal"`
	WealthItems          *float64          `json:"wealthItems"`
	WealthBuildings      *float64          `json:"wealthBuildings"`
	WealthPawns          *float64          `json:"wealthPawns"`
	PlayerTechLevel      *string           `json:"playerTechLevel"`
	TechTier             *string           `json:"techTier"`
	Shrines              []colonyShrineDTO `json:"shrines"`
	// Stockpiles counts the owned stockpile zones by role kind and
	// ForbiddenSupplies is whether starting supplies were still forbidden
	// at the last review (the colony review report's zone check); null
	// until a review has filed.
	Stockpiles        []colonyStockpileDTO `json:"stockpiles"`
	ForbiddenSupplies *bool                `json:"forbiddenSupplies"`
	Downed            int                  `json:"downed"`
	MoodMean          *float64             `json:"moodMean"`
	// MoodLedger ranks where the colony loses mood and the unowned bucket;
	// null until a review has filed.
	MoodLedger *moodLedgerDTO        `json:"moodLedger"`
	Pawns      []colonyStatusPawnDTO `json:"pawns"`
}

type colonyStockpileDTO struct {
	Role  string `json:"role"`
	Zones int    `json:"zones"`
	Cells int    `json:"cells"`
	Used  int    `json:"used"`
}

// colonyShrineDTO is one ancient shrine: the casket group, whether
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
	// Ready and Reason are the breach judgement; Reason is empty when
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
	// Share, Spent and Remaining are the colonist's personal wealth share,
	// what they have attributed to them now and what is left; null
	// when the held colony facts are missing or stale, or the colonist has no
	// share (a slave) or an input is unread.
	Share     *float64 `json:"share"`
	Spent     *float64 `json:"spent"`
	Remaining *float64 `json:"remaining"`
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
		RaidPoints: factPointer(v.Threat.RaidPoints), WealthTotal: factPointer(v.Threat.WealthTotal),
		WealthItems: factPointer(v.Threat.WealthItems), WealthBuildings: factPointer(v.Threat.WealthBuildings), WealthPawns: factPointer(v.Threat.WealthPawns),
		Pawns: []colonyStatusPawnDTO{},
	}
	out.FoodPlanTick = factPointer(v.FoodPlanTick)
	out.PlayerTechLevel = factPointer(v.PlayerTechLevel)
	out.ForbiddenSupplies = factPointer(v.ForbiddenSupplies)
	if zones, known := v.Stockpiles.Value(); known {
		out.Stockpiles = []colonyStockpileDTO{}
		for _, z := range zones {
			out.Stockpiles = append(out.Stockpiles, colonyStockpileDTO{Role: z.Role, Zones: z.Zones, Cells: z.Cells, Used: z.Used})
		}
	}
	if tier, known := v.TechTier.Value(); known {
		name := tier.String()
		out.TechTier = &name
	}
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
		out.FoodPlan = projectFoodPlan(plan, v.PetLabels)
	}
	moodSum, moodCount := 0.0, 0
	for _, pawn := range v.Pawns {
		row := colonyStatusPawnDTO{ID: pawn.ID, Label: pawn.Label, Downed: factPointer(pawn.Downed), Mood: factPointer(pawn.Mood), Food: factPointer(pawn.Food),
			Share: factPointer(pawn.Share), Spent: factPointer(pawn.Spent), Remaining: factPointer(pawn.Remaining)}
		if row.Downed != nil && *row.Downed {
			out.Downed++
		}
		if row.Mood != nil {
			moodSum += *row.Mood
			moodCount++
		}
		out.Pawns = append(out.Pawns, row)
	}
	if ledger, known := v.MoodLedger.Value(); known {
		out.MoodLedger = projectMoodLedger(ledger)
	}
	if moodCount > 0 {
		mean := moodSum / float64(moodCount)
		out.MoodMean = &mean
	}
	return out
}

// moodLedgerDTO is policy.MoodLedger on the wire: sources ranked by mood lost
// (most negative first), the unowned subset, the pawns whose thoughts were
// unreadable and the expectation levels observed. Expectation is empty while
// the level is not read; a source's Unverified counts affected pawns whose
// immunity or expectation gating could not be checked.
type moodLedgerDTO struct {
	Sources      []moodSourceDTO      `json:"sources"`
	Unowned      []moodSourceDTO      `json:"unowned"`
	UnknownPawns int                  `json:"unknownPawns"`
	Expectation  []moodExpectationDTO `json:"expectation"`
}

type moodSourceDTO struct {
	Def        string   `json:"def"`
	Pawns      int      `json:"pawns"`
	Lost       float64  `json:"lost"`
	Owners     []string `json:"owners"`
	Unverified int      `json:"unverified"`
}

type moodExpectationDTO struct {
	Level string `json:"level"`
	Pawns int    `json:"pawns"`
}

func projectMoodLedger(l policy.MoodLedger) *moodLedgerDTO {
	project := func(rows []policy.MoodLedgerSource) []moodSourceDTO {
		out := make([]moodSourceDTO, 0, len(rows))
		for _, s := range rows {
			owners := make([]string, 0, len(s.Owners))
			for _, owner := range s.Owners {
				owners = append(owners, string(owner))
			}
			out = append(out, moodSourceDTO{Def: s.Def, Pawns: s.Pawns, Lost: s.Lost, Owners: owners, Unverified: s.Unverified})
		}
		return out
	}
	expectation := make([]moodExpectationDTO, 0, len(l.Expectation))
	for _, e := range l.Expectation {
		expectation = append(expectation, moodExpectationDTO{Level: e.Level, Pawns: e.Pawns})
	}
	return &moodLedgerDTO{Sources: project(l.Sources), Unowned: project(l.Unowned), UnknownPawns: l.UnknownPawns, Expectation: expectation}
}

type foodPlanDTO struct {
	Portfolio       []foodPlanEntryDTO `json:"portfolio"`
	Unknown         []foodPlanEntryDTO `json:"unknown"`
	DeliveredPerDay float64            `json:"deliveredPerDay"`
	DemandPerDay    float64            `json:"demandPerDay"`
	GapPerDay       float64            `json:"gapPerDay"`
	Explain         string             `json:"explain"`
	// PetShortfalls are the non-colonist consumers below the minimum runway;
	// the colony runway no longer carries their need.
	PetShortfalls []petShortfallDTO `json:"petShortfalls"`
}

type petShortfallDTO struct {
	ID policy.PawnID `json:"id"`
	// Label is the pet's display name, null when the census gave none.
	Label           *string `json:"label"`
	RunwayDays      float64 `json:"runwayDays"`
	NutritionPerDay float64 `json:"nutritionPerDay"`
}

type foodPlanEntryDTO struct {
	Kind            string                  `json:"kind"`
	ID              string                  `json:"id"`
	Decision        policy.FoodPlanDecision `json:"decision"`
	Reason          string                  `json:"reason"`
	NutritionPerDay *float64                `json:"nutritionPerDay"`
	WorkPerDay      *float64                `json:"workPerDay"`
	LeadDays        *float64                `json:"leadDays"`
	Open            *bool                   `json:"open"`
	DeliveredPerDay float64                 `json:"deliveredPerDay"`
	Terms           []policy.CandidateTerm  `json:"terms"`
}

// foodKindWire is a food candidate's kind on the wire, the capitalized name the
// foodPlan rows have always carried.
func foodKindWire(k policy.CandidateKind) string {
	if k == policy.CandidateAnimalProduct {
		return "AnimalProduct"
	}
	return strings.ToUpper(string(k[:1])) + string(k[1:])
}

func projectFoodPlan(p policy.FoodPlan, labels map[policy.PawnID]string) *foodPlanDTO {
	project := func(rows []policy.FoodPlanEntry) []foodPlanEntryDTO {
		out := make([]foodPlanEntryDTO, 0, len(rows))
		for _, row := range rows {
			c := row.Channel
			out = append(out, foodPlanEntryDTO{Kind: foodKindWire(c.Kind), ID: c.ID, Decision: row.Decision, Reason: row.Reason,
				NutritionPerDay: factPointer(c.Nutrition().PerDay), WorkPerDay: factPointer(c.LaborPerDay), LeadDays: factPointer(c.LeadDays),
				Open: factPointer(c.Open()), DeliveredPerDay: row.DeliveredPerDay, Terms: row.Terms})
		}
		return out
	}
	pets := make([]petShortfallDTO, 0, len(p.Forecast.PetShortfalls))
	for _, row := range p.Forecast.PetShortfalls {
		pets = append(pets, petShortfallDTO{ID: row.ID, Label: labelPointer(labels, row.ID), RunwayDays: row.RunwayDays, NutritionPerDay: row.NutritionPerDay})
	}
	return &foodPlanDTO{Portfolio: project(p.Portfolio), Unknown: project(p.Unknown), DeliveredPerDay: p.DeliveredPerDay, DemandPerDay: p.DemandPerDay, GapPerDay: p.GapPerDay, Explain: p.Explain(), PetShortfalls: pets}
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

func labelPointer(labels map[policy.PawnID]string, id policy.PawnID) *string {
	if label, ok := labels[id]; ok {
		return &label
	}
	return nil
}
