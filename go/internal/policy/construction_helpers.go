package policy

import (
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Construction helpers: a pawn under the Construction floor (4) is
// not a constructor, but while suitable construction is ready and unmet
// and the pawn has been idle across two reviews, the roster planner enables
// Construction for it at the lowest rank (4 under manual priorities) beside
// the skilled owners, who stay first.
//
// Enforcement boundary: a work-type priority enables every native
// construction job, not one chosen wall. Native RimWorld still refuses a
// frame whose constructionSkillPrerequisite the pawn lacks. Quality sites
// with an observed finishing minimum also enforce that minimum natively.
// Helper safety is derived from observed facts per definition, with no name
// list: a definition is helper work when its quality sensitivity is observed
// false and its skill prerequisite is observed (the helper must reach it), and
// a quality-sensitive definition is protected when its sites carry an observed
// finishing minimum, which native enforces per target (helpers then neither
// count as demand nor constrain the floor). An unobserved prerequisite, an
// unobserved quality fact or an unconfigured quality site withholds helpers
// with a reason naming that fact. A new unconfigured site between reviews
// still follows vanilla rules until Go adopts it on a later review.

// ConstructionHelpHoldTicks: helpers stay one game hour after suitable
// demand disappears, so a review between two walls does not rewrite work
// settings.
const ConstructionHelpHoldTicks domain.Tick = domain.TicksPerHour

// Helper reasons recorded on ConstructionHelpRecord.Reason.
const (
	HelpAssigned      = "helping"
	HelpHeld          = "held_after_demand"
	HelpNoDemand      = "no_unmet_suitable_construction"
	HelpDemandUnknown = "construction_demand_unknown"
	HelpNoSpare       = "no_sustained_idle_pawn"
	// Withholding reasons name the unobserved or unconfigured fact.
	HelpPrerequisiteUnknown = "construction_prerequisite_unknown"
	HelpQualityUnknown      = "construction_quality_unknown"
	HelpQualityUnprotected  = "construction_quality_unprotected"
)

// helpWithholdRank orders withholding reasons when several hold.
var helpWithholdRank = map[string]int{HelpPrerequisiteUnknown: 1, HelpQualityUnknown: 2, HelpQualityUnprotected: 3}

// HelpDefinition is an open plan's building definition with its observed
// native skill prerequisite.
type HelpDefinition struct {
	Name  string
	Skill domain.Fact[int]
}

// ConstructionHelp is the planner's helper input (WorkDemand.Help).
type ConstructionHelp struct {
	Tick domain.Tick
	// Floor is the highest observed skill prerequisite among the quality-free
	// definitions in play; a helper must reach it.
	Floor int
	// Ready is the runnable helper-eligible construction parallelism; unknown
	// never authorizes help.
	Ready domain.Fact[int]
	// Withheld names the definitions whose prerequisite or quality is
	// unobserved or unconfigured; Reason is the highest-ranked fact among them.
	Withheld []string
	Reason   string
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
	Withheld   []string `json:",omitempty"`
}

// ConstructionHelpDemand reads the ready-work report and the native
// construction census for helper demand. Each building definition in play (an
// observed site, an open plan or a construction candidate) is classified from
// observed facts: quality-free definitions with an observed prerequisite are
// helper work, their runnable candidates count their parallelism and their
// highest prerequisite becomes the helper floor; protected quality definitions
// are left to native per-target enforcement; anything else is withheld under
// the reason naming the missing fact. A report from another world is unknown.
func ConstructionHelpDemand(report *ReadyWorkReport, current domain.GenerationSnapshot, tick domain.Tick, definitions []HelpDefinition, previous *ConstructionHelpRecord, census domain.Fact[CurrentConstruction]) ConstructionHelp {
	help := ConstructionHelp{Tick: tick, Ready: domain.Unknown[int]()}
	report = ConstructionHelperView(report, census, current)
	if previous != nil && previous.Tick <= tick {
		help.Previous = *previous
	}
	withheld := map[string]string{}
	withhold := func(name, reason string) {
		if rank := helpWithholdRank[withheld[name]]; rank == 0 || helpWithholdRank[reason] < rank {
			withheld[name] = reason
		}
	}
	// Quality per definition: absent (no observed site, or no census) is unknown.
	const (
		free = iota
		protected
		unprotected
	)
	quality := map[string]int{}
	siteSkill := map[string]int{}
	siteSkillUnknown := map[string]bool{}
	if observed, known := census.Value(); known && observed.Colony {
		for _, site := range observed.Sites {
			name := site.Building.Definition()
			q := unprotected
			sensitive, sensitiveKnown := site.QualitySensitive.Value()
			if _, set := site.MinimumFinishingSkill.Value(); sensitiveKnown && !sensitive {
				q = free
			} else if sensitiveKnown && set {
				q = protected
			} else if !sensitiveKnown {
				withhold(name, HelpQualityUnknown)
			}
			quality[name] = max(quality[name], q)
			if skill, ok := site.NativeFinishingSkill.Value(); ok {
				siteSkill[name] = max(siteSkill[name], skill)
			} else {
				siteSkillUnknown[name] = true
			}
		}
	}
	skills := map[string]domain.Fact[int]{}
	names := map[string]bool{}
	for _, d := range definitions {
		skills[d.Name] = d.Skill
		names[d.Name] = true
	}
	for name := range quality {
		names[name] = true
	}
	candidateDefs := map[string]string{} // stage -> definition
	if report != nil && report.Current(current) {
		for _, c := range report.Candidates {
			builds := false
			for _, w := range c.Work {
				builds = builds || w == WorkConstruction
			}
			if !builds {
				continue
			}
			def, migrated := strings.CutPrefix(c.Stage, "building:")
			if !migrated {
				withhold(c.Stage, HelpPrerequisiteUnknown)
				continue
			}
			candidateDefs[c.Stage] = def
			names[def] = true
		}
	}
	helperWork := map[string]bool{}
	for name := range names {
		if withheld[name] != "" {
			continue
		}
		q, observed := quality[name]
		switch {
		case !observed:
			withhold(name, HelpQualityUnknown)
		case q == unprotected:
			withhold(name, HelpQualityUnprotected)
		case q == free:
			skill, known := skills[name].Value()
			if !known && !siteSkillUnknown[name] {
				skill, known = siteSkill[name], true // a free definition has an observed site
			}
			if !known {
				withhold(name, HelpPrerequisiteUnknown)
				break
			}
			helperWork[name] = true
			help.Floor = max(help.Floor, skill)
		}
	}
	if report != nil && report.Current(current) {
		ready := 0
		for _, c := range report.Candidates {
			if def, ok := candidateDefs[c.Stage]; ok && helperWork[def] && c.State == ReadyRunnable {
				ready += c.Parallelism
			}
		}
		help.Ready = domain.Known(ready)
	}
	for name, reason := range withheld {
		help.Withheld = append(help.Withheld, name)
		if help.Reason == "" || helpWithholdRank[reason] < helpWithholdRank[help.Reason] {
			help.Reason = reason
		}
	}
	sort.Strings(help.Withheld)
	return help
}

// planConstructionHelp picks helpers among sub-floor pawns: able at the
// native floor (requirement minimum, else 0), not an owner, no player
// override, idle now and idle (or helping) at the previous review. Unmet
// demand is ready work beyond the owners not held by other work; previous helpers are kept first
// and through the hold after demand clears.
func planConstructionHelp(h ConstructionHelp, workers, owners []*workWorker, eligible func(*workWorker) bool) ConstructionHelpRecord {
	rec := ConstructionHelpRecord{Tick: h.Tick, Withheld: h.Withheld}
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
	case len(h.Withheld) > 0:
		rec.Reason = h.Reason
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
	level := func(w *workWorker) int { return w.profile.SkillFor(WorkConstruction).Level }
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

// idleJob reports a pawn job with no productive work.
func idleJob(job PawnJob) bool {
	switch job.Def {
	case "", "Wait", "Wait_Wander", "GotoWander", "Wait_MaintainPosture":
		return true
	}
	return false
}
