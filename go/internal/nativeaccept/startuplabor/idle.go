package startuplabor

import (
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Activity is what one sampled pawn was doing, derived from the job the
// sample carries. Only the non-work kinds are enumerated; any other named
// job is work, and a sample without a readable job is ActivityUnknown.
type Activity string

const (
	ActivityWork        Activity = "work"
	ActivityIdle        Activity = "idle"
	ActivitySleep       Activity = "sleep"
	ActivityMeal        Activity = "meal"
	ActivityRecreation  Activity = "recreation"
	ActivityMedical     Activity = "medical_rest"
	ActivityUnavailable Activity = "unavailable"
	ActivityUnknown     Activity = "unknown"
)

// idleJobs are the jobs that mean the pawn has nothing to do: RimWorld's
// own wait and wander drivers.
var idleJobs = map[string]bool{
	"Wait": true, "Wait_Wander": true, "Wait_MaintainPosture": true,
	"GotoWander": true, "Wait_WithSleeping": true,
}

// nonWorkJobs map a job to the need it is serving. A pawn on one of these
// is unavailable for work and its interval is excluded from idle time,
// never counted as starvation.
var nonWorkJobs = map[string]Activity{
	"LayDown":                  ActivitySleep,
	"LayDownAwake":             ActivitySleep,
	"Ingest":                   ActivityMeal,
	"SocialRelax":              ActivityRecreation,
	"ViewArt":                  ActivityRecreation,
	"Skygaze":                  ActivityRecreation,
	"StandAndBeSociallyActive": ActivityRecreation,
	"Meditate":                 ActivityRecreation,
	"VisitSickPawn":            ActivityRecreation,
	"Pray":                     ActivityRecreation,
}

// PawnSample is one bounded observation of one pawn, as a
// home/list_pawns row reports it. JobKnown false (the row carried no job
// field at all) is the only unknown: an absent job on a present row is a
// pawn with nothing to do.
type PawnSample struct {
	Pawn     string
	Tick     domain.Tick
	Job      string
	JobKnown bool
	Drafted  bool
	Downed   bool
	Mental   string
	InBed    bool
	// Restart marks the first sample after the service restarted: the
	// interval that ends here spans a boundary and is not credited.
	Restart bool
}

// Classify is the sample's activity. Drafted, downed and mental-state
// pawns are unavailable whatever job they carry; a downed pawn in bed is
// medical rest.
func (s PawnSample) Classify() Activity {
	if !s.JobKnown {
		return ActivityUnknown
	}
	if s.Downed {
		if s.InBed {
			return ActivityMedical
		}
		return ActivityUnavailable
	}
	if s.Drafted || s.Mental != "" {
		return ActivityUnavailable
	}
	job := strings.TrimSpace(s.Job)
	if job == "" || idleJobs[job] {
		return ActivityIdle
	}
	if a, ok := nonWorkJobs[job]; ok {
		if a == ActivitySleep && s.InBed && s.Downed {
			return ActivityMedical
		}
		return a
	}
	return ActivityWork
}

// DefaultMaxGap bounds how much of an unobserved stretch one sample may
// speak for: half a game hour. A longer gap credits this much and flags
// the remainder rather than assuming the sampled job persisted.
const DefaultMaxGap domain.Tick = 1250

// Gap is an observation gap longer than the bound: the interval, what was
// credited and what was left unattributed.
type Gap struct {
	Pawn                string
	From, To            domain.Tick
	Activity            Activity
	Credited, Unbounded domain.Tick
}

// Account is derived idle accounting over a run of samples. Every tick of
// every credited interval lands in exactly one bucket, so
// Idle+Work+Excluded+Unknown plus Unattributed equals the observed span
// the samples actually cover.
type Account struct {
	// Idle is available-pawn time with nothing to do: the epic's subject.
	Idle domain.Tick
	// Work is time on a job that is not a need.
	Work domain.Tick
	// Excluded is sleep, meals, recreation, medical rest and drafted or
	// otherwise unavailable time.
	Excluded domain.Tick
	// Unknown is time whose sample carried no readable job.
	Unknown domain.Tick
	// Unattributed is the part of over-long gaps no sample speaks for.
	Unattributed domain.Tick
	// ByActivity is the credited time per activity.
	ByActivity map[Activity]domain.Tick
	// Gaps, Rewinds and Restarts are the boundaries the accounting
	// refused to credit; they make the derived totals auditable.
	Gaps     []Gap
	Rewinds  int
	Restarts int
	// Samples is the number of samples the account read.
	Samples int
	// Pawns is the number of distinct pawns sampled.
	Pawns int
}

// Accumulate derives an Account from samples. Samples may arrive in any
// order and interleaved across pawns; they are grouped per pawn and
// ordered by tick within the sequence given, so a tick that goes backwards
// is a rewind (a reload or a fresh world), not a sort error.
//
// A sample credits its own activity to the interval up to the next sample
// of the same pawn. It credits nothing across a rewind, a restart
// boundary or the end of the run, nothing for a paused pair (equal
// ticks), and at most maxGap for an over-long gap.
func Accumulate(samples []PawnSample, maxGap domain.Tick) Account {
	if maxGap <= 0 {
		maxGap = DefaultMaxGap
	}
	a := Account{ByActivity: map[Activity]domain.Tick{}, Samples: len(samples)}
	byPawn := map[string][]PawnSample{}
	var order []string
	for _, s := range samples {
		if _, seen := byPawn[s.Pawn]; !seen {
			order = append(order, s.Pawn)
		}
		byPawn[s.Pawn] = append(byPawn[s.Pawn], s)
	}
	sort.Strings(order)
	a.Pawns = len(order)
	for _, pawn := range order {
		run := byPawn[pawn]
		for i := 0; i+1 < len(run); i++ {
			from, to := run[i], run[i+1]
			if to.Restart {
				a.Restarts++
				continue
			}
			if to.Tick < from.Tick {
				a.Rewinds++
				continue
			}
			span := to.Tick - from.Tick
			if span == 0 {
				continue
			}
			activity := from.Classify()
			credited := span
			if span > maxGap {
				credited = maxGap
				a.Unattributed += span - maxGap
				a.Gaps = append(a.Gaps, Gap{Pawn: pawn, From: from.Tick, To: to.Tick, Activity: activity, Credited: credited, Unbounded: span - maxGap})
			}
			a.ByActivity[activity] += credited
			switch activity {
			case ActivityIdle:
				a.Idle += credited
			case ActivityWork:
				a.Work += credited
			case ActivityUnknown:
				a.Unknown += credited
			default:
				a.Excluded += credited
			}
		}
	}
	return a
}

// Row renders the account for the report artifact.
func (a Account) Row() map[string]any {
	by := map[string]int64{}
	for k, v := range a.ByActivity {
		by[string(k)] = int64(v)
	}
	gaps := make([]map[string]any, 0, len(a.Gaps))
	for _, g := range a.Gaps {
		gaps = append(gaps, map[string]any{"pawn": g.Pawn, "from": g.From, "to": g.To,
			"activity": string(g.Activity), "credited": g.Credited, "unbounded": g.Unbounded})
	}
	row := map[string]any{
		"idle_ticks": a.Idle, "work_ticks": a.Work, "excluded_ticks": a.Excluded,
		"unknown_ticks": a.Unknown, "unattributed_ticks": a.Unattributed,
		"by_activity": by, "samples": a.Samples, "pawns": a.Pawns,
		"rewinds": a.Rewinds, "restarts": a.Restarts,
	}
	if len(gaps) > 0 {
		row["bounded_gaps"] = gaps
	}
	return row
}
