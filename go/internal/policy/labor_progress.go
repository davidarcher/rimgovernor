package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// JobTarget is the thing or cell a pawn's job works (the job's targetA, or
// the frame or bench a delivery feeds). A zero JobTarget is a job with no
// target; Cell is the thing's cell when it is spawned.
type JobTarget struct {
	Thing string
	Cell  domain.Fact[domain.Cell]
}

// WorkTargets are the native things and cells an open action's pawn work
// lands on: a job aimed at any of them works the action (#643).
type WorkTargets struct {
	Things []string
	Cells  []domain.Cell
}

// ActionWorkTargets names the targets of actions whose pawn work aims at the
// action's own thing or cell. Kinds whose work lands elsewhere (a sown zone,
// a stockpile, a queued clean) are unknown, and keep the work-type evidence.
func ActionWorkTargets(a domain.Action) domain.Fact[WorkTargets] {
	var t WorkTargets
	if v, ok := a.Haul(); ok {
		t = WorkTargets{Things: []string{v.Thing()}, Cells: []domain.Cell{v.Cell()}}
	} else if v, ok := a.CutPlant(); ok {
		t = WorkTargets{Things: []string{v.Plant()}, Cells: []domain.Cell{v.Cell()}}
	} else if v, ok := a.Acquisition(); ok {
		t = WorkTargets{Things: []string{v.Thing()}, Cells: []domain.Cell{v.Cell()}}
	} else if v, ok := a.MineAcquisition(); ok {
		t = WorkTargets{Things: []string{v.Thing()}, Cells: []domain.Cell{v.Cell()}}
	} else if v, ok := a.Building(); ok {
		t = WorkTargets{Cells: []domain.Cell{v.Cell()}}
	} else if v, ok := a.Repair(); ok {
		t = WorkTargets{Things: []string{v.Structure()}, Cells: []domain.Cell{v.Cell()}}
	} else if v, ok := a.Deconstruction(); ok {
		t = WorkTargets{Things: []string{v.Target()}, Cells: []domain.Cell{v.Cell()}}
	} else if v, ok := a.Excavation(); ok {
		t = WorkTargets{Cells: []domain.Cell{v.Cell()}}
	} else if v, ok := a.CoverClearance(); ok {
		t = WorkTargets{Things: []string{v.Thing()}, Cells: []domain.Cell{v.Cell()}}
	} else if v, ok := a.Waste(); ok {
		t = WorkTargets{Things: []string{v.Target()}, Cells: []domain.Cell{v.Cell()}}
	} else if v, ok := a.SupplyAllow(); ok && a.Kind() == domain.SupplyAllowAction {
		t = WorkTargets{Things: []string{v.Thing()}, Cells: []domain.Cell{v.Cell()}}
	} else if v, ok := a.ProductionBill(); ok && v.Bench() != "" {
		t = WorkTargets{Things: []string{v.Bench()}}
	} else {
		return domain.Unknown[WorkTargets]()
	}
	return domain.Known(t)
}

// on reports a job aimed at one of the targets.
func (j JobTarget) on(t WorkTargets) bool {
	for _, thing := range t.Things {
		if j.Thing != "" && j.Thing == thing {
			return true
		}
	}
	if cell, known := j.Cell.Value(); known {
		for _, c := range t.Cells {
			if c == cell {
				return true
			}
		}
	}
	return false
}

// LaborEvidence classifies one review's evidence about whether anyone works
// a commitment. Attributed and WorkTypeBusy are activity (a pawn on the work,
// not proof its output advances: Commitment.Stalled bounds that);
// Unattributed and WorkTypeIdle are observed lack of it; Unknown (a sleeping
// colony, an unknown census, an unattributable job) is neither and neither
// starts nor resets the idle deadline.
type LaborEvidence string

const (
	// LaborAttributed: a pawn's job aims at the commitment's targets.
	LaborAttributed LaborEvidence = "attributed"
	// LaborUnattributed: pawns able to do the profile's work idle or work
	// other targets, nobody works this commitment's.
	LaborUnattributed LaborEvidence = "unattributed"
	// LaborWorkTypeBusy / LaborWorkTypeIdle: the pre-#643 work-type
	// evidence, used when the commitment's targets or a relevant job's
	// target are unknown (older producers, untargeted action kinds).
	LaborWorkTypeBusy LaborEvidence = "work_type_busy"
	LaborWorkTypeIdle LaborEvidence = "work_type_idle"
	LaborUnknown      LaborEvidence = "unknown"
)

// Idle reports evidence that nobody works the commitment.
func (e LaborEvidence) Idle() bool { return e == LaborUnattributed || e == LaborWorkTypeIdle }

// Active reports evidence that someone works the commitment.
func (e LaborEvidence) Active() bool { return e == LaborAttributed || e == LaborWorkTypeBusy }

func validLaborEvidence(e LaborEvidence) bool {
	switch e {
	case "", LaborAttributed, LaborUnattributed, LaborWorkTypeBusy, LaborWorkTypeIdle, LaborUnknown:
		return true
	}
	return false
}

// CommitmentLabor classifies this review's evidence for one commitment. With
// known targets and a census that carries every busy job, a job aimed at a
// target (any work type) is attributed; a same-profile job aimed elsewhere
// (a haul for a third goal) counts as idle labor, not activity. A
// profile-typed job with an unknown target, unknown targets, or a census
// without jobs falls back to the work-type rule.
func CommitmentLabor(use domain.Fact[LaborUse], profile LaborProfile, targets domain.Fact[WorkTargets]) LaborEvidence {
	v, known := use.Value()
	if !known || len(profile) == 0 {
		return LaborUnknown
	}
	inProfile := func(w WorkType) bool {
		for _, p := range profile {
			if p == w {
				return true
			}
		}
		return false
	}
	busy := 0
	for _, n := range v.Busy {
		busy += n
	}
	if want, targeted := targets.Value(); targeted && busy == len(v.Jobs) {
		idle, attributable := 0, true
		for _, w := range profile {
			idle += v.Idle[w]
		}
		for _, job := range v.Jobs {
			t, tk := job.Target.Value()
			switch {
			case tk && t.on(want):
				return LaborAttributed
			case !inProfile(job.Work):
			case !tk:
				attributable = false
			default:
				idle++
			}
		}
		if attributable {
			if idle > 0 {
				return LaborUnattributed
			}
			return LaborUnknown
		}
	}
	for _, w := range profile {
		if v.Busy[w] > 0 {
			return LaborWorkTypeBusy
		}
	}
	if laborIdle(use, profile) {
		return LaborWorkTypeIdle
	}
	return LaborUnknown
}

// evidenceRank orders one goal's commitments' evidence: any activity wins,
// then observed idleness, then unknown.
func evidenceRank(e LaborEvidence) int {
	switch {
	case e.Active():
		return 3
	case e.Idle():
		return 2
	case e == LaborUnknown:
		return 1
	}
	return 0
}
