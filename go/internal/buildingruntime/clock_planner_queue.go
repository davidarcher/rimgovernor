package buildingruntime

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// plannerQueue is the scheduler's due queue over the planner catalog
// (#625): per planner, the game tick its last evaluation stands until
// (plannerEntry.reviewEvery), the wake evidence it has yet to act on, and
// the dependency it reported waiting on. A step selects the planners that
// are dirty or due at its tick and skips those still waiting; the full
// step (FullStepEvery, the coarse reconciliation for missed invalidations)
// and an authority change run everything and clear the waits. Touched
// only under the player gate.
type plannerQueue struct {
	// due is the next review tick per planner, recorded when it ran; a
	// planner absent from it has not run since the last full step and is
	// not due on the clock (the full step covers it).
	due map[string]int64
	// dirty holds the planners a wake selected, each with the sequence
	// of the mark, so a wave clears only the marks that preceded it.
	dirty map[string]uint64
	seq   uint64
	// all is set by evidence that can change anything (an authority
	// change, an outcome whose kind the scheduler never armed): the next
	// planner wave runs everything, waits included.
	all bool
	// waits are the planners that reported existing work of their kinds
	// still open, keyed by name: none is re-run until one of the attempts
	// it waits on reaches its outcome row, its tick deadline passes, or a
	// full step runs.
	waits map[string]plannerWait
	// configured filters the catalog to the planners the scheduler runs
	// (plannerEntry.configured); nil admits every entry. An unconfigured
	// planner is never marked or due, so it cannot keep a step reviewing.
	configured func(plannerEntry) bool
	// catalog is the planner table the queue tracks (the scheduler's, which
	// a test may substitute); nil means plannerCatalog.
	catalog func() []plannerEntry
}

// plannerWait is one planner's dependency: the open attempts of its kinds
// when it last ran, and the tick past which it is re-evaluated regardless.
type plannerWait struct {
	On       []domain.ActionID
	Deadline int64
}

func newPlannerQueue() *plannerQueue {
	return &plannerQueue{due: map[string]int64{}, dirty: map[string]uint64{}, waits: map[string]plannerWait{}}
}

// clone copies the queue for a selection that must not persist its marks
// (plannerSelection previews the step's selection before the bundle read).
func (q *plannerQueue) clone() *plannerQueue {
	out := newPlannerQueue()
	if q == nil {
		return out
	}
	for name, tick := range q.due {
		out.due[name] = tick
	}
	for name, mark := range q.dirty {
		out.dirty[name] = mark
	}
	for name, wait := range q.waits {
		out.waits[name] = wait
	}
	out.seq, out.all, out.configured, out.catalog = q.seq, q.all, q.configured, q.catalog
	return out
}

// entries lists the catalog planners the queue tracks.
func (q *plannerQueue) entries() []plannerEntry {
	catalog := plannerCatalog
	if q.catalog != nil {
		catalog = q.catalog()
	}
	if q.configured == nil {
		return catalog
	}
	var out []plannerEntry
	for _, entry := range catalog {
		if q.configured(entry) {
			out = append(out, entry)
		}
	}
	return out
}

// wake folds one wake reason into the queue: the planners of the latched
// outcomes' kinds and the consumers of the dirty sections are marked, an
// outcome releases the waits naming its attempt, and authority (or an
// outcome of unknown kind) marks everything. A wake carrying nothing
// selectable (a watch stop without an outcome) is a settled window: every
// planner is marked. Families without sections on the reason are widened
// to their sections; a sectionless family (world) matches by name.
func (q *plannerQueue) wake(reason StepReason, kindOf func(domain.ActionID) (domain.ActionKind, bool)) {
	if reason.Authority {
		q.all = true
	}
	kinds := map[domain.ActionKind]bool{}
	for _, event := range reason.Events {
		if event.Terminal {
			q.release(event.Action)
		}
		kind, known := kindOf(event.Action)
		if !known {
			q.all = true
			continue
		}
		kinds[kind] = true
	}
	sections := map[facts.Section]bool{}
	for _, section := range reason.Sections {
		sections[section] = true
	}
	families := map[bridge.FactFamily]bool{}
	for _, family := range reason.Families {
		families[family] = true
		if len(reason.Sections) == 0 {
			for _, section := range facts.FamilySections(family) {
				sections[section] = true
			}
		}
	}
	if len(kinds) == 0 && len(sections) == 0 && len(families) == 0 && !reason.Authority {
		q.markAll()
		return
	}
	for _, entry := range q.entries() {
		if q.matches(entry, kinds, sections, families) {
			q.mark(entry.name)
		}
	}
}

func (q *plannerQueue) matches(entry plannerEntry, kinds map[domain.ActionKind]bool, sections map[facts.Section]bool, families map[bridge.FactFamily]bool) bool {
	for _, kind := range entry.kinds {
		if kinds[kind] {
			return true
		}
	}
	for _, section := range entry.sections {
		if sections[section] {
			return true
		}
	}
	for _, family := range entry.families {
		if families[family] {
			return true
		}
	}
	return false
}

func (q *plannerQueue) mark(name string) {
	q.seq++
	q.dirty[name] = q.seq
}

// markAll marks every catalog planner dirty: the settled-window wake.
func (q *plannerQueue) markAll() {
	for _, entry := range q.entries() {
		q.mark(entry.name)
	}
}

// release drops every wait naming action: its outcome row landed.
func (q *plannerQueue) release(action domain.ActionID) {
	for name, wait := range q.waits {
		for _, on := range wait.On {
			if on == action {
				delete(q.waits, name)
				break
			}
		}
	}
}

// plannerSelectionResult is what a step's selection decided: whether any
// planner runs, the catalog filter (nil for every planner), the planners
// skipped as waiting on a dependency, and the marks the wave will clear.
type plannerSelectionResult struct {
	planners bool
	pick     func(plannerEntry) bool
	waiting  []string
	began    uint64
}

// selection decides the planners a step at tick runs. full runs every
// planner (a full step, or pending all-evidence) and clears the waits;
// settled marks every planner first (a window just ran). Otherwise the
// dirty and due planners run, less those whose wait stillOpen confirms
// (every attempt waited on still open) and whose deadline is ahead of
// tick; stillOpen nil confirms every wait. A wait whose attempt is no
// longer open is released here.
func (q *plannerQueue) selection(tick int64, full, settled bool, stillOpen func(domain.ActionID) bool) plannerSelectionResult {
	if full || q.all {
		return plannerSelectionResult{planners: true, began: q.seq}
	}
	if settled {
		q.markAll()
	}
	for name, wait := range q.waits {
		open := true
		if stillOpen != nil {
			for _, on := range wait.On {
				if !stillOpen(on) {
					open = false
				}
			}
		}
		if !open || wait.Deadline <= tick {
			delete(q.waits, name)
		}
	}
	chosen := map[string]bool{}
	var waiting []string
	for _, entry := range q.entries() {
		_, dirty := q.dirty[entry.name]
		due, ran := q.due[entry.name]
		if !dirty && !(ran && due <= tick) {
			continue
		}
		if _, waits := q.waits[entry.name]; waits {
			waiting = append(waiting, entry.name)
			continue
		}
		chosen[entry.name] = true
	}
	if len(chosen) == 0 {
		return plannerSelectionResult{waiting: waiting, began: q.seq}
	}
	return plannerSelectionResult{planners: true, pick: func(entry plannerEntry) bool { return chosen[entry.name] }, waiting: waiting, began: q.seq}
}

// ran records a wave: each planner that returned (names) gets its next
// review tick and its marks up to began cleared; one that reported
// existing work of its kinds (reasonOf, the wave's per-planner reason)
// waits on those attempts (openWork names them) until their outcome or
// its deadline; a full wave (pick nil) also clears the all-evidence flag
// and every wait. A planner that missed the wave keeps its marks.
func (q *plannerQueue) ran(sel plannerSelectionResult, names []string, reasonOf func(string) (RoutineBuildingReason, bool), tick int64, openWork func(kinds []domain.ActionKind) []domain.ActionID) {
	if sel.pick == nil {
		q.all = false
		q.waits = map[string]plannerWait{}
	}
	entries := map[string]plannerEntry{}
	for _, entry := range q.entries() {
		entries[entry.name] = entry
	}
	for _, name := range names {
		entry := entries[name]
		q.due[name] = tick + int64(entry.reviewEvery())
		if mark, ok := q.dirty[name]; ok && mark <= sel.began {
			delete(q.dirty, name)
		}
		reason, finished := reasonOf(name)
		if !finished || reason != BuildingMethodExistingWork || openWork == nil {
			delete(q.waits, name)
			continue
		}
		on := openWork(entry.kinds)
		if len(on) == 0 {
			delete(q.waits, name)
			continue
		}
		q.waits[name] = plannerWait{On: on, Deadline: q.due[name]}
	}
}

// waitingOn reports the wait recorded for name, for tests and the step row.
func (q *plannerQueue) waitingOn(name string) (plannerWait, bool) {
	wait, ok := q.waits[name]
	return wait, ok
}

// openWorkOfKinds names the open attempts of the given kinds across
// plans, in a stable order: the dependency a planner reporting existing
// work waits on.
func openWorkOfKinds(plans []store.PlanState, kinds []domain.ActionKind) []domain.ActionID {
	wanted := map[domain.ActionKind]bool{}
	for _, kind := range kinds {
		wanted[kind] = true
	}
	var out []domain.ActionID
	for _, plan := range plans {
		for _, progress := range plan.Progress {
			if !wanted[progress.Action().Kind()] || !progressOpen(progress) {
				continue
			}
			out = append(out, progress.View().Action)
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i] < out[j] })
	return out
}

// progressOpen is domain.GoalWorkOpen for one progress row.
func progressOpen(progress domain.Progress) bool {
	return domain.GoalWorkOpen([]domain.Progress{progress})
}

// openWorkIndex indexes every open attempt across plans, for the waits'
// stillOpen check.
func openWorkIndex(plans []store.PlanState) func(domain.ActionID) bool {
	open := map[domain.ActionID]bool{}
	for _, plan := range plans {
		for _, progress := range plan.Progress {
			if progressOpen(progress) {
				open[progress.View().Action] = true
			}
		}
	}
	return func(id domain.ActionID) bool { return open[id] }
}

// sectionsWanted is the union of the sections the selected planners
// declare, the sections a subset step reads at cadence (the rest are
// served held, #625); nil when every planner runs.
func (s *ClockScheduler) sectionsWanted(pick func(plannerEntry) bool) map[facts.Section]bool {
	if pick == nil {
		return nil
	}
	wanted := map[facts.Section]bool{}
	for _, entry := range s.catalog {
		if !entry.configured(&s.config) || !pick(entry) {
			continue
		}
		for _, section := range entry.sections {
			wanted[section] = true
		}
	}
	return wanted
}

// sectionNames lists a wanted set in report order, for the step row.
func sectionNames(wanted map[facts.Section]bool) []string {
	if wanted == nil {
		return nil
	}
	out := []string{}
	for _, section := range facts.Sections() {
		if wanted[section] {
			out = append(out, string(section))
		}
	}
	return out
}
