package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"testing"
)

func questForecastSnapshot() (JoinerOffer, RoundsFacts) {
	schedule := make([]string, 24)
	for i := range schedule {
		schedule[i] = "Anything"
	}
	worker := WorkPawn{ID: "builder", Schedule: domain.Known(schedule), Work: domain.Known([]WorkPriority{{Work: WorkConstruction, Priority: 2}})}
	offer := JoinerOffer{Quest: "offered", State: "NotYetAccepted", Profile: domain.Known(QuestProfile{Family: QuestFamilyBuildMonument, Cost: QuestCostTime, Disposition: QuestDecide}), Objectives: []QuestObjective{{Kind: o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_MONUMENT, DurationTicks: domain.Known(int64(60000)), Workload: domain.Known(QuestWorkload{Work: WorkConstruction, Stat: "ConstructionSpeed", Amount: 8000, RateFactor: 1})}}}
	facts := RoundsFacts{QuestObservedTick: domain.Known(domain.Tick(100)), QuestOffers: domain.Known([]JoinerOffer{}), QuestWorkers: domain.Known([]WorkPawn{worker}), QuestSparePawns: domain.Known([]PawnID{"builder"}), QuestWorkCapacity: domain.Known([]QuestWorkCapacity{{Pawn: "builder", HealthyAdult: domain.Known(true), Rates: map[string]float64{"ConstructionSpeed": 1}}})}
	return offer, facts
}

func TestQuestDeadlineForecastSnapshots(t *testing.T) {
	for name, test := range map[string]struct {
		edit func(*JoinerOffer, *RoundsFacts)
		want QuestSkipReason
	}{
		"fits observed work rate": {},
		"short deadline":          {edit: func(q *JoinerOffer, _ *RoundsFacts) { q.Objectives[0].DurationTicks = domain.Known(int64(30000)) }, want: "deadline_capacity"},
		"expired active deadline": {edit: func(q *JoinerOffer, _ *RoundsFacts) { q.Objectives[0].DeadlineTicks = domain.Known(int64(99)) }, want: "deadline"},
		"work rate unknown":       {edit: func(_ *JoinerOffer, f *RoundsFacts) { rows, _ := f.QuestWorkCapacity.Value(); rows[0].Rates = nil }, want: "deadline_unknown"},
		"schedule unknown": {edit: func(_ *JoinerOffer, f *RoundsFacts) {
			rows, _ := f.QuestWorkers.Value()
			rows[0].Schedule = domain.Unknown[[]string]()
		}, want: "deadline_unknown"},
		"work disabled": {edit: func(_ *JoinerOffer, f *RoundsFacts) {
			rows, _ := f.QuestWorkers.Value()
			rows[0].Work = domain.Known([]WorkPriority{{Work: WorkConstruction, Disabled: true}})
		}, want: "deadline_capacity"},
		"open work overcommits": {edit: func(q *JoinerOffer, f *RoundsFacts) {
			open := *q
			open.Quest = "open"
			open.State = "Ongoing"
			f.QuestOffers = domain.Known([]JoinerOffer{open})
		}, want: "deadline_capacity"},
		"automatic open reserves no work": {edit: func(q *JoinerOffer, f *RoundsFacts) {
			open := *q
			open.Quest = "automatic"
			open.State = "Ongoing"
			open.Profile = domain.Known(QuestProfile{Cost: QuestCostTime, NeverAct: true, Disposition: QuestObserve})
			f.QuestOffers = domain.Known([]JoinerOffer{open})
		}},
	} {
		t.Run(name, func(t *testing.T) {
			q, f := questForecastSnapshot()
			if test.edit != nil {
				test.edit(&q, &f)
			}
			if got := QuestDeadlineFeasibility(q, f); got != test.want {
				t.Fatalf("got %q want %q", got, test.want)
			}
		})
	}
}

func TestQuestDeadlineForecastSharesDifferentWorkAcrossOnePawn(t *testing.T) {
	q, f := questForecastSnapshot()
	workers, _ := f.QuestWorkers.Value()
	workers[0].Work = domain.Known([]WorkPriority{{Work: WorkConstruction, Priority: 2}, {Work: WorkPlantCutting, Priority: 2}})
	capacity, _ := f.QuestWorkCapacity.Value()
	capacity[0].Rates["PlantWorkSpeed"] = 1
	open := JoinerOffer{Quest: "harvest", State: "Ongoing", Profile: domain.Known(QuestProfile{Cost: QuestCostTime}), Objectives: []QuestObjective{{Active: domain.Known(true), Kind: o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_HARVEST_PLANT, DeadlineTicks: domain.Known(int64(60100)), Workload: domain.Known(QuestWorkload{Work: WorkPlantCutting, Stat: "PlantWorkSpeed", Amount: 8000, RateFactor: 1})}}}
	f.QuestOffers = domain.Known([]JoinerOffer{open})
	if got := QuestDeadlineFeasibility(q, f); got != "deadline_capacity" {
		t.Fatal(got)
	}
}
