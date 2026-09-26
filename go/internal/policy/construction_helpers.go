package policy

import (
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Construction helpers (#653): a pawn under the Construction floor (4) is
// not a constructor, but while suitable construction is ready and unmet
// and the pawn has been idle across two reviews, the roster planner enables
// Construction for it at the lowest rank (4 under manual priorities) beside
// the skilled owners, who stay first.
//
// Enforcement boundary: a work-type priority enables every native
// construction job, not one chosen wall. Native RimWorld still refuses a
// frame whose constructionSkillPrerequisite the pawn lacks, but nothing
// native keeps a helper off a quality or expensive frame. So helpers are
// enabled only while every construction task the review knows of (ready
// work candidates and the open plans' building definitions) is in
// HelperConstructionDefinitions, and a review that finds any other task
// withdraws them at once, with no hysteresis. A risky frame placed between
// two reviews is open to an enabled helper until the next review; that is
// the limit of the coarse setting.

// HelperConstructionDefinitions is the conservative task set: Core
// buildables with no constructionSkillPrerequisite and no quality, whose
// failed attempt loses a share of cheap materials (stone blocks, wood).
// Beds, furniture and anything with quality or a skill minimum are
// excluded; an unknown definition is excluded.
var HelperConstructionDefinitions = map[string]bool{
	"Wall": true, "Door": true, "SleepingSpot": true, "DoubleSleepingSpot": true,
	"Campfire": true, "Sandbags": true, "PowerConduit": true,
}

// ConstructionHelpHoldTicks: helpers stay one game hour after suitable
// demand disappears, so a review between two walls does not rewrite work
// settings.
const ConstructionHelpHoldTicks domain.Tick = 2500

// Helper reasons recorded on ConstructionHelpRecord.Reason.
const (
	HelpAssigned      = "helping"
	HelpHeld          = "held_after_demand"
	HelpNoDemand      = "no_unmet_suitable_construction"
	HelpDemandUnknown = "construction_demand_unknown"
	HelpRiskyTask     = "risky_construction_pending"
	HelpNoSpare       = "no_sustained_idle_pawn"
)

// ConstructionHelp is the planner's helper input (WorkDemand.Help).
type ConstructionHelp struct {
	Tick domain.Tick
	// Ready is the runnable suitable construction parallelism; unknown
	// never authorizes help.
	Ready domain.Fact[int]
	// Risky lists pending construction outside the conservative set.
	Risky []string
	// Previous is the last review's record in the same world, zero when
	// none.
	Previous ConstructionHelpRecord
}

// ConstructionHelpRecord is what the planner decided and why; the roster
// report carries it so the next review's hysteresis and the dossier read
// the same row.
type ConstructionHelpRecord struct {
	Tick domain.Tick
	// Idle are the pawns seen idle this review (spare capacity).
	Idle []PawnID `json:",omitempty"`
	// Helpers are the pawns enabled for Construction below its floor.
	Helpers []PawnID `json:",omitempty"`
	// DemandTick is the last tick unmet suitable demand held.
	DemandTick domain.Tick `json:",omitempty"`
	Ready      int         `json:",omitempty"`
	Unmet      int         `json:",omitempty"`
	Reason     string
	Risky      []string `json:",omitempty"`
}

// ConstructionHelpDemand reads the ready-work report (#645) for helper
// demand: runnable Construction candidates on a conservative definition
// count their parallelism; any construction candidate outside the set (or
// a conservative-adapter candidate that may build) is risky, as is any
// pending definition outside it. A report from another world is unknown.
func ConstructionHelpDemand(report *ReadyWorkReport, current domain.GenerationSnapshot, tick domain.Tick, definitions []string, previous *ConstructionHelpRecord) ConstructionHelp {
	help := ConstructionHelp{Tick: tick, Ready: domain.Unknown[int]()}
	if previous != nil && previous.Tick <= tick {
		help.Previous = *previous
	}
	risky := map[string]bool{}
	for _, d := range definitions {
		if !HelperConstructionDefinitions[d] {
			risky[d] = true
		}
	}
	if report != nil && report.Current(current) {
		ready := 0
		for _, c := range report.Candidates {
			builds := false
			for _, w := range c.Work {
				builds = builds || w == WorkConstruction
			}
			if !builds {
				continue
			}
			def, migrated := strings.CutPrefix(c.Stage, "building:")
			if !migrated || !HelperConstructionDefinitions[def] || c.Adapter != ReadyMigrated {
				risky[c.Stage] = true
				continue
			}
			if c.State == ReadyRunnable {
				ready += c.Parallelism
			}
		}
		help.Ready = domain.Known(ready)
	}
	for d := range risky {
		help.Risky = append(help.Risky, d)
	}
	sort.Strings(help.Risky)
	return help
}

// planConstructionHelp picks helpers among sub-floor pawns: able at the
// native floor (requirement minimum, else 0), not an owner, no player
// override, idle now and idle (or helping) at the previous review. Unmet
// demand is ready work beyond the owners not held by other work; previous helpers are kept first
// and through the hold after demand clears.
func planConstructionHelp(h ConstructionHelp, workers, owners []*workWorker, eligible func(*workWorker) bool) ConstructionHelpRecord {
	rec := ConstructionHelpRecord{Tick: h.Tick, Risky: h.Risky}
	// An owner a giver of another type holds builds nothing this review.
	free := 0
	for _, o := range owners {
		if job, ok := o.pawn.Job.Value(); !ok || job.Work == "" || job.Work == WorkConstruction {
			free++
		}
	}
	idle := map[PawnID]bool{}
	for _, w := range workers {
		if job, ok := w.pawn.Job.Value(); ok && idleJob(job) {
			idle[w.pawn.ID] = true
			rec.Idle = append(rec.Idle, w.pawn.ID)
		}
	}
	ready, known := h.Ready.Value()
	switch {
	case !known:
		rec.Reason = HelpDemandUnknown
		return rec
	case len(h.Risky) > 0:
		rec.Reason = HelpRiskyTask
		return rec
	}
	rec.Ready = ready
	rec.Unmet = max(ready-free, 0)
	was := map[PawnID]bool{}
	for _, id := range h.Previous.Helpers {
		was[id] = true
	}
	wasIdle := map[PawnID]bool{}
	for _, id := range h.Previous.Idle {
		wasIdle[id] = true
	}
	want := rec.Unmet
	rec.Reason = HelpAssigned
	if want > 0 {
		rec.DemandTick = h.Tick
	} else if len(h.Previous.Helpers) > 0 && h.Previous.DemandTick > 0 && h.Tick-h.Previous.DemandTick < ConstructionHelpHoldTicks {
		rec.DemandTick = h.Previous.DemandTick
		want = len(h.Previous.Helpers)
		rec.Reason = HelpHeld
	} else {
		rec.Reason = HelpNoDemand
		return rec
	}
	var pool []*workWorker
	for _, w := range workers {
		if !eligible(w) {
			continue
		}
		// A helper keeps helping while eligible; a new one needs spare
		// capacity now and at the previous review.
		if was[w.pawn.ID] && (rec.Reason == HelpHeld || idle[w.pawn.ID] || busyBuilding(w)) || idle[w.pawn.ID] && wasIdle[w.pawn.ID] && rec.Reason == HelpAssigned {
			pool = append(pool, w)
		}
	}
	level := func(w *workWorker) int { return w.profile.Skill("Construction").Level }
	sort.Slice(pool, func(i, j int) bool {
		a, b := pool[i], pool[j]
		if was[a.pawn.ID] != was[b.pawn.ID] {
			return was[a.pawn.ID]
		}
		if level(a) != level(b) {
			return level(a) > level(b)
		}
		return a.pawn.ID < b.pawn.ID
	})
	for i := 0; i < len(pool) && i < want; i++ {
		rec.Helpers = append(rec.Helpers, pool[i].pawn.ID)
	}
	sort.Slice(rec.Helpers, func(i, j int) bool { return rec.Helpers[i] < rec.Helpers[j] })
	if len(rec.Helpers) == 0 {
		rec.Reason = HelpNoSpare
	}
	return rec
}

func busyBuilding(w *workWorker) bool {
	job, ok := w.pawn.Job.Value()
	return ok && job.Work == WorkConstruction
}
