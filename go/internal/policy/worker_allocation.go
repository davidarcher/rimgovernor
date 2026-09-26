package policy

import (
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Worker allocation (#647) proposes which distinct pawn could take which
// ready-work position (#645). It replaces per-work-type headcounts as the
// capacity model: a pawn that can cook, grow and haul is one worker, not
// three. It is planning and accounting only. It forces no job and reserves
// nothing. Material and cell feasibility stay with admission, which
// revalidates them atomically. Development admission matches through it
// in automatic mode (developmentFit, #649).
//
// Algorithm and bounds: candidates are expanded, in their ready-work order
// (that order is the priority), into at most Parallelism positions each.
// Positions are then matched one at a time by augmenting paths (Kuhn). An
// augmenting path can move a tentatively assigned pawn to another position
// it is eligible for, but it never unmatches a position that was already
// filled. So each earlier (higher-priority) position that was filled stays
// filled, and the result is a maximum matching of the eligible positions
// with priority broken lexicographically. Operator ceilings and alternative
// groups are applied greedily in priority order, so with them the result is
// only maximal. It is not a global schedule: it ignores skill quality,
// travel and job duration. Each position's search visits each worker at
// most once and scans W workers per visit, so it costs O(W²), and a whole
// pass is O(P·W²) operations. MaxOps caps that. When the cap
// is reached, the remaining positions read allocation_budget and the report
// carries a Continuation instead of searching further.

// AllocStatus is a worker's availability as the observation contract saw
// it.
type AllocStatus string

const (
	AllocAvailable AllocStatus = "available"
	AllocDrafted   AllocStatus = "drafted"
	AllocResting   AllocStatus = "resting" // medical rest, bed rest
	// AllocUnavailable: downed, mental break, away from the map, or
	// otherwise not working.
	AllocUnavailable AllocStatus = "unavailable"
	AllocUnknown     AllocStatus = "unknown"
)

// AllocOccupancy is what the worker is doing now.
type AllocOccupancy string

const (
	OccupancyIdle AllocOccupancy = "idle"
	// OccupancyServing: the worker's observed job is attributed (#643) to
	// the candidate named by Serving. The worker stays on it.
	OccupancyServing AllocOccupancy = "serving"
	// OccupancyOther: the worker is on work outside the candidates, such
	// as survival or startup work, another goal, or a player order.
	OccupancyOther AllocOccupancy = "other"
	// OccupancyUnknown: no job observation, so the worker is not treated
	// as free.
	OccupancyUnknown AllocOccupancy = "unknown"
)

// AllocWorker is one immutable worker observation.
type AllocWorker struct {
	ID     PawnID
	Status AllocStatus
	// Work is the enabled work settings. When unknown, eligibility is
	// unknown, never assumed.
	Work domain.Fact[[]WorkPriority]
	// Incapable lists native hard constraints (backstory or trait
	// disables). A known incapability is final.
	Incapable []WorkType `json:",omitempty"`
	Occupancy AllocOccupancy
	Serving   ReadyWorkID `json:",omitempty"`
}

// AllocBounds are the explicit policy inputs and the operation budget.
type AllocBounds struct {
	Workers   int // observed workers considered, ordered by ID
	Positions int // positions expanded
	MaxOps    int // edge visits in the matching
	// Ceiling caps assigned positions (incumbents included) per work type
	// (an operator ceiling). A work type that is not listed has no cap.
	Ceiling map[WorkType]int `json:",omitempty"`
	// Restricted work types get no new positions (emergency or control
	// restrictions). Incumbents already on them keep their job.
	Restricted map[WorkType]bool `json:",omitempty"`
	// Overrides are player overrides: a priority here replaces the
	// worker's setting for that work type, and 0 disables it.
	Overrides []WorkOverride `json:",omitempty"`
}

// Declared maximums (the complexity contract): at most 64 workers against
// 256 positions (64 candidates × maxReadyParallelism). The budget allows
// the full P·W² = 256·64² worst case.
const (
	MaxAllocWorkers   = 64
	MaxAllocPositions = 256
	MaxAllocOps       = MaxAllocPositions * MaxAllocWorkers * MaxAllocWorkers
)

func DefaultAllocBounds() AllocBounds {
	return AllocBounds{Workers: MaxAllocWorkers, Positions: MaxAllocPositions, MaxOps: MaxAllocOps}
}

// Reasons for a position or worker that is left unused.
const (
	AllocReasonIncumbent        = "incumbent"
	AllocReasonMatched          = "matched"
	AllocReasonNoEligible       = "no_eligible_worker"
	AllocReasonEligibleUnknown  = "eligibility_unknown"
	AllocReasonWorkersTaken     = "eligible_workers_assigned"
	AllocReasonCeiling          = "operator_ceiling"
	AllocReasonRestricted       = "restricted"
	AllocReasonAlternativeTaken = "alternative_assigned"
	AllocReasonBudget           = "allocation_budget"
	AllocReasonPositionBound    = "position_bound"
	AllocReasonWorkerBound      = "worker_bound"
	AllocReasonBusy             = "busy_other_work"
	AllocReasonOccupancyUnknown = "occupancy_unknown"
	AllocReasonSettingsUnknown  = "settings_unknown"
	AllocReasonNoDemand         = "no_eligible_position"
	AllocReasonDisplaced        = "incumbent_work_not_runnable"
)

type AllocAssignment struct {
	Pawn     PawnID
	Work     ReadyWorkID
	Position int // 0-based slot within the candidate
	WorkType WorkType
	Reason   string // incumbent or matched
}

type AllocPositionGap struct {
	Work     ReadyWorkID
	Position int
	Reason   string
}

// AllocIdle is capacity that got no assignment. Potential lists the work
// types the worker might take once unknown facts or availability resolve.
// That is future eligibility, never counted as usable now.
type AllocIdle struct {
	Pawn      PawnID
	Reason    string
	Potential []WorkType `json:",omitempty"`
}

type AllocationReport struct {
	Assignments []AllocAssignment  `json:",omitempty"`
	Unfilled    []AllocPositionGap `json:",omitempty"`
	Unused      []AllocIdle        `json:",omitempty"`
	Ops         int
	// Continuation is the first position the budget skipped.
	Continuation string `json:",omitempty"`
}

type AllocRequest struct {
	Workers []AllocWorker
	// Ready is the ready-work report. Its candidate order is the priority.
	Ready  ReadyWorkReport
	Bounds AllocBounds
}

type allocPosition struct {
	work  ReadyWork
	slot  int
	wtype WorkType
}

// Eligibility of one worker for one work type.
type allocEdge int

const (
	edgeNo allocEdge = iota
	edgeUnknown
	edgeYes
)

func allocEligible(w AllocWorker, t WorkType, overrides map[[2]string]int) allocEdge {
	for _, x := range w.Incapable {
		if x == t {
			return edgeNo
		}
	}
	if p, ok := overrides[[2]string{string(w.ID), string(t)}]; ok {
		if p > 0 {
			return edgeYes
		}
		return edgeNo
	}
	settings, known := w.Work.Value()
	if !known {
		return edgeUnknown
	}
	for _, s := range settings {
		if s.Work == t {
			if s.Disabled || s.Priority <= 0 {
				return edgeNo
			}
			return edgeYes
		}
	}
	return edgeNo
}

// AllocateWorkers proposes distinct-worker assignments. It is pure and
// deterministic: equal inputs give equal reports whatever their order.
func AllocateWorkers(r AllocRequest) AllocationReport {
	b := r.Bounds
	if b.Workers == 0 {
		b.Workers = MaxAllocWorkers
	}
	if b.Positions == 0 {
		b.Positions = MaxAllocPositions
	}
	if b.MaxOps == 0 {
		b.MaxOps = MaxAllocOps
	}
	overrides := map[[2]string]int{}
	for _, o := range b.Overrides {
		overrides[[2]string{string(o.Pawn), string(o.Work)}] = o.Priority
	}
	var report AllocationReport

	workers := append([]AllocWorker(nil), r.Workers...)
	sort.SliceStable(workers, func(i, j int) bool { return workers[i].ID < workers[j].ID })
	if len(workers) > b.Workers {
		for _, w := range workers[b.Workers:] {
			report.Unused = append(report.Unused, AllocIdle{Pawn: w.ID, Reason: AllocReasonWorkerBound})
		}
		workers = workers[:b.Workers]
	}

	// Expand runnable candidates into positions, in priority order.
	var positions []allocPosition
	byID := map[ReadyWorkID]int{} // candidate -> first position index
	for _, c := range r.Ready.Candidates {
		if c.State != ReadyRunnable || c.Parallelism == 0 || len(c.Work) == 0 {
			continue
		}
		byID[c.ID] = len(positions)
		for s := 0; s < c.Parallelism; s++ {
			if len(positions) >= b.Positions {
				report.Unfilled = append(report.Unfilled, AllocPositionGap{Work: c.ID, Position: s, Reason: AllocReasonPositionBound})
				continue
			}
			positions = append(positions, allocPosition{work: c, slot: s, wtype: c.Work[0]})
		}
	}

	owner := make([]int, len(positions)) // position -> worker index, -1 if open
	for i := range owner {
		owner[i] = -1
	}
	held := make([]int, len(workers)) // worker -> position index, -1 if none
	perType := map[WorkType]int{}
	groupTaken := map[string]bool{}
	incumbent := make([]bool, len(positions))

	// eligible reports whether worker w can fill position p. A conservative
	// candidate accepts any work type in its profile.
	eligible := func(w AllocWorker, p allocPosition) allocEdge {
		best := edgeNo
		for _, t := range p.work.Work {
			if e := allocEligible(w, t, overrides); e > best {
				best = e
			}
		}
		return best
	}

	// Incumbents come first. A worker on attributed work keeps its first
	// open position there, even under a restriction or ceiling, so that
	// rematching never displaces it.
	for wi, w := range workers {
		held[wi] = -1
		if w.Occupancy != OccupancyServing {
			continue
		}
		start, ok := byID[w.Serving]
		if !ok {
			continue
		}
		for pi := start; pi < len(positions) && positions[pi].work.ID == w.Serving; pi++ {
			if owner[pi] == -1 {
				owner[pi], held[wi], incumbent[pi] = wi, pi, true
				perType[positions[pi].wtype]++
				if g := positions[pi].work.Alternative; g != "" {
					groupTaken[g] = true
				}
				break
			}
		}
	}

	free := func(w AllocWorker) bool {
		return w.Status == AllocAvailable && (w.Occupancy == OccupancyIdle || w.Occupancy == OccupancyServing)
	}
	// A serving worker whose candidate is not runnable (or has no free
	// slot) is idle for this pass: its old work no longer holds it.
	var visited []bool
	budgetOut := false
	var augment func(pi int) bool
	augment = func(pi int) bool {
		for wi, w := range workers {
			if budgetOut {
				return false
			}
			report.Ops++
			if report.Ops > b.MaxOps {
				budgetOut = true
				return false
			}
			if visited[wi] || !free(w) || eligible(w, positions[pi]) != edgeYes {
				continue
			}
			visited[wi] = true
			cur := held[wi]
			// augment(cur) re-seats cur with another worker, which frees
			// wi to take pi.
			if cur == -1 || (!incumbent[cur] && augment(cur)) {
				owner[pi], held[wi] = wi, pi
				return true
			}
		}
		return false
	}

	gap := map[int]string{}
	for pi, p := range positions {
		if owner[pi] != -1 {
			continue
		}
		if budgetOut {
			gap[pi] = AllocReasonBudget
			if report.Continuation == "" {
				report.Continuation = fmt.Sprintf("%s/%d", p.work.ID, p.slot)
			}
			continue
		}
		switch g := p.work.Alternative; {
		case b.Restricted[p.wtype]:
			gap[pi] = AllocReasonRestricted
			continue
		case g != "" && groupTaken[g] && !sameCandidateHeld(positions, owner, pi):
			gap[pi] = AllocReasonAlternativeTaken
			continue
		}
		if c, ok := b.Ceiling[p.wtype]; ok && perType[p.wtype] >= c {
			gap[pi] = AllocReasonCeiling
			continue
		}
		visited = make([]bool, len(workers))
		if augment(pi) {
			perType[p.wtype]++
			if g := p.work.Alternative; g != "" {
				groupTaken[g] = true
			}
			continue
		}
		if budgetOut {
			gap[pi] = AllocReasonBudget
			if report.Continuation == "" {
				report.Continuation = fmt.Sprintf("%s/%d", p.work.ID, p.slot)
			}
			continue
		}
		gap[pi] = positionGapReason(workers, p, free, eligible)
	}

	for pi, p := range positions {
		if wi := owner[pi]; wi != -1 {
			reason := AllocReasonMatched
			if incumbent[pi] {
				reason = AllocReasonIncumbent
			}
			report.Assignments = append(report.Assignments, AllocAssignment{Pawn: workers[wi].ID, Work: p.work.ID, Position: p.slot, WorkType: p.wtype, Reason: reason})
			continue
		}
		report.Unfilled = append(report.Unfilled, AllocPositionGap{Work: p.work.ID, Position: p.slot, Reason: gap[pi]})
	}
	for wi, w := range workers {
		if held[wi] != -1 {
			continue
		}
		report.Unused = append(report.Unused, AllocIdle{Pawn: w.ID, Reason: workerIdleReason(w), Potential: potential(w, overrides)})
	}
	sort.SliceStable(report.Unused, func(i, j int) bool { return report.Unused[i].Pawn < report.Unused[j].Pawn })
	return report
}

// sameCandidateHeld reports whether another slot of pi's own candidate is
// held. Extra slots of the chosen alternative stay open to fill.
func sameCandidateHeld(positions []allocPosition, owner []int, pi int) bool {
	for i, p := range positions {
		if i != pi && p.work.ID == positions[pi].work.ID && owner[i] != -1 {
			return true
		}
	}
	return false
}

func positionGapReason(workers []AllocWorker, p allocPosition, free func(AllocWorker) bool, eligible func(AllocWorker, allocPosition) allocEdge) string {
	unknown, taken := false, false
	for _, w := range workers {
		switch e := eligible(w, p); {
		case e == edgeYes && free(w):
			taken = true
		case e == edgeUnknown && w.Status != AllocDrafted && w.Status != AllocResting && w.Status != AllocUnavailable:
			unknown = true
		}
	}
	switch {
	case taken:
		return AllocReasonWorkersTaken
	case unknown:
		return AllocReasonEligibleUnknown
	}
	return AllocReasonNoEligible
}

func workerIdleReason(w AllocWorker) string {
	switch w.Status {
	case AllocDrafted, AllocResting, AllocUnavailable:
		return string(w.Status)
	case AllocUnknown:
		return "status_unknown"
	}
	switch w.Occupancy {
	case OccupancyOther:
		return AllocReasonBusy
	case OccupancyUnknown, "":
		return AllocReasonOccupancyUnknown
	case OccupancyServing:
		return AllocReasonDisplaced
	}
	if _, known := w.Work.Value(); !known {
		return AllocReasonSettingsUnknown
	}
	return AllocReasonNoDemand
}

// potential lists the work types the worker may take that are not ruled
// out: known-enabled ones, or every type not known incapable when the
// settings are unknown. It is sorted.
func potential(w AllocWorker, overrides map[[2]string]int) []WorkType {
	var out []WorkType
	if settings, known := w.Work.Value(); known {
		for _, s := range settings {
			if allocEligible(w, s.Work, overrides) == edgeYes {
				out = append(out, s.Work)
			}
		}
	}
	for k, p := range overrides {
		if k[0] == string(w.ID) && p > 0 && allocEligible(w, WorkType(k[1]), overrides) == edgeYes {
			out = append(out, WorkType(k[1]))
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i] < out[j] })
	return compactWork(out)
}

func compactWork(in []WorkType) []WorkType {
	var out []WorkType
	for i, t := range in {
		if i == 0 || t != in[i-1] {
			out = append(out, t)
		}
	}
	return out
}
