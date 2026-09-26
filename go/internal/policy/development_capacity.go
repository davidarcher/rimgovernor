package policy

import (
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Development capacity (#649) is the one accounting the ranking, a
// planner's yield and method admission share. A development slot is a
// concurrent optional project (a goal ranked at priority 3-4 or a player
// project); the limit (explicit --routine-project-limit, or
// MaxAutoDevelopmentProjects in automatic mode) bounds planner cost and
// queue growth, never worker use. Worker capacity is separate: each held
// or selected project needs labor for its profile, and developmentFit
// decides whether the next one still gets it.
//
// Explicit mode keeps the per-work-type headcount (laborLedger). Automatic
// mode matches one distinct census worker per project (AllocateWorkers,
// #647), so a pawn enabled for three work types is one worker, and open
// startup/survival work that takes no slot still holds its worker
// (DevelopmentHold with Slot false). One worker per project is the
// admission floor, not a ratio: an admitted project's designations are
// open to every enabled pawn natively.

// MaxAutoDevelopmentProjects is the automatic mode's slot bound: the
// ranking's own bound, kept for planner cost, not a worker ratio.
const MaxAutoDevelopmentProjects = 8

// MaxDevelopmentYields bounds the regrants one review hands out after
// planners report no method (YieldDevelopment); past it a yield records
// DevelopmentYieldBound and the next review ranks again.
const MaxDevelopmentYields = 8

// DevelopmentYieldBound is the continuation a review records once its
// yields are spent.
const DevelopmentYieldBound = "yield_bound"

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
func CommitmentHolds(commitments []Commitment, now domain.Tick, released map[GoalID]bool, auto bool, withheld LaborProfile) []DevelopmentHold {
	var holds []DevelopmentHold
	for _, w := range withheld {
		holds = append(holds, DevelopmentHold{Labor: LaborProfile{w}})
	}
	byGoal := map[GoalID]DevelopmentHold{}
	for _, c := range commitments {
		v := c.Progress.View()
		if c.Source == AdviserGoal || c.Stalled(now) || released[c.Goal] {
			continue
		}
		if !(v.Unresolved || v.Stage == domain.Pending || v.Stage == domain.Prepared || v.Stage == domain.Dispatched || v.Stage == domain.AwaitingObservation) {
			continue
		}
		slot := c.Source == PlayerGoal || c.Priority >= 3
		if !slot && !auto {
			continue
		}
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
func developmentFit(auto bool, census domain.Fact[[]DevelopmentWorker], labor domain.Fact[map[WorkType]int], demands []LaborProfile) ([]bool, []WorkType) {
	fits := make([]bool, len(demands))
	bottlenecks := make([]WorkType, len(demands))
	workers, known := census.Value()
	if !auto || !known {
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

// holdsFit fits the holds and then each of rows in order; overcommitted
// reports a hold that did not fit (open work already exceeds the census).
func holdsFit(s DevelopmentState, rows []LaborProfile) (fits []bool, bottlenecks []WorkType, overcommitted bool) {
	demands := make([]LaborProfile, 0, len(s.Holds)+len(rows))
	for _, h := range s.Holds {
		demands = append(demands, h.Labor)
	}
	demands = append(demands, rows...)
	all, necks := developmentFit(s.Auto, s.Census, s.Labor, demands)
	for _, ok := range all[:len(s.Holds)] {
		overcommitted = overcommitted || s.Auto && !ok
	}
	return all[len(s.Holds):], necks[len(s.Holds):], overcommitted
}

func slotHolds(holds []DevelopmentHold) int {
	n := 0
	for _, h := range holds {
		if h.Slot {
			n++
		}
	}
	return n
}

// AdmitDevelopment revalidates, inside method admission, the slot the
// review granted need: the row is still selected, the slots held now
// (holds, recomputed from current commitments with CommitmentHolds) leave
// room, and the labor after the holds and every selected row ranked ahead
// of need that has not been admitted yet still fits need. A player project
// or another admission since the ranking is in holds; a retry of the same
// admission finds its own work held and is refused by the goal's revision
// before it gets here.
func AdmitDevelopment(s DevelopmentState, need GoalID, holds []DevelopmentHold) error {
	at := -1
	for i, row := range s.Rows {
		if row.Goal == need && row.Selected {
			at = i
		}
	}
	if at < 0 {
		return fmt.Errorf("goal %s holds no development slot", need)
	}
	held := map[GoalID]bool{}
	for _, h := range holds {
		if h.Goal != "" {
			held[h.Goal] = true
		}
	}
	// Selected rows ranked ahead and not admitted yet keep the slot and
	// labor the ranking gave them.
	var rows []LaborProfile
	for _, row := range s.Rows[:at] {
		if row.Selected && !held[row.Goal] {
			rows = append(rows, row.Labor)
		}
	}
	if n := slotHolds(holds) + len(rows); n >= s.Capacity {
		return fmt.Errorf("%s: development capacity %d already committed", DevelopmentCapacity, s.Capacity)
	}
	rows = append(rows, s.Rows[at].Labor)
	current := s
	current.Holds = holds
	fits, bottlenecks, overcommitted := holdsFit(current, rows)
	switch last := len(rows) - 1; {
	case overcommitted:
		return fmt.Errorf("%s: open work holds more workers than the census has", DevelopmentOvercommitted)
	case !fits[last]:
		return fmt.Errorf("%s: no free %s worker for %s", DevelopmentLabor, bottlenecks[last], need)
	}
	return nil
}
