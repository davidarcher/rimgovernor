package policy

import (
	"errors"
	"slices"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// GoalProgress is the one progress contract every active goal reports
// (#629): the method it is working, the native observable that proves the
// method is advancing, the last tick that observable moved, the tick by
// which it must move again and why it is not moving now. Progress is
// measured from native outcomes (a settled effect, a deficit that shrank,
// construction observed), never from dispatch: an order nobody can take is
// blocked, not progressing.
type ConcernProgress struct {
	Concern ConcernID
	// Method is the goal's current method or ladder rung ("hunt", "cook").
	Method string
	// Expected is the observable the method is expected to move.
	Expected string
	// LastProgress is the last review tick native evidence advanced Expected.
	LastProgress domain.Tick
	// NextReview is the tick the method is judged stalled unless Expected
	// advances first; zero when the contract sets no deadline.
	NextReview domain.Tick
	// Blocked names why Expected is not advancing (BlockedReason); empty
	// while the method is unblocked.
	Blocked BlockedReason
	// Cooldowns are the failed situations a rotation keyed, each bounded by
	// its Until tick; none is a permanent ban.
	Cooldowns []ProgressCooldown `json:",omitempty"`
	// Observed is the deficit fraction the last review measured, the
	// evidence a shrinking deficit is compared against.
	Observed *float64 `json:",omitempty"`
	// Planner is the goal's planner's latest refusal ("no_space"), empty
	// while it last admitted, found work or has not run (RecordPlannerReason).
	// A record with no method reads it as BlockedPlanner, so "no_method"
	// only means no planner has said why.
	Planner string `json:",omitempty"`
	// PlannerWaiting: Planner is a wait (the goal waits on something that is
	// not a failure) rather than a refusal; a record with no method reads it
	// as BlockedWaiting. Meaningless while Planner is empty.
	PlannerWaiting bool `json:",omitempty"`
	// Open: the goal has a method with unsettled work in flight, the
	// evidence a prerequisite is being worked rather than merely owed.
	Open bool `json:",omitempty"`
}

// BlockedReason says why a goal's expected observable is not advancing. A
// prerequisite reason carries the goal it waits on after a colon.
type BlockedReason string

const (
	// BlockedNoWorker: the method's work is issued but no available pawn is
	// capable of it, so the designation can never be taken.
	BlockedNoWorker BlockedReason = "no_worker"
	// BlockedNativeIneligible: native refuses the order (no storage accepts
	// the thing, no route reaches it).
	BlockedNativeIneligible BlockedReason = "native_ineligible"
	// BlockedReconciling: a write's outcome is unknown; the action identity
	// is reconciled before anything is retried.
	BlockedReconciling BlockedReason = "reconcile_write"
	// BlockedCooldown: every alternative method is under a cooldown.
	BlockedCooldown BlockedReason = "cooldown"
	// BlockedNoMethod: the goal has no method open and its planner proposed
	// none this review.
	BlockedNoMethod     BlockedReason = "no_method"
	blockedPrerequisite string        = "prerequisite:"
	blockedPlanner      string        = "planner:"
	blockedWaiting      string        = "waiting:"
	blockedHeld         string        = "held:"
	// Held reasons: the goal is intentionally not worked (HoldProgress).
	HeldStage       BlockedReason = "held:stage"
	HeldUnavailable BlockedReason = "held:unavailable"
	HeldOptIn       BlockedReason = "held:opt-in"
	HeldCapacity    BlockedReason = "held:capacity"
	HeldEmergency   BlockedReason = "held:emergency"
)

// BlockedPlanner blocks on the goal's planner's refusal reason.
func BlockedPlanner(reason string) BlockedReason {
	return BlockedReason(blockedPlanner + reason)
}

// BlockedWaiting is the goal's planner waiting on what the text says: not a
// failure, but shown so the goal says what it waits on.
func BlockedWaiting(text string) BlockedReason {
	return BlockedReason(blockedWaiting + text)
}

// Waiting reports a planner wait.
func (r BlockedReason) Waiting() bool {
	return strings.HasPrefix(string(r), blockedWaiting)
}

// PlannerNote is a planner's latest standing on a goal as it files on the
// goal's progress record: the zero note clears it (the planner admitted or
// found no deficit), Text is the plain-English refusal or wait, and Waiting
// marks a wait.
type PlannerNote struct {
	Text    string
	Waiting bool
}

// Blocked is the reason a record with no method reads the note as: nothing
// to do while cleared, a wait, or a planner refusal.
func (n PlannerNote) Blocked() BlockedReason {
	switch {
	case n.Text == "":
		return BlockedNoMethod
	case n.Waiting:
		return BlockedWaiting(n.Text)
	}
	return BlockedPlanner(n.Text)
}

// Planner is the record's filed planner note.
func (p ConcernProgress) PlannerNote() PlannerNote {
	return PlannerNote{Text: p.Planner, Waiting: p.PlannerWaiting}
}

// Unmethoded reports a record whose blocked reason only the planner's note
// can improve: no method, or a refusal or wait already filed from it.
func (r BlockedReason) Unmethoded() bool {
	return r == BlockedNoMethod || strings.HasPrefix(string(r), blockedPlanner) || r.Waiting()
}

// HeldLabor holds a goal whose work type is withheld or unavailable.
func HeldLabor(w WorkType) BlockedReason {
	return BlockedReason(blockedHeld + "labor:" + string(w))
}

// Held reports a goal intentionally not worked: a held: reason or a
// prerequisite another goal serves first.
func (r BlockedReason) Held() bool {
	return strings.HasPrefix(string(r), blockedHeld) || r.Prerequisite() != ""
}

// Actionable reports a blocked goal someone should look at: blocked and
// not held and not merely waiting.
func (r BlockedReason) Actionable() bool {
	return r != "" && !r.Held() && !r.Waiting()
}

// printableReason bounds a free-text reason suffix to short printable ASCII
// (spaces allowed: planner reasons are sentences).
func printableReason(s string) bool {
	if s == "" || len(s) > 96 {
		return false
	}
	for i := 0; i < len(s); i++ {
		if s[i] < 0x20 || s[i] > 0x7e {
			return false
		}
	}
	return true
}

// BlockedPrerequisite blocks on another goal that must be served first.
func BlockedPrerequisite(goal ConcernID) BlockedReason {
	return BlockedReason(blockedPrerequisite + string(goal))
}

// Prerequisite is the goal a prerequisite reason waits on, else "".
func (r BlockedReason) Prerequisite() ConcernID {
	if strings.HasPrefix(string(r), blockedPrerequisite) {
		return ConcernID(strings.TrimPrefix(string(r), blockedPrerequisite))
	}
	return ""
}

func (r BlockedReason) valid() bool {
	switch r {
	case "", BlockedNoWorker, BlockedNativeIneligible, BlockedReconciling, BlockedCooldown, BlockedNoMethod:
		return true
	}
	for _, prefix := range []string{blockedPlanner, blockedWaiting, blockedHeld} {
		if strings.HasPrefix(string(r), prefix) {
			return printableReason(strings.TrimPrefix(string(r), prefix))
		}
	}
	return r.Prerequisite() != "" && validResource(Resource(r.Prerequisite()))
}

// ProgressCooldown keeps one failed situation (a prey, a haul target, a
// route, a season) off the table until Until.
type ProgressCooldown struct {
	Key   string
	Until domain.Tick
}

// ProgressCooldownMax bounds every cooldown to three game days: a failed
// situation is retried, never banned.
const ProgressCooldownMax domain.Tick = 180000

// ProgressContract is what one method promises: the observable it moves,
// how long it may go without moving it (Deadline, zero for no bound) and
// how long a situation it failed in stays keyed out (Cooldown, bounded by
// ProgressCooldownMax; zero uses the deadline).
type ProgressContract struct {
	Method   string
	Expected string
	Deadline domain.Tick
	Cooldown domain.Tick
}

// Expired reports a method whose observable last moved at since and has
// exhausted its deadline at now; a contract without a deadline never expires.
func (c ProgressContract) Expired(since, now domain.Tick) bool {
	return c.Deadline > 0 && now-since >= c.Deadline
}

func (c ProgressContract) cooldown() domain.Tick {
	d := c.Cooldown
	if d <= 0 {
		d = c.Deadline
	}
	return min(max(d, 0), ProgressCooldownMax)
}

// CooldownUntil is the tick a situation failed at now stays keyed out to.
func (c ProgressContract) CooldownUntil(now domain.Tick) domain.Tick {
	return now + c.cooldown()
}

// HuntProgress is the hunt stall policy as a contract: a hunt designation
// the census reports untaken for HuntStallTicks since native first saw it
// (#1044) is withdrawn, so the planner tries other prey or a non-hunt
// source.
func (p RoundsPolicy) HuntProgress() ProgressContract {
	return ProgressContract{Method: "hunt", Expected: "designated animal killed or the hunt settled", Deadline: domain.Tick(p.HuntStallTicks)}
}

// AcquisitionProgress is the designation stall policy: a plant harvest
// the census reports designated and untaken for AcquisitionStallTicks
// since native first saw it (#291, #1044) is cancelled so the goal re-plans from another
// source (#291).
func (p RoundsPolicy) AcquisitionProgress() ProgressContract {
	return ProgressContract{Method: "harvest", Expected: "designation taken and the yield hauled", Deadline: domain.Tick(p.AcquisitionStallTicks)}
}

// ProgressEvidence is what one review observed about a goal's method, all
// of it native outcome or the absence of one.
type ProgressEvidence struct {
	// Advanced: a native outcome moved the expected observable since the
	// last review (an effect settled, a deficit shrank, work observed).
	Advanced bool
	// Open: a method has unsettled work (dispatched, pending or held).
	Open bool
	// Dispatched: the open work has been issued to native.
	Dispatched bool
	// Unresolved: a dispatched write's outcome is unknown.
	Unresolved bool
	// WorkerAvailable: an available pawn is capable of the open work;
	// unknown when the labor census or the work's profile is.
	WorkerAvailable domain.Fact[bool]
	// NativeIneligible: native holds the open work as ineligible.
	NativeIneligible bool
	// Prerequisite is a goal that must be served before this method can
	// advance; "" when none.
	Prerequisite ConcernID
	// Observed is the deficit fraction this review measured.
	Observed domain.Fact[float64]
}

// ReviewGoalProgress folds one review's evidence into the goal's record.
// A new record starts its clock at now. Progress resets the deadline;
// blocked evidence leaves the clock running, so a method blocked past its
// deadline rotates (ExpireGoalProgress). Expired cooldowns are dropped.
func ReviewConcernProgress(previous ConcernProgress, goal ConcernID, c ProgressContract, e ProgressEvidence, now domain.Tick) ConcernProgress {
	p := previous
	p.Concern = goal
	// A new goal, a new method or a tick rewind starts the clock; cooldowns
	// are keyed to situations, not methods, and outlive a method change.
	fresh := previous.Concern != goal || previous.Method != c.Method || previous.LastProgress > now
	if fresh {
		p.LastProgress = now
	}
	if previous.Concern != goal || previous.LastProgress > now {
		p.Cooldowns = nil
	}
	p.Method, p.Expected = c.Method, c.Expected
	if observed, known := e.Observed.Value(); known {
		if previous.Observed != nil && !fresh && observed < *previous.Observed {
			e.Advanced = true
		}
		v := observed
		p.Observed = &v
	} else {
		p.Observed = nil
	}
	if e.Advanced {
		p.LastProgress = now
	}
	p.Blocked = blockedReason(e)
	if p.Blocked == BlockedNoMethod && p.Planner != "" {
		p.Blocked = p.PlannerNote().Blocked()
	}
	p.Open = e.Open
	p.NextReview = 0
	if c.Deadline > 0 {
		p.NextReview = p.LastProgress + c.Deadline
	}
	p.Cooldowns = liveCooldowns(p.Cooldowns, now)
	return p
}

func blockedReason(e ProgressEvidence) BlockedReason {
	switch {
	case e.Prerequisite != "":
		return BlockedPrerequisite(e.Prerequisite)
	case e.Open && e.Unresolved:
		return BlockedReconciling
	case e.Open && e.NativeIneligible:
		return BlockedNativeIneligible
	case e.Open && e.Dispatched && !e.Advanced:
		if available, known := e.WorkerAvailable.Value(); known && !available {
			return BlockedNoWorker
		}
	case !e.Open && !e.Advanced:
		return BlockedNoMethod
	}
	return ""
}

func liveCooldowns(cooldowns []ProgressCooldown, now domain.Tick) []ProgressCooldown {
	var live []ProgressCooldown
	for _, cd := range cooldowns {
		if cd.Until > now {
			live = append(live, cd)
		}
	}
	return live
}

// Cooled reports whether the situation key is under a cooldown at now. A
// key encodes the failed situation (method, target, blocker, season), so
// a changed condition is a different key and lifts the cooldown by itself.
func (p ConcernProgress) Cooled(key string, now domain.Tick) bool {
	for _, cd := range p.Cooldowns {
		if cd.Key == key && cd.Until > now {
			return true
		}
	}
	return false
}

// CooldownKey joins the parts of a failed situation into one cooldown key.
func CooldownKey(parts ...string) string {
	return strings.Join(parts, "/")
}

// ExpireGoalProgress rotates a method whose deadline has passed without
// progress: the failed situation is keyed out for the contract's bounded
// cooldown and the first alternative not under one becomes the method,
// with a fresh deadline. It reports whether it rotated. A write whose
// outcome is unknown is never rotated past: the action identity is
// reconciled first (BlockedReconciling). With no free alternative the
// method stays, blocked on cooldown, until one lifts.
func ExpireConcernProgress(p ConcernProgress, c ProgressContract, now domain.Tick, situation string, alternatives []ProgressContract) (ConcernProgress, bool) {
	if p.NextReview == 0 || now < p.NextReview || p.Blocked == BlockedReconciling {
		return p, false
	}
	p.Cooldowns = append(liveCooldowns(p.Cooldowns, now), ProgressCooldown{Key: situation, Until: now + c.cooldown()})
	sort.SliceStable(p.Cooldowns, func(i, j int) bool { return p.Cooldowns[i].Key < p.Cooldowns[j].Key })
	for _, alt := range alternatives {
		if alt.Method == c.Method || p.Cooled(alt.Method, now) {
			continue
		}
		p.Method, p.Expected = alt.Method, alt.Expected
		p.LastProgress, p.Blocked = now, ""
		p.NextReview = 0
		if alt.Deadline > 0 {
			p.NextReview = now + alt.Deadline
		}
		return p, true
	}
	// Alternatives offered and every one cooled: blocked on cooldown. With
	// none offered the planner owns the rotation and the blocker stands.
	if len(alternatives) > 0 {
		p.Blocked = BlockedCooldown
	}
	p.LastProgress = now
	p.NextReview = 0
	if c.Deadline > 0 {
		p.NextReview = now + c.Deadline
	}
	return p, true
}

// AddProgressCooldown keys situation out until the given tick, bounded by
// ProgressCooldownMax from now; a key already cooled takes the later bound.
func AddProgressCooldown(p ConcernProgress, key string, until, now domain.Tick) ConcernProgress {
	until = min(until, now+ProgressCooldownMax)
	live := liveCooldowns(p.Cooldowns, now)
	p.Cooldowns = nil
	replaced := false
	for _, cd := range live {
		if cd.Key == key {
			cd.Until, replaced = max(cd.Until, until), true
		}
		p.Cooldowns = append(p.Cooldowns, cd)
	}
	if !replaced && until > now && len(p.Cooldowns) < 64 {
		p.Cooldowns = append(p.Cooldowns, ProgressCooldown{Key: key, Until: until})
	}
	sort.SliceStable(p.Cooldowns, func(i, j int) bool { return p.Cooldowns[i].Key < p.Cooldowns[j].Key })
	return p
}

// ValidateGoalProgress checks a persisted record against the review tick.
func ValidateConcernProgress(p ConcernProgress, tick domain.Tick) error {
	if !validResource(Resource(p.Concern)) || len(p.Method) > 64 || p.LastProgress < 0 || p.LastProgress > tick || p.NextReview < 0 || !p.Blocked.valid() || p.Planner != "" && !printableReason(p.Planner) || p.Planner == "" && p.PlannerWaiting {
		return errors.New("invalid standard progress")
	}
	if p.Observed != nil && (*p.Observed < 0 || *p.Observed > 1) {
		return errors.New("invalid standard progress")
	}
	for _, cd := range p.Cooldowns {
		if cd.Key == "" || cd.Until <= 0 || cd.Until > tick+ProgressCooldownMax {
			return errors.New("invalid standard progress cooldown")
		}
	}
	return nil
}

// FoodLadder is EnsureFoodSupply's rung order: acquire food, cook it, store
// it, grow it. FoodProgress names the rung the gates leave owed, what
// advancing it looks like and the goal a rung waits on: cooking needs
// EnsureCooking's bench, storing needs MaintainFoodStorage's zone. The
// cooking prerequisite is surfaced whenever the bench is known missing,
// whichever rung is current, because the ladder cannot pass "cook" without
// it and the builder placing it is the colony's scarce worker (#629). An
// unknown gate is no evidence of a missing bench and blocks nothing. The
// observable is the food runway's shortfall against FoodTargetDays, so a
// day of food gained reads as progress whichever rung is current.
func FoodProgress(f RoundsFacts, p RoundsPolicy, storageOpen bool) (ProgressContract, ConcernID, domain.Fact[float64]) {
	owed := func(v domain.Fact[bool]) bool { b, k := v.Value(); return k && !b }
	observed := domain.Unknown[float64]()
	if days, known := f.FoodDays.Value(); known && p.FoodTargetDays > 0 {
		observed = domain.Known(max(0, min(1, 1-days/p.FoodTargetDays)))
	}
	var prerequisite ConcernID
	if owed(f.Cooking) {
		prerequisite = EnsureCooking
	}
	deadline := domain.Tick(p.ConcernStallTicks)
	switch {
	case HumanFoodPending(f.FoodPlan) || !positive(footholdFood(f, p)):
		return ProgressContract{Method: "acquire", Expected: "food days rise toward the target", Deadline: deadline}, prerequisite, observed
	case owed(f.Cooking):
		return ProgressContract{Method: "cook", Expected: "meals cooked at a bench", Deadline: deadline}, prerequisite, observed
	case owed(f.FoodStorage) && storageOpen:
		return ProgressContract{Method: "store", Expected: "raw food stored under a roof", Deadline: deadline}, MaintainFoodStorage, observed
	default:
		return ProgressContract{Method: "grow", Expected: "growing zone planted to the field target", Deadline: deadline}, prerequisite, observed
	}
}

// GoalProgressContract is the default contract for a goal's method: the
// method id as the method, the goal's deficit as the observable,
// RoundsPolicy.ConcernStallTicks (scaled by colony stage) without progress as
// the deadline.
func ConcernProgressContract(method string, p RoundsPolicy) ProgressContract {
	if method == "" {
		method = "assess"
	}
	return ProgressContract{Method: method, Expected: "deficit shrinks or the method's work settles", Deadline: domain.Tick(p.ConcernStallTicks)}
}

// WithheldLabor is the labor a blocked prerequisite keeps out of optional
// development: while a goal's record is blocked on a prerequisite goal
// that a builder places, construction is withheld from the ranked queue
// so the builder is not diverted before the bench stands.
// A prerequisite with no open method (its planner proposed nothing)
// withholds nothing: holding the builder for work nobody placed would
// idle construction forever.
func WithheldLabor(progress []ConcernProgress) LaborProfile {
	var withheld LaborProfile
	seen := map[WorkType]bool{}
	open := map[ConcernID]bool{}
	for _, p := range progress {
		open[p.Concern] = open[p.Concern] || p.Open
	}
	for _, p := range progress {
		switch pre := p.Blocked.Prerequisite(); pre {
		case EnsureCooking, MaintainFoodStorage, MaintainHousing:
			if open[pre] && !seen[WorkConstruction] {
				seen[WorkConstruction] = true
				withheld = append(withheld, WorkConstruction)
			}
		}
	}
	return withheld
}

// PlannerOptOut is the planner reason for a goal whose method this runtime
// did not enable (the routine building planners' "disabled").
const PlannerOptOut = "disabled"

// HoldProgress relabels the records with no method whose goal is held on
// purpose, so "no_method" and planner refusals name only goals someone
// should look at: an unavailable method, a planner the runtime left
// disabled, a development row the ranking held (stage, labor, capacity,
// emergency) or labor withheld for every work type the goal uses.
func HoldProgress(progress []ConcernProgress, rows []DevelopmentRow, withheld LaborProfile, unavailable map[ConcernID]bool) []ConcernProgress {
	byGoal := map[ConcernID]DevelopmentRow{}
	for _, row := range rows {
		byGoal[row.Concern] = row
	}
	out := append([]ConcernProgress(nil), progress...)
	for i := range out {
		p := &out[i]
		if !p.Blocked.Unmethoded() {
			continue
		}
		if held := heldReason(*p, byGoal, withheld, unavailable); held != "" {
			p.Blocked = held
		}
	}
	return out
}

func heldReason(p ConcernProgress, rows map[ConcernID]DevelopmentRow, withheld LaborProfile, unavailable map[ConcernID]bool) BlockedReason {
	if unavailable[p.Concern] {
		return HeldUnavailable
	}
	if p.Planner == PlannerOptOut {
		return HeldOptIn
	}
	if row, ok := rows[p.Concern]; ok {
		switch row.Reason {
		case DevelopmentStage:
			return HeldStage
		case DevelopmentMethodUnavailable:
			return HeldUnavailable
		case DevelopmentCapacity, DevelopmentOvercommitted:
			return HeldCapacity
		case DevelopmentEmergency:
			return HeldEmergency
		case DevelopmentLabor, DevelopmentNoWorkers:
			w := row.Bottleneck
			if w == "" && len(row.Labor) > 0 {
				w = row.Labor[0]
			}
			if w != "" {
				return HeldLabor(w)
			}
		}
	}
	if labor := ConcernLabor(p.Concern); len(labor) > 0 && len(withheld) > 0 {
		all := true
		for _, w := range labor {
			all = all && slices.Contains(withheld, w)
		}
		if all {
			return HeldLabor(labor[0])
		}
	}
	return ""
}
