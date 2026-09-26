package policy

import (
	"errors"
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// GoalID identifies a maintained need, independently of any one executable plan.
type GoalID = domain.GoalID
type GoalSource = domain.GoalSource

const (
	AutopilotGoal = domain.AutopilotGoal
	PlayerGoal    = domain.PlayerGoal
	AdviserGoal   = domain.AdviserGoal
)

type DevelopmentGoal struct {
	ID                          GoalID
	Source                      GoalSource
	Priority                    int
	Deficit                     domain.Fact[float64]
	Cancelled, Blocked, Comfort bool
	MethodUnavailable           bool
	// Served: the goal has a method on record (an active plan under any of
	// its epochs). A startup-survival goal (priority class 0-2) holds comfort
	// back only until it is served or declared monitoring-only, so a colony
	// whose fields are planted and campfire lit may furnish a table while
	// the food latch is still open.
	Served bool
	// Labor is the goal's profile (GoalLabor); nil means no pawn work.
	Labor LaborProfile
	// Risk is observed exposure of the goal's work (0 none .. 1 unsafe), from
	// RoutineDevelopmentRisk; unknown risk neither penalises nor defers.
	Risk domain.Fact[float64]
}

// DevelopmentWeights are the ranking's policy ordering terms. They order
// candidates for bounded admission; none is a measured benefit, a labor
// forecast or a completion-time estimate.
type DevelopmentWeights struct {
	// Deficit scales the 0..1 observed deficit fraction.
	Deficit float64
	// Player is the flat preference for player-sourced goals.
	Player float64
	// AgeTicks is the game-tick wait that earns one point.
	AgeTicks float64
	// Hysteresis keeps a previously selected goal ahead of near ties.
	Hysteresis float64
	// Bottleneck penalises goals whose profile labor is scarce relative to
	// the eligible goals competing for it (lead time: contested scarce labor
	// finishes later and frees its slot later).
	Bottleneck float64
	// Risk scales the observed 0..1 exposure penalty; risk of 1 defers.
	Risk float64
}

func DefaultDevelopmentWeights() DevelopmentWeights {
	return DevelopmentWeights{Deficit: 100, Player: 100, AgeTicks: 1000, Hysteresis: 20, Bottleneck: 30, Risk: 40}
}

func (w DevelopmentWeights) valid() bool {
	for _, v := range []float64{w.Deficit, w.Player, w.AgeTicks, w.Hysteresis, w.Bottleneck, w.Risk} {
		if math.IsNaN(v) || math.IsInf(v, 0) || v < 0 || v > 1e6 {
			return false
		}
	}
	return w.AgeTicks > 0
}

// Commitment refers to existing shared action progress, never a receipt-derived
// claim of completion. All accepted player projects consume optional capacity.
type Commitment struct {
	Goal     GoalID
	Source   GoalSource
	Priority int
	Progress domain.Progress
	// Labor is the work the open commitment already occupies (GoalLabor for
	// routine goals, construction for player building projects).
	Labor LaborProfile
	// Dispatched is the tick of the open action's latest dispatch, when the
	// caller knows it; a pending effect older than DevelopmentStallTicks is
	// a stalled commitment and holds no capacity or labor.
	Dispatched domain.Fact[domain.Tick]
	// Targets are the open action's work targets (ActionWorkTargets);
	// unknown keeps the work-type labor evidence.
	Targets domain.Fact[WorkTargets]
}

// DevelopmentStallTicks: one game day. colony-6 held a development slot for
// two days on a wild healroot harvest nobody picked up.
const DevelopmentStallTicks domain.Tick = 60000

// DevelopmentIdleTicks: one game hour. A commitment whose labor has idled
// (laborIdle) across reviews spanning this long releases its slot; the
// bound outlasts a pawn's walk between two designated trees or the haul
// after a finished frame, not a project nobody picks up (#445: tribal8
// held both slots for days on a wood cut and a herbal bill while the
// colonists built and hauled).
const DevelopmentIdleTicks domain.Tick = 2500

// Stalled reports a dispatched commitment whose effect has stayed pending
// past DevelopmentStallTicks at tick now.
func (c Commitment) Stalled(now domain.Tick) bool {
	since, known := c.Dispatched.Value()
	effect, effectKnown := c.Progress.View().Effect.Value()
	return known && effectKnown && effect == domain.EffectPending && now-since > DevelopmentStallTicks
}

type DevelopmentReason string

const (
	DevelopmentCancelled DevelopmentReason = "cancelled"
	DevelopmentAdviser   DevelopmentReason = "adviser"
	DevelopmentEmergency DevelopmentReason = "emergency"
	DevelopmentStartup   DevelopmentReason = "startup_survival"
	DevelopmentBlocked   DevelopmentReason = "blocked"
	DevelopmentCommitted DevelopmentReason = "existing_commitment"
	// DevelopmentLaborIdle: the goal's open work holds no slot because the
	// labor its profile names has idled past DevelopmentIdleTicks; the work
	// stays open and takes a slot back when a pawn picks it up.
	DevelopmentLaborIdle         DevelopmentReason = "labor_idle"
	DevelopmentWorkersUnknown    DevelopmentReason = "workers_unknown"
	DevelopmentNoWorkers         DevelopmentReason = "no_workers"
	DevelopmentUnknown           DevelopmentReason = "deficit_unknown"
	DevelopmentCapacity          DevelopmentReason = "capacity_committed"
	DevelopmentMethodUnavailable DevelopmentReason = "method_unavailable"
	// DevelopmentLabor: every work type in the goal's profile is already
	// occupied by committed work or has no enabled pawn; Bottleneck names one.
	DevelopmentLabor DevelopmentReason = "labor_unavailable"
	// DevelopmentRisk: the goal's work is observed unsafe (risk 1) this review.
	DevelopmentRisk DevelopmentReason = "risk_deferred"
	// DevelopmentDisabled: the review ran without authority (Manual, a
	// player interruption, a restart before authority returned); the row
	// keeps its waiting age from the last ranking but nothing is selected.
	DevelopmentDisabled DevelopmentReason = "control_disabled"
	// DevelopmentStage: the colony stage holds the project (#630): at
	// Foothold with the shelter unmet, the comfort-class development
	// (StageDevelopmentGoal) waits for the builder to raise the shelter.
	DevelopmentStage DevelopmentReason = "stage_foothold"
)

type DevelopmentRow struct {
	Goal                GoalID
	Score               float64
	Deficit             domain.Fact[float64]
	WaitingSince        domain.Tick
	Selected, Committed bool
	Reason              DevelopmentReason
	Bottleneck          WorkType
	Risk                domain.Fact[float64]
	// Idle: the goal held a slot through a review and its planner committed
	// nothing. An idle goal keeps its waiting age but no hysteresis, and
	// ranks behind every other eligible goal until each of them has been
	// idle too (then the round restarts on score), so a planner with no
	// method this review (colony-3: EnsureDefensiveLayout and
	// MaintainAnimalFeed held both slots for a game day) hands the slot on.
	Idle bool
	// Granted: the row took its slot from a planner's yield after this
	// review's ranking (YieldDevelopment). Its planner may not have run under
	// the selection, so the next review does not judge it idle.
	Granted bool
	// LaborIdleSince is the first review tick at which the goal's open
	// work found its labor idle (laborIdle), carried while it stays idle;
	// unknown while the work is picked up or the goal holds no open work.
	LaborIdleSince domain.Fact[domain.Tick]
	// LaborEvidence is this review's evidence for the goal's open work
	// (CommitmentLabor): attributed if any commitment's work is attended.
	LaborEvidence LaborEvidence
}

// DevelopmentState is a value snapshot owned by the review caller. Context and
// direction changes or tick rewinds discard age and selection history.
type DevelopmentState struct {
	Snapshot domain.GenerationSnapshot
	Tick     domain.Tick
	Workers  domain.Fact[int]
	// Labor is the observed per-work-type pawn census the ranking used;
	// unknown labor falls back to the coarse worker bound alone.
	Labor     domain.Fact[map[WorkType]int]
	Capacity  int
	Committed []GoalID
	Rows      []DevelopmentRow
	// Partial: the planner pass that followed this review ran only the
	// planners a wake named, so a selected goal whose planner did not run
	// is not judged idle by the next review.
	Partial bool
}

type DevelopmentRequest struct {
	Snapshot domain.GenerationSnapshot
	Tick     domain.Tick
	Workers  domain.Fact[int]
	Labor    domain.Fact[map[WorkType]int]
	// LaborUse is what the counted pawns are doing (RoutineLaborUse);
	// unknown releases no commitment.
	LaborUse domain.Fact[LaborUse]
	// Weights zero value uses DefaultDevelopmentWeights.
	Weights     DevelopmentWeights
	Limit       int
	Goals       []DevelopmentGoal
	Commitments []Commitment
	Previous    DevelopmentState
	// Partial marks this review's planner pass as a wake subset.
	Partial bool
	// Withheld is labor a blocked prerequisite keeps out of the ranked
	// queue (WithheldLabor): one pawn of each type is reserved before any
	// optional goal takes it, so a goal needing it reads labor_unavailable.
	Withheld LaborProfile
	// Stage is the colony stage record this review derived
	// (ReviewColonyStage); its Foothold hold refuses the comfort-class
	// development with DevelopmentStage.
	Stage ColonyStageRecord
}

func validGoal(id GoalID, source GoalSource, priority int) bool {
	return validResource(Resource(id)) && (source == AutopilotGoal || source == PlayerGoal || source == AdviserGoal) && priority >= 0 && priority <= 4
}

// RankDevelopment ports development_priorities.arbitrate. It grants selection
// slots only; native admission, shared resource reservations and Hands still apply.
func RankDevelopment(r DevelopmentRequest) (DevelopmentState, error) {
	if r.Snapshot.Validate() != nil || r.Tick < 0 || r.Limit < 1 || r.Limit > 8 || len(r.Goals) > 512 || len(r.Commitments) > 4096 {
		return DevelopmentState{}, errors.New("invalid development review")
	}
	weights := r.Weights
	if weights == (DevelopmentWeights{}) {
		weights = DefaultDevelopmentWeights()
	}
	if !weights.valid() {
		return DevelopmentState{}, errors.New("invalid development weights")
	}
	workers, knownWorkers := r.Workers.Value()
	if knownWorkers && (workers < 0 || workers > 4096) {
		return DevelopmentState{}, errors.New("invalid worker count")
	}
	if labor, known := r.Labor.Value(); known {
		if len(labor) > 256 {
			return DevelopmentState{}, errors.New("invalid labor census")
		}
		for w, n := range labor {
			if !validResource(Resource(w)) || n < 0 || n > 4096 {
				return DevelopmentState{}, errors.New("invalid labor census")
			}
		}
	}
	result := DevelopmentState{Snapshot: r.Snapshot, Tick: r.Tick, Workers: r.Workers, Labor: r.Labor, Capacity: min(r.Limit, workers)}
	result.Partial = r.Partial
	if !validLabor(r.Withheld) {
		return DevelopmentState{}, errors.New("invalid withheld labor")
	}
	ledger := newLaborLedger(r.Labor)
	for _, w := range r.Withheld {
		ledger.take(LaborProfile{w})
	}
	old := map[GoalID]DevelopmentRow{}
	if sameWorld(r.Previous.Snapshot, r.Snapshot) && r.Tick >= r.Previous.Tick {
		for _, row := range r.Previous.Rows {
			if row.WaitingSince < 0 || row.WaitingSince > r.Previous.Tick {
				return DevelopmentState{}, errors.New("invalid development history")
			}
			if _, duplicate := old[row.Goal]; duplicate {
				return DevelopmentState{}, errors.New("duplicate development history")
			}
			old[row.Goal] = row
		}
	}
	committed := map[GoalID]bool{}
	released := map[GoalID]bool{}
	idleSince := map[GoalID]domain.Tick{}
	evidence := map[GoalID]LaborEvidence{}
	seenActions := map[domain.ActionID]bool{}
	for _, c := range r.Commitments {
		v := c.Progress.View()
		if !validGoal(c.Goal, c.Source, c.Priority) || v.Stage == "" || seenActions[v.Action] || !validLabor(c.Labor) {
			return DevelopmentState{}, errors.New("invalid development commitment")
		}
		seenActions[v.Action] = true
		if c.Source == AdviserGoal || c.Source != PlayerGoal && c.Priority < 3 {
			continue
		}
		// Unknown effects retain capacity even after cancellation. A terminal
		// observed failure/absence releases it; a command receipt never does.
		if c.Stalled(r.Tick) {
			continue
		}
		if v.Unresolved || v.Stage == domain.Pending || v.Stage == domain.Prepared || v.Stage == domain.Dispatched || v.Stage == domain.AwaitingObservation {
			// Labor idle across reviews for DevelopmentIdleTicks releases
			// the slot without closing the work: nobody is picking the
			// work up, so the goal's row reads labor_idle until they do.
			// The evidence is target-linked (#643): a haul for a third
			// goal is not activity on this one. Unknown evidence (a
			// sleeping colony, an unattributable job) carries the
			// deadline without starting, resetting or completing it.
			e := CommitmentLabor(r.LaborUse, c.Labor, c.Targets)
			if evidenceRank(e) > evidenceRank(evidence[c.Goal]) {
				evidence[c.Goal] = e
			}
			since, carried := old[c.Goal].LaborIdleSince.Value()
			carried = carried && since <= r.Tick
			if e.Idle() || e == LaborUnknown && carried {
				if _, seen := idleSince[c.Goal]; !seen {
					idleSince[c.Goal] = r.Tick
					if carried {
						idleSince[c.Goal] = since
					}
				}
				if e.Idle() && r.Tick-idleSince[c.Goal] >= DevelopmentIdleTicks {
					released[c.Goal] = true
					continue
				}
			}
			if !committed[c.Goal] {
				ledger.take(c.Labor)
			}
			committed[c.Goal] = true
		}
	}
	for id := range committed {
		delete(released, id)
	}
	for id, e := range evidence {
		if e.Active() {
			delete(idleSince, id)
		}
	}
	for id := range committed {
		result.Committed = append(result.Committed, id)
	}
	sort.Slice(result.Committed, func(i, j int) bool { return result.Committed[i] < result.Committed[j] })
	emergency, startup := false, false
	seen := map[GoalID]bool{}
	for _, g := range r.Goals {
		fraction, known := g.Deficit.Value()
		risk, riskKnown := g.Risk.Value()
		if !validGoal(g.ID, g.Source, g.Priority) || seen[g.ID] || !validLabor(g.Labor) || known && (math.IsNaN(fraction) || math.IsInf(fraction, 0) || fraction < 0 || fraction > 1) || riskKnown && (math.IsNaN(risk) || risk < 0 || risk > 1) {
			return DevelopmentState{}, errors.New("invalid development goal")
		}
		seen[g.ID] = true
		// A mental break's mood goal is priority 1 but not an emergency: it
		// ends only as ticks pass, so it must not freeze development.
		emergency = emergency || g.Priority < 2 && !IsMoodGoal(g.ID)
		// MaintainRefrigeration sits at priority 2 only to bypass the ranked
		// queue (a cooler queued behind the project limit arrives after the
		// food is gone); it is upkeep, not a startup need, and a tribal
		// colony with no way to cool a room would otherwise hold comfort
		// back for as long as any berry is near spoiling (#217).
		startup = startup || g.Priority < 3 && !g.Served && !g.MethodUnavailable && !g.Cancelled && g.ID != MaintainRefrigeration
	}
	for _, g := range r.Goals {
		if g.Priority < 3 {
			continue
		}
		previous, exists := old[g.ID]
		since := r.Tick
		if exists {
			since = previous.WaitingSince
		}
		idle := exists && !committed[g.ID] && (previous.Selected && !previous.Committed && !previous.Granted && !r.Previous.Partial || previous.Idle && !previous.Selected)
		fraction, known := g.Deficit.Value()
		score := weights.Deficit*fraction + float64(r.Tick-since)/weights.AgeTicks
		if g.Source == PlayerGoal {
			score += weights.Player
		}
		if previous.Selected && !idle {
			score += weights.Hysteresis
		}
		risk, riskKnown := g.Risk.Value()
		if riskKnown {
			score -= weights.Risk * risk
		}
		row := DevelopmentRow{Goal: g.ID, Score: score, Deficit: g.Deficit, WaitingSince: since, Committed: committed[g.ID], Risk: g.Risk, Idle: idle}
		if since, seen := idleSince[g.ID]; seen && (committed[g.ID] || released[g.ID]) {
			row.LaborIdleSince = domain.Known(since)
		}
		if committed[g.ID] || released[g.ID] {
			row.LaborEvidence = evidence[g.ID]
		}
		switch {
		case g.Cancelled:
			row.Reason = DevelopmentCancelled
		case g.Source == AdviserGoal:
			row.Reason = DevelopmentAdviser
		case emergency:
			row.Reason = DevelopmentEmergency
		case g.Comfort && startup:
			row.Reason = DevelopmentStartup
		case g.Blocked:
			row.Reason = DevelopmentBlocked
		case row.Committed:
			row.Reason = DevelopmentCommitted
		case released[g.ID]:
			row.Reason = DevelopmentLaborIdle
		case g.MethodUnavailable:
			row.Reason = DevelopmentMethodUnavailable
		case riskKnown && risk >= 1:
			row.Reason = DevelopmentRisk
		case !knownWorkers:
			row.Reason = DevelopmentWorkersUnknown
		case workers == 0:
			row.Reason = DevelopmentNoWorkers
		case !known:
			row.Reason = DevelopmentUnknown
		}
		if row.Committed || released[g.ID] {
			row.WaitingSince = r.Tick
		}
		result.Rows = append(result.Rows, row)
	}
	profiles := map[GoalID]LaborProfile{}
	for _, g := range r.Goals {
		profiles[g.ID] = g.Labor
	}
	// Bottleneck: eligible goals competing for the same scarce labor. The
	// ratio uses free labor after commitments against the number of eligible
	// goals whose profiles share a type, so it is order-independent.
	competing := map[WorkType]int{}
	for _, row := range result.Rows {
		if row.Reason == "" {
			for _, w := range profiles[row.Goal] {
				competing[w]++
			}
		}
	}
	for i := range result.Rows {
		row := &result.Rows[i]
		profile := profiles[row.Goal]
		if row.Reason != "" || len(profile) == 0 || !ledger.known {
			row.Score = math.RoundToEven(max(0, row.Score)*1000) / 1000
			continue
		}
		free, contenders := 0, 0
		for _, w := range profile {
			free += ledger.free[w]
			contenders = max(contenders, competing[w])
		}
		ratio := min(1, float64(free)/float64(max(1, contenders)))
		row.Score = math.RoundToEven(max(0, row.Score-weights.Bottleneck*(1-ratio))*1000) / 1000
	}
	sort.Slice(result.Rows, func(i, j int) bool {
		a, b := result.Rows[i], result.Rows[j]
		// An idle selection yields to every other goal before score; the
		// tier is a total order so rows carrying a reason cannot form a
		// cycle between an idle and a non-idle eligible row.
		if tierA, tierB := a.Reason == "" && a.Idle, b.Reason == "" && b.Idle; tierA != tierB {
			return !tierA
		}
		if a.Score != b.Score {
			return a.Score > b.Score
		}
		return a.Goal < b.Goal
	})
	free := max(0, result.Capacity-len(committed))
	// A round ends once every eligible goal has been idle: the flags clear
	// and score order restarts.
	eligible, idle := 0, 0
	for _, row := range result.Rows {
		if row.Reason == "" {
			eligible++
			if row.Idle {
				idle++
			}
		}
	}
	if eligible > 0 && idle == eligible {
		for i := range result.Rows {
			if result.Rows[i].Reason == "" {
				result.Rows[i].Idle = false
			}
		}
		sort.SliceStable(result.Rows, func(i, j int) bool {
			a, b := result.Rows[i], result.Rows[j]
			if a.Score != b.Score {
				return a.Score > b.Score
			}
			return a.Goal < b.Goal
		})
	}
	for i := range result.Rows {
		row := &result.Rows[i]
		if row.Reason != "" {
			continue
		}
		if free == 0 {
			row.Reason = DevelopmentCapacity
			continue
		}
		// The Foothold hold (#630) is the last reason: a held project still
		// names the labor it lacks, and never claims labor it may not use.
		if r.Stage.HoldsDevelopment() && StageDevelopmentGoal(row.Goal) {
			if bottleneck, ok := ledger.peek(profiles[row.Goal]); !ok {
				row.Reason, row.Bottleneck = DevelopmentLabor, bottleneck
				continue
			}
			row.Reason = DevelopmentStage
			continue
		}
		if bottleneck, ok := ledger.take(profiles[row.Goal]); !ok {
			row.Reason, row.Bottleneck = DevelopmentLabor, bottleneck
			continue
		}
		row.Selected = true
		free--
	}
	return result, nil
}

// YieldDevelopment lets a planner whose selected goal has no method this
// review (retries exhausted, every fallback refused) hand its unused slot to
// the next capacity-deferred goal in this same review, without inflating
// waiting age. The yielding row reads method_unavailable and is idle, so the
// next review ranks it behind the goals it yielded to, as an unused
// selection would have been; the recipient is Granted, so the next review
// does not judge it idle for a slot its planner may never have run under.
// Labor released by the yielding goal is unknown here, so a labor-deferred
// candidate waits for the next review's fresh census. A goal that is not
// selected yields nothing.
func YieldDevelopment(state DevelopmentState, goal GoalID) DevelopmentState {
	state.Rows = append([]DevelopmentRow(nil), state.Rows...)
	state.Committed = append([]GoalID(nil), state.Committed...)
	for i := range state.Rows {
		if state.Rows[i].Goal != goal || !state.Rows[i].Selected {
			continue
		}
		state.Rows[i].Selected = false
		state.Rows[i].Granted = false
		state.Rows[i].Reason = DevelopmentMethodUnavailable
		state.Rows[i].Idle = true
		for j := range state.Rows {
			if state.Rows[j].Reason == DevelopmentCapacity {
				state.Rows[j].Selected = true
				state.Rows[j].Granted = true
				state.Rows[j].Reason = ""
				break
			}
		}
		break
	}
	return state
}
