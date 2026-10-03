package policy

import (
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Development capacity (#649) is the one accounting the ranking, a
// planner's yield and method admission share. A development slot is a
// concurrent optional project (a goal ranked at priority 3-4 or a player
// project); there is no count limit beyond the workers: each held or selected
// project needs labor for its profile, and developmentFit decides whether
// the next one still gets it.
//
// Admission matches one distinct census worker per project (AllocateWorkers,
// #647), so a pawn enabled for three work types is one worker, and open
// startup/survival work that takes no slot still holds its worker
// (DevelopmentHold with Slot false). One worker per project is the
// admission floor, not a ratio: an admitted project's designations are
// open to every enabled pawn natively. An unobserved census falls back to
// the per-work-type headcount (laborLedger).

// DevelopmentWorker is one available pawn of the distinct-worker census:
// the work types it is enabled for (priority above zero, not disabled, not
// incapable).
type DevelopmentWorker struct {
	ID   PawnID
	Work []WorkType `json:",omitempty"`
}

// DevelopmentHold is labor spoken for ahead of the ranked rows: a
// withheld prerequisite's work type, open optional work (Slot: it holds a
// development slot) or, in automatic mode, open startup/survival work
// (no slot, but its worker is busy).
type DevelopmentHold struct {
	Goal  GoalID       `json:",omitempty"`
	Labor LaborProfile `json:",omitempty"`
	Slot  bool         `json:",omitempty"`
}

// DevelopmentCensus is the automatic mode's worker census from the same
// pawns RoutineLabor counts. Unknown settings, availability or scope for
// any counted pawn make it unknown, and the ranking falls back to the
// per-type headcount.
func DevelopmentCensus(pawns []WorkPawn) domain.Fact[[]DevelopmentWorker] {
	var out []DevelopmentWorker
	for _, p := range pawns {
		available, known := p.Available.Value()
		applies, appliesKnown := p.Applies.Value()
		if known && !available || appliesKnown && !applies {
			continue
		}
		work, workKnown := p.Work.Value()
		if !known || !appliesKnown || !workKnown || len(work) == 0 {
			return domain.Unknown[[]DevelopmentWorker]()
		}
		incapable := map[WorkType]bool{}
		if list, ok := p.Incapable.Value(); ok {
			for _, w := range list {
				incapable[w] = true
			}
		}
		w := DevelopmentWorker{ID: p.ID}
		for _, s := range work {
			if s.Priority > 0 && !s.Disabled && !incapable[s.Work] {
				w.Work = append(w.Work, s.Work)
			}
		}
		sort.Slice(w.Work, func(i, j int) bool { return w.Work[i] < w.Work[j] })
		w.Work = compactWork(w.Work)
		out = append(out, w)
	}
	sort.SliceStable(out, func(i, j int) bool { return out[i].ID < out[j].ID })
	if len(out) > MaxAllocWorkers {
		out = out[:MaxAllocWorkers]
	}
	return domain.Known(out)
}

// CommitmentHolds is the labor open commitments hold, the same at ranking
// and at admission: one hold per goal with an unresolved open action that
// is neither stalled nor released (a labor_idle row). Optional work holds
// a slot; startup/survival work holds only its worker, and only in
// automatic mode. Withheld labor comes first, then goals in ID order.
func CommitmentHolds(commitments []Commitment, now domain.Tick, released map[GoalID]bool, withheld LaborProfile) []DevelopmentHold {
	var holds []DevelopmentHold
	for _, w := range withheld {
		holds = append(holds, DevelopmentHold{Labor: LaborProfile{w}})
	}
	byGoal := map[GoalID]DevelopmentHold{}
	for _, c := range commitments {
		v := c.Progress.View()
		if released[c.Goal] {
			continue
		}
		if !(v.Unresolved || v.Stage == domain.Pending || v.Stage == domain.Prepared || v.Stage == domain.Dispatched || v.Stage == domain.AwaitingObservation) {
			continue
		}
		slot := c.Source == PlayerGoal || c.Priority >= 3
		if prev, seen := byGoal[c.Goal]; seen && (prev.Slot || !slot) {
			continue
		}
		byGoal[c.Goal] = DevelopmentHold{Goal: c.Goal, Labor: append(LaborProfile(nil), c.Labor...), Slot: slot}
	}
	ids := make([]GoalID, 0, len(byGoal))
	for id := range byGoal {
		ids = append(ids, id)
	}
	sort.Slice(ids, func(i, j int) bool { return ids[i] < ids[j] })
	for _, id := range ids {
		holds = append(holds, byGoal[id])
	}
	return holds
}

// developmentFit reports, for each demand in order, whether it gets labor
// after every demand before it: a distinct census worker in automatic
// mode with a known census, one pawn of a free type on the per-type
// headcount otherwise. An empty profile needs no worker. A demand that
// does not fit names its bottleneck.
func developmentFit(census domain.Fact[[]DevelopmentWorker], labor domain.Fact[map[WorkType]int], demands []LaborProfile) ([]bool, []WorkType) {
	fits := make([]bool, len(demands))
	bottlenecks := make([]WorkType, len(demands))
	workers, known := census.Value()
	if !known {
		ledger := newLaborLedger(labor)
		for i, d := range demands {
			bottlenecks[i], fits[i] = ledger.take(d)
		}
		return fits, bottlenecks
	}
	var ready ReadyWorkReport
	for i, d := range demands {
		if len(d) == 0 {
			fits[i] = true
			continue
		}
		ready.Candidates = append(ready.Candidates, ReadyWork{ID: ReadyWorkID(fmt.Sprintf("demand/%04d", i)), Stage: "development", Work: d, State: ReadyRunnable, Parallelism: 1, Adapter: ReadyConservative})
	}
	pool := make([]AllocWorker, 0, len(workers))
	for _, w := range workers {
		settings := make([]WorkPriority, 0, len(w.Work))
		for _, t := range w.Work {
			settings = append(settings, WorkPriority{Work: t, Priority: 1})
		}
		// Capacity, not the moment's job: every available pawn is
		// idle to the matching, and open work is a hold ahead of it.
		pool = append(pool, AllocWorker{ID: w.ID, Status: AllocAvailable, Work: domain.Known(settings), Occupancy: OccupancyIdle})
	}
	report := AllocateWorkers(AllocRequest{Workers: pool, Ready: ready})
	filled := map[ReadyWorkID]bool{}
	for _, a := range report.Assignments {
		filled[a.Work] = true
	}
	for i, d := range demands {
		if len(d) == 0 {
			continue
		}
		fits[i] = filled[ReadyWorkID(fmt.Sprintf("demand/%04d", i))]
		if !fits[i] {
			bottlenecks[i] = d[0]
		}
	}
	return fits, bottlenecks
}

// AdmitDevelopment revalidates, inside method admission, that the review
// still grants need a development slot: its row is still selected. A retry
// of the same admission is refused by the goal's revision before it gets
// here.
func AdmitDevelopment(s DevelopmentState, need GoalID) error {
	for _, row := range s.Rows {
		if row.Goal == need && row.Selected {
			return nil
		}
	}
	return fmt.Errorf("goal %s holds no development slot", need)
}
