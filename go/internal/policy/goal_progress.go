package policy

import (
	"errors"
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
type GoalProgress struct {
	Goal GoalID
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
)

// BlockedPrerequisite blocks on another goal that must be served first.
func BlockedPrerequisite(goal GoalID) BlockedReason {
	return BlockedReason(blockedPrerequisite + string(goal))
}

// Prerequisite is the goal a prerequisite reason waits on, else "".
func (r BlockedReason) Prerequisite() GoalID {
	if strings.HasPrefix(string(r), blockedPrerequisite) {
		return GoalID(strings.TrimPrefix(string(r), blockedPrerequisite))
	}
	return ""
}

func (r BlockedReason) valid() bool {
	switch r {
	case "", BlockedNoWorker, BlockedNativeIneligible, BlockedReconciling, BlockedCooldown, BlockedNoMethod:
		return true
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

// HuntProgress is the hunt stall policy as a contract: a dispatched hunt
// whose route native keeps refusing (HuntingSafety.RouteSafe) never
// settles, so the planner tries other prey or a non-hunt source once
// HuntStallTicks pass without the kill.
func (p RoutinePolicy) HuntProgress() ProgressContract {
	return ProgressContract{Method: "hunt", Expected: "designated animal killed or the hunt settled", Deadline: domain.Tick(p.HuntStallTicks)}
}

// HaulProgress is the haul stall policy: a proposed haul native holds as
// ineligible (no storage accepts it, no hauler reaches it) for
// HaulStallTicks is cancelled so the attempt count advances to the fallback.
func (p RoutinePolicy) HaulProgress() ProgressContract {
	return ProgressContract{Method: "haul", Expected: "thing carried into storage", Deadline: domain.Tick(p.HaulStallTicks)}
}

// AcquisitionProgress is the designation stall policy: a plant harvest
// designated with its effect pending (no colonist took it) for
// AcquisitionStallTicks is cancelled so the goal re-plans from another
// source (#291).
func (p RoutinePolicy) AcquisitionProgress() ProgressContract {
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
	Prerequisite GoalID
	// Observed is the deficit fraction this review measured.
	Observed domain.Fact[float64]
}

// ReviewGoalProgress folds one review's evidence into the goal's record.
// A new record starts its clock at now. Progress resets the deadline;
// blocked evidence leaves the clock running, so a method blocked past its
// deadline rotates (ExpireGoalProgress). Expired cooldowns are dropped.
func ReviewGoalProgress(previous GoalProgress, goal GoalID, c ProgressContract, e ProgressEvidence, now domain.Tick) GoalProgress {
	p := previous
	p.Goal = goal
	// A new goal, a new method or a tick rewind starts the clock; cooldowns
	// are keyed to situations, not methods, and outlive a method change.
	fresh := previous.Goal != goal || previous.Method != c.Method || previous.LastProgress > now
	if fresh {
		p.LastProgress = now
	}
	if previous.Goal != goal || previous.LastProgress > now {
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
func (p GoalProgress) Cooled(key string, now domain.Tick) bool {
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
func ExpireGoalProgress(p GoalProgress, c ProgressContract, now domain.Tick, situation string, alternatives []ProgressContract) (GoalProgress, bool) {
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
func AddProgressCooldown(p GoalProgress, key string, until, now domain.Tick) GoalProgress {
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
func ValidateGoalProgress(p GoalProgress, tick domain.Tick) error {
	if !validResource(Resource(p.Goal)) || len(p.Method) > 64 || len(p.Expected) > 256 || p.LastProgress < 0 || p.LastProgress > tick || p.NextReview < 0 || !p.Blocked.valid() || len(p.Cooldowns) > 64 {
		return errors.New("invalid goal progress")
	}
	if p.Observed != nil && (*p.Observed < 0 || *p.Observed > 1) {
		return errors.New("invalid goal progress")
	}
	for _, cd := range p.Cooldowns {
		if cd.Key == "" || len(cd.Key) > 256 || cd.Until <= 0 || cd.Until > tick+ProgressCooldownMax {
			return errors.New("invalid goal progress cooldown")
		}
	}
	return nil
}

// FoodLadder is EnsureFoodSupply's rung order: acquire food, cook it, store
// it, grow it. FoodProgress names the rung the gates leave owed, what
// advancing it looks like and the goal a rung waits on: cooking needs
// EnsureCooking's bench, storing needs EnsureFoodStorage's zone. The
// cooking prerequisite is surfaced whenever the bench is known missing,
// whichever rung is current, because the ladder cannot pass "cook" without
// it and the builder placing it is the colony's scarce worker (#629). An
// unknown gate is no evidence of a missing bench and blocks nothing. The
// observable is the food runway's shortfall against FoodTargetDays, so a
// day of food gained reads as progress whichever rung is current.
func FoodProgress(g FootholdGates, f RoutineFacts, p RoutinePolicy) (ProgressContract, GoalID, domain.Fact[float64]) {
	owed := func(v domain.Fact[bool]) bool { b, k := v.Value(); return k && !b }
	observed := domain.Unknown[float64]()
	if days, known := fallback(f.PopulationFoodDays, f.FoodDays).Value(); known && p.FoodTargetDays > 0 {
		observed = domain.Known(max(0, min(1, 1-days/p.FoodTargetDays)))
	}
	var prerequisite GoalID
	if owed(g.Cooking) {
		prerequisite = EnsureCooking
	}
	deadline := domain.Tick(p.GoalStallTicks)
	switch {
	case HumanFoodPending(f.FoodPlan) || !positive(g.Food):
		return ProgressContract{Method: "acquire", Expected: "food days rise toward the target", Deadline: deadline}, prerequisite, observed
	case owed(g.Cooking):
		return ProgressContract{Method: "cook", Expected: "meals cooked at a bench", Deadline: deadline}, prerequisite, observed
	case owed(g.Storage):
		return ProgressContract{Method: "store", Expected: "raw food stored under a roof", Deadline: deadline}, EnsureFoodStorage, observed
	default:
		return ProgressContract{Method: "grow", Expected: "growing zone planted to the field target", Deadline: deadline}, prerequisite, observed
	}
}

// GoalProgressContract is the default contract for a goal's method: the
// method id as the method, the goal's deficit as the observable,
// RoutinePolicy.GoalStallTicks (scaled by colony stage) without progress as
// the deadline.
func GoalProgressContract(method string, p RoutinePolicy) ProgressContract {
	if method == "" {
		method = "assess"
	}
	return ProgressContract{Method: method, Expected: "deficit shrinks or the method's work settles", Deadline: domain.Tick(p.GoalStallTicks)}
}

// WithheldLabor is the labor a blocked prerequisite keeps out of optional
// development: while a goal's record is blocked on a prerequisite goal
// that a builder places, construction is withheld from the ranked queue
// so the builder is not diverted before the bench stands.
func WithheldLabor(progress []GoalProgress) LaborProfile {
	var withheld LaborProfile
	seen := map[WorkType]bool{}
	for _, p := range progress {
		switch p.Blocked.Prerequisite() {
		case EnsureCooking, EnsureFoodStorage, EnsureInitialShelter:
			if !seen[WorkConstruction] {
				seen[WorkConstruction] = true
				withheld = append(withheld, WorkConstruction)
			}
		}
	}
	return withheld
}
