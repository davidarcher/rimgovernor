package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"math"
	"slices"
	"sort"
)

type QuestWorkCapacity struct {
	Pawn         PawnID
	Rates        map[string]float64
	HealthyAdult domain.Fact[bool]
}
type QuestWorkload struct {
	Work               WorkType
	Stat               string
	Amount, RateFactor float64
}
type questTimedWork struct {
	deadline int64
	workload QuestWorkload
}

// The forecast prices actual native work at observed rates. It uses complete
// days, at most eight scheduled work/Anything hours per day, and half that time
// for useful work; the remainder funds hauling, walking and normal upkeep.
func QuestDeadlineFeasibility(offer JoinerOffer, f RoundsFacts) QuestSkipReason {
	now, nk := f.QuestObservedTick.Value()
	tasks, reason := questDeadlineTasks(offer, int64(now))
	if reason != "" {
		return reason
	}
	if len(tasks) == 0 {
		return ""
	}
	if !nk {
		return "deadline_unknown"
	}
	capacities, ck := f.QuestWorkCapacity.Value()
	workers, wk := f.QuestWorkers.Value()
	spare, sk := f.QuestSparePawns.Value()
	open, ok := f.QuestOffers.Value()
	if !ck || !wk || !sk || !ok {
		return "deadline_unknown"
	}
	// Nonautomatic open work has first claim. Untimed commitments reserve a
	// whole worker; timed work shares the same budgets as the new offer.
	reserved := int64(0)
	for _, q := range open {
		if q.State != "Ongoing" || q.Quest == offer.Quest {
			continue
		}
		p, known := q.Profile.Value()
		if !known {
			return "open_demands_unknown"
		}
		if p.Cost == QuestCostFree || p.NeverAct || p.Disposition == QuestObserve {
			continue
		}
		pending, r := questDeadlineTasks(q, int64(now))
		if r != "" {
			return "open_demands_unknown"
		}
		if len(pending) == 0 {
			n, r := questPawnDemand(q)
			if r != "" {
				return r
			}
			reserved += n
		} else {
			tasks = append(tasks, pending...)
		}
	}
	byID := map[PawnID]WorkPawn{}
	for _, p := range workers {
		byID[p.ID] = p
	}
	allowed := map[PawnID]bool{}
	for _, id := range spare {
		allowed[id] = true
	}
	capacities = slices.Clone(capacities)
	sort.Slice(capacities, func(i, j int) bool { return capacities[i].Pawn < capacities[j].Pawn })
	eligible := []QuestWorkCapacity{}
	for _, p := range capacities {
		if !allowed[p.Pawn] {
			continue
		}
		healthy, hk := p.HealthyAdult.Value()
		if !hk || !healthy {
			continue
		}
		if _, exists := byID[p.Pawn]; !exists {
			continue
		}
		if reserved > 0 {
			reserved--
			continue
		}
		eligible = append(eligible, p)
	}
	sort.SliceStable(tasks, func(i, j int) bool { return tasks[i].deadline < tasks[j].deadline })
	used := map[PawnID]float64{}
	for _, task := range tasks {
		if task.deadline <= int64(now) {
			return "deadline"
		}
		remaining := task.workload.Amount
		if remaining <= 0 {
			continue
		}
		for _, pawn := range eligible {
			p := byID[pawn.Pawn]
			scheduled, known := p.WorkHoursPerDay().Value()
			if !known {
				return "deadline_unknown"
			}
			if !questWorkEnabled(p, task.workload.Work) {
				continue
			}
			rate := 1.0
			if task.workload.Stat != "" {
				var known bool
				rate, known = pawn.Rates[task.workload.Stat]
				if !known {
					return "deadline_unknown"
				}
			}
			rate *= task.workload.RateFactor
			if !finitePositive(rate) {
				continue
			}
			budget := float64((task.deadline-int64(now))/domain.TicksPerDay)*float64(min(scheduled, 8)*domain.TicksPerHour)/2 - used[pawn.Pawn]
			if budget <= 0 {
				continue
			}
			done := math.Min(remaining, budget*rate)
			used[pawn.Pawn] += done / rate
			remaining -= done
			if remaining <= 1e-6 {
				break
			}
		}
		if remaining > 1e-6 {
			return "deadline_capacity"
		}
	}
	return ""
}

func questDeadlineTasks(offer JoinerOffer, now int64) ([]questTimedWork, QuestSkipReason) {
	tasks := []questTimedWork{}
	for _, objective := range offer.Objectives {
		if objective.Kind == o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_EXPIRY {
			continue
		}
		if offer.State == "Ongoing" {
			active, known := objective.Active.Value()
			if known && !active && objective.Kind != o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_MONUMENT {
				continue
			}
		}
		deadline, dk := objective.DeadlineTicks.Value()
		duration, tk := objective.DurationTicks.Value()
		if !dk && !tk {
			continue
		}
		if !dk {
			if duration > math.MaxInt64-now {
				return nil, "deadline_unknown"
			}
			deadline = now + duration
		}
		if count, ck := objective.Count.Value(); ck {
			if produced, pk := objective.Produced.Value(); pk && produced >= count {
				continue
			}
		}
		work, known := objective.Workload.Value()
		if marker, mk := objective.Monument.Value(); mk {
			if done, known := marker.AllDone.Value(); known && done {
				continue
			}
			amount := 0.0
			for _, piece := range marker.Pieces {
				if built, bk := piece.Built.Value(); bk && built {
					continue
				}
				if len(piece.BuildOptions) == 0 {
					return nil, "deadline_unknown"
				}
				largest := 0.0
				for _, option := range piece.BuildOptions {
					largest = math.Max(largest, option.Work)
				}
				amount += largest
			}
			work = QuestWorkload{Work: WorkConstruction, Stat: "ConstructionSpeed", Amount: amount, RateFactor: 1}
			known = true
		}
		if !known {
			return nil, "deadline_unknown"
		}
		if work.Amount == 0 {
			continue
		}
		tasks = append(tasks, questTimedWork{deadline, work})
	}
	return tasks, ""
}
func questWorkEnabled(p WorkPawn, work WorkType) bool {
	rows, known := p.Work.Value()
	if !known {
		return false
	}
	for _, w := range rows {
		if w.Work == work {
			return !w.Disabled && w.Priority > 0
		}
	}
	return false
}
func finitePositive(v float64) bool { return v > 0 && !math.IsNaN(v) && !math.IsInf(v, 0) }
