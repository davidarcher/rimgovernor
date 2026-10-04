package buildingruntime

import (
	"context"
	"fmt"
	"slices"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// MaintainPsylink (#1609, epic #1598): each review finds the willing
// colonists with no psylink (policy.PsylinkCandidates) and the untitled ones
// below the level cap (policy.PsylinkLevelUps, #1940) and, while one waits,
// reads the held neuroformer items. The planner orders one colonist to use
// one neuroformer on itself (UseItem with the pawn as its own target),
// colonists with no psylink first. The neuroformer itself is acquired by
// MaintainResource: the review adds policy.NeuroformerNeeds, for the
// no-psylink colonists only, to the resource needs, which the resource ladder
// meets with a production bill or a trade. At most maxPsylinkAttempts uses
// are ordered per colonist per goal epoch, so a colonist native refuses
// does not block the others or loop.
const (
	maxPsylinkAttempts = 2
	psylinkPrefix      = "psylink-"
)

// RoutinePsylinkSource is the native read behind the held neuroformer items.
type RoutinePsylinkSource interface {
	ReadNeuroformerItems(context.Context, *c.Identity, string) ([]string, bridge.Result, error)
}

// psylinkMemory is the latest review's candidates, level-up colonists and held
// neuroformer items for one world, in memory only.
type psylinkMemory struct {
	native RoutinePsylinkSource
	mu     sync.Mutex
	world  string
	tick   domain.Tick
	who    domain.Fact[[]policy.PawnID]
	up     []policy.PawnID
	items  []string
}

// review finds the candidates and level-up colonists and, while one waits and
// the royalty read counts a held neuroformer, the held item ids; it returns the
// candidates (for the resource needs) and whether a use is owed. A failed item read leaves the
// use unknown rather than failing the review.
func (m *psylinkMemory) review(ctx context.Context, identity *c.Identity, current domain.GenerationSnapshot, projection observation.ColonyProjection) (domain.Fact[[]policy.PawnID], domain.Fact[bool]) {
	candidates := policy.PsylinkCandidates(projection.Royalty, projection.WorkPawns)
	levelUps := policy.PsylinkLevelUps(projection.Royalty, projection.WorkPawns)
	items := domain.Unknown[[]string]()
	first, ok1 := candidates.Value()
	up, ok2 := levelUps.Value()
	if ok1 && ok2 && len(first)+len(up) > 0 {
		royalty, _ := projection.Royalty.Value()
		if held, known := royalty.Neuroformers[policy.PsylinkNeuroformer].Held.Value(); known && held == 0 {
			items = domain.Known([]string{})
		} else if known {
			read, _, err := m.native.ReadNeuroformerItems(ctx, identity, policy.PsylinkNeuroformer)
			if err != nil {
				clockSchedulerLog("psylink: item read deferred: %v", err)
			} else {
				items = domain.Known(read)
			}
		}
	}
	held, _ := items.Value()
	m.mu.Lock()
	defer m.mu.Unlock()
	m.world, m.tick, m.who, m.up, m.items = stockpileWorld(current), projection.Identity.Tick, candidates, slices.Clone(up), slices.Clone(held)
	return candidates, policy.PsylinkOwed(candidates, levelUps, items)
}

// take returns the candidates, level-up colonists and items the review at tick
// left for world.
func (m *psylinkMemory) take(world string, tick domain.Tick) ([]policy.PawnID, []policy.PawnID, []string, bool) {
	m.mu.Lock()
	defer m.mu.Unlock()
	who, known := m.who.Value()
	if m.world != world || m.tick != tick || !known {
		return nil, nil, nil, false
	}
	return slices.Clone(who), slices.Clone(m.up), slices.Clone(m.items), true
}

// RoutinePsylinkPlanner is MaintainPsylink's planner.
type RoutinePsylinkPlanner struct {
	reviewer *Rounder
	memory   *psylinkMemory
}
type RoutinePsylinkResult struct {
	Verdict
	Plan domain.PlanID
}

// NewRoutinePsylinkPlanner composes the planner and has reviewer run the
// psylink review over native.
func NewRoutinePsylinkPlanner(reviewer *Rounder, native RoutinePsylinkSource) (*RoutinePsylinkPlanner, error) {
	if reviewer == nil || native == nil || !reviewer.methodEnabled(policy.MaintainPsylink) {
		return nil, fmt.Errorf("%w: NewRoutinePsylinkPlanner: reviewer == nil || native == nil || !reviewer.methodEnabled(policy.MaintainPsylink)", ErrControl)
	}
	reviewer.psylink = &psylinkMemory{native: native, who: domain.Unknown[[]policy.PawnID]()}
	return &RoutinePsylinkPlanner{reviewer, reviewer.psylink}, nil
}

func (r *RoutinePsylinkPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutinePsylinkResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutinePsylinkResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutinePsylinkResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoutinePsylinkResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutinePsylinkResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainPsylink)
	if err != nil {
		return RoutinePsylinkResult{}, err
	}
	if !workable {
		return RoutinePsylinkResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutinePsylinkResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoutinePsylinkResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	candidates, levelUps, items, ok := r.memory.take(stockpileWorld(state.Snapshot), review.Tick)
	if !ok {
		return RoutinePsylinkResult{Verdict: BuildingReasonNoReview}, nil
	}
	history, err := p.journal.LoadGoalMethods(call, goal.Goal.ID, goal.Goal.Epoch)
	if err != nil {
		return RoutinePsylinkResult{}, err
	}
	willingOf := func(who []policy.PawnID) []policy.PawnID {
		var out []policy.PawnID
		for _, pawn := range who {
			if medicalAttemptCount(history, goal.Goal.Epoch, fmt.Sprintf("%s%s-", psylinkPrefix, pawn)) < maxPsylinkAttempts {
				out = append(out, pawn)
			}
		}
		return out
	}
	choice, owed := policy.NextPsylinkUse(willingOf(candidates), willingOf(levelUps), items)
	if !owed {
		if len(candidates)+len(levelUps) > 0 && len(items) > 0 {
			return RoutinePsylinkResult{Verdict: refuse(RefusalRetriesSpent, "maxPsylinkAttempts", "")}, nil
		}
		return RoutinePsylinkResult{Verdict: waitFor(WaitMethodUsed, "psylink_item")}, nil
	}
	if !arbiter.tryClaim([]domain.PawnID{domain.PawnID(choice.Pawn)}) {
		return RoutinePsylinkResult{Verdict: waitFor(WaitMethodUsed, "psylink_pawn_claim")}, nil
	}
	prefix := fmt.Sprintf("%s%s-", psylinkPrefix, choice.Pawn)
	attempt := medicalAttemptCount(history, goal.Goal.Epoch, prefix)
	use, err := domain.NewUseItem(domain.PawnID(choice.Pawn), choice.Item, domain.PawnID(choice.Pawn))
	if err != nil {
		return RoutinePsylinkResult{}, err
	}
	id := domain.MintPlanID()
	action, err := domain.NewUseItemAction(domain.ActionID(fmt.Sprintf("%s-0", id)), use)
	if err != nil {
		return RoutinePsylinkResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutinePsylinkResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutinePsylinkResult{}, err
	}
	if p.session.State() != state {
		return RoutinePsylinkResult{}, fmt.Errorf("%w: step: p.session.State() != state", ErrControl)
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	if _, err = p.journal.CommitGoalMethodReason(call, goal.Goal.ID, goal.Revision, method, fmt.Sprintf("psylink: %s uses neuroformer %s", choice.Pawn, choice.Item), plan); err != nil {
		return RoutinePsylinkResult{}, err
	}
	return RoutinePsylinkResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}
