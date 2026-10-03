package buildingruntime

import (
	"context"
	"fmt"
	"sort"
	"strings"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// PlanResultKind classifies what a planner's step produced (#622). Only a
// proposal competes for the step's shared claims; every other kind is the
// planner reporting why it has nothing to admit this step.
type PlanResultKind string

const (
	// PlanProposed carries a Proposal for the coordinator to arbitrate.
	PlanProposed PlanResultKind = "proposed"
	// PlanDemandSatisfied: the goal has no deficit, or the work it needs
	// is already in flight.
	PlanDemandSatisfied PlanResultKind = "satisfied"
	// PlanWaiting: the planner waits on the named dependency (a review, a
	// selection slot, a retry budget, a native read it could not complete).
	PlanWaiting PlanResultKind = "waiting"
	// PlanNativeWait: native work the planner cannot hurry is in progress;
	// NativeWorkTicks is the window it asks for.
	PlanNativeWait PlanResultKind = "native_wait"
	// PlanUnsupported: the planner is disabled or the composition lacks
	// what it needs.
	PlanUnsupported PlanResultKind = "unsupported"
)

// PlanResult is what a migrated planner returns instead of committing: the
// proposal it wants admitted, or the reason it has none. Reason is the
// planner family's own outcome vocabulary, kept so the step row and the
// family's result type read as before the migration.
type PlanResult struct {
	Kind            PlanResultKind
	Proposal        *Proposal
	Dependency      string
	NativeWorkTicks domain.Tick
	Verdict
}

// ResourceClaims is what a proposal needs held for it exclusively while it
// commits: the pawns it assigns, the entities it takes whole (a bench, an
// item stack, a bed; namespaced as stepArbiter's resources are) and the
// quantities it consumes (steel, wood, medicine, work capacity: named by
// policy.Resource, counted in units).
type ResourceClaims struct {
	Pawns      []domain.PawnID
	Entities   []string
	Quantities []policy.Amount
}

// Proposal is one planner's admissible plan for this step, with everything
// the coordinator needs to rank it and check it against its peers before
// anything is committed (#622). The commit itself stays with the planner:
// commit runs the family's existing admission path (authority, staleness,
// native validation, plan and attempt identities, generation checks) and
// the coordinator only decides whether and in which order it runs.
type Proposal struct {
	// ID is stable for the same goal, epoch and method across steps, so a
	// tie between two proposals resolves the same way every time.
	ID       string
	Planner  string
	Goal     domain.GoalID
	Priority int
	// Urgency is the goal's own priority class (domain.Goal.Priority):
	// lower is more urgent, the same sense as Priority.
	Urgency  int
	Snapshot domain.GenerationSnapshot
	Facts    []bridge.FactFamily
	Claims   ResourceClaims
	// ValidTick is the review tick the proposal was planned from.
	ValidTick domain.Tick
	Actions   []domain.Action
	commit    func(context.Context) (domain.PlanID, Verdict, error)
}

// ProposalOutcome is one proposal's fate on the step row: admitted with
// its plan, waiting on the claim a peer holds, or expired against the
// step it reached. A loser is never an error; it proposes again next step.
type ProposalOutcome struct {
	Proposal string
	Planner  string
	Goal     domain.GoalID
	Admitted bool
	Plan     domain.PlanID
	// Waiting names the claim that refused the proposal and who holds it.
	Waiting string
	// Stale names the dependency the coordinator found changed since the
	// proposal was evaluated (#623): the snapshot's scope, its native
	// generation or plan revision, or the tick it was planned from.
	Stale string
	Verdict
	// Demand is the quantity the step could not cover for the proposal:
	// what it claimed beyond the stock left after the step's earlier
	// claims and the admitted plans' commitments (#628). Nil unless Reason
	// is BuildingReasonDemand.
	Demand []policy.Amount
}

// proposalScope is the step the coordinator commits under: the read
// validity the step fixed (#624), which every proposal must still hold
// under. A proposal evaluated late, on an earlier step's facts (#623), is
// revalidated against it before its commit; the zero validity revalidates
// nothing.
type proposalScope = domain.ReadValidity

// proposalStale names the first dependency of p that no longer holds
// under scope, empty when p is still valid: the world and generation,
// then the plan revision. Its age never expires it: its actions are
// revalidated natively when they apply.
func proposalStale(scope proposalScope, p *Proposal) string {
	if !scope.Known() {
		return ""
	}
	if stale := scope.Stale(domain.ScopeOf(p.Snapshot)); stale != "" {
		return stale
	}
	if have := p.Snapshot; have.Plan != scope.Plan || have.Revision != scope.Revision {
		return fmt.Sprintf("plan %s@%d, step is %s@%d", have.Plan, have.Revision, scope.Plan, scope.Revision)
	}
	return ""
}

// lateProposals carries the proposals that reached their step's arbiter
// after its cutoff (#623) to the next step's coordinator, which revalidates
// them against its own scope: a still-valid one commits there, an expired
// one is refused with the stale dependency named.
type lateProposals struct {
	mu       sync.Mutex
	arrivals []proposalArrival
}

func (l *lateProposals) add(arrival proposalArrival) {
	l.mu.Lock()
	defer l.mu.Unlock()
	l.arrivals = append(l.arrivals, arrival)
}

func (l *lateProposals) drain() []proposalArrival {
	if l == nil {
		return nil
	}
	l.mu.Lock()
	defer l.mu.Unlock()
	arrivals := l.arrivals
	l.arrivals = nil
	return arrivals
}

// stepBudget is what the coordinator checks a proposal's quantity claims
// against (#628): Stock is the count the routine review observed for each
// bounded resource (a resource absent from it is unbounded here and checked
// by the admission path beneath the commit).
type stepBudget struct {
	Stock map[policy.Resource]int64
}

// proposalArrival is one PlanResult as the wave delivered it: the result,
// and the callback that writes it to the step's result.
type proposalArrival struct {
	planner string
	result  PlanResult
	settle  func(ProposalOutcome)
}

// propose records a migrated planner's result for the coordinator. The
// planner has already finished every read; nothing is claimed here.
func (a *stepArbiter) propose(planner string, result PlanResult, settle func(ProposalOutcome)) {
	a.mu.Lock()
	defer a.mu.Unlock()
	arrival := proposalArrival{planner: planner, result: result, settle: settle}
	if a.closed {
		// Past the cutoff the step has moved on and its result is not
		// read again: a proposal is carried to the next coordinator, and
		// a non-proposal result has nothing to carry.
		if a.late != nil && result.Kind == PlanProposed && result.Proposal != nil {
			arrival.settle = nil
			a.late.add(arrival)
		}
		return
	}
	a.arrivals = append(a.arrivals, arrival)
}

// claimsQuantities reports whether any proposal the wave delivered claims a
// quantity, so the step reads a budget only when one is needed.
func (a *stepArbiter) claimsQuantities() bool {
	a.mu.Lock()
	defer a.mu.Unlock()
	for _, arrival := range a.arrivals {
		if arrival.result.Kind == PlanProposed && arrival.result.Proposal != nil && len(arrival.result.Proposal.Claims.Quantities) != 0 {
			return true
		}
	}
	return false
}

// proposalRank orders proposals for the coordinator: planner priority,
// then goal urgency, then the stable proposal ID.
func proposalRank(a, b *Proposal) bool {
	if a.Priority != b.Priority {
		return a.Priority < b.Priority
	}
	if a.Urgency != b.Urgency {
		return a.Urgency < b.Urgency
	}
	return a.ID < b.ID
}

// claimIndex is the coordinator's view of what this step holds: the
// arbiter's own pawn and entity claims (so un-migrated planners' first-
// arrival claims are honoured) and a quantity ledger against the budget's
// stock (#628). A resource absent
// from the stock is unbounded: the admission path beneath the commit still
// checks it.
type claimIndex struct {
	arbiter *stepArbiter
	budget  stepBudget
	used    map[policy.Resource]int64
	holders map[string]string
}

func newClaimIndex(arbiter *stepArbiter, budget stepBudget) *claimIndex {
	return &claimIndex{arbiter: arbiter, budget: budget, used: map[policy.Resource]int64{}, holders: map[string]string{}}
}

// refusal names the first pawn or entity claim of p already held, and by
// whom; empty when every one is free. Quantities are checked by shortfall.
func (c *claimIndex) refusal(p *Proposal) string {
	for _, pawn := range p.Claims.Pawns {
		if c.arbiter.pawns[pawn] {
			return "pawn:" + string(pawn) + c.holder("pawn:"+string(pawn))
		}
	}
	for _, entity := range p.Claims.Entities {
		if c.arbiter.resources[entity] {
			return entity + c.holder(entity)
		}
	}
	return ""
}

// shortfall is what p's quantity claims need beyond the stock left after
// this step's earlier claims, with the first
// refusing claim named and who holds the rest; nil when the stock covers
// them.
func (c *claimIndex) shortfall(p *Proposal) (string, []policy.Amount) {
	var refused string
	var demand []policy.Amount
	for _, amount := range p.Claims.Quantities {
		limit, bounded := c.budget.Stock[amount.Resource]
		if !bounded {
			continue
		}
		free := limit - c.used[amount.Resource]
		if amount.Count <= free {
			continue
		}
		demand = append(demand, policy.Amount{Resource: amount.Resource, Count: amount.Count - free})
		if refused == "" {
			refused = fmt.Sprintf("%s:%d of %d", amount.Resource, amount.Count, free) + c.holder(string(amount.Resource))
		}
	}
	return refused, demand
}

func (c *claimIndex) holder(key string) string {
	if who := c.holders[key]; who != "" {
		return " held by " + who
	}
	return ""
}

// take records p's claims as held by it.
func (c *claimIndex) take(p *Proposal) {
	for _, pawn := range p.Claims.Pawns {
		c.arbiter.pawns[pawn] = true
		c.holders["pawn:"+string(pawn)] = p.ID
	}
	for _, entity := range p.Claims.Entities {
		c.arbiter.resources[entity] = true
		c.holders[entity] = p.ID
	}
	for _, amount := range p.Claims.Quantities {
		c.used[amount.Resource] += amount.Count
		c.holders[string(amount.Resource)] = p.ID
	}
}

// coordinate arbitrates the wave's proposals once the admission cycle has
// joined it: proposals sorted by (priority, urgency, ID) are revalidated
// against scope and take their claims against the step's index in that
// order, and a proposal whose claims are free commits through its
// planner's own admission path. A proposal whose dependencies changed
// since it was evaluated (a late proposal carried from an earlier step,
// #623) is reported expired with the stale dependency named and never
// commits. One that loses a pawn or entity is reported waiting on it,
// never failed. A quantity the stock left after earlier claims cannot
// cover is refused as demand with the shortfall on its outcome (#628).
// Non-proposal results settle as they were reported. The outcomes are
// returned in rank order; commit errors are returned beside them for the
// step to isolate.
func (a *stepArbiter) coordinate(ctx context.Context, budget stepBudget, scope proposalScope) ([]ProposalOutcome, []error) {
	a.mu.Lock()
	arrivals := append(a.late.drain(), a.arrivals...)
	a.arrivals = nil
	a.mu.Unlock()
	index := newClaimIndex(a, budget)
	var proposals []proposalArrival
	for _, arrival := range arrivals {
		if arrival.result.Kind == PlanProposed && arrival.result.Proposal != nil {
			proposals = append(proposals, arrival)
			continue
		}
		if arrival.settle != nil {
			arrival.settle(ProposalOutcome{Planner: arrival.planner, Verdict: arrival.result.Verdict})
		}
	}
	sort.SliceStable(proposals, func(i, j int) bool { return proposalRank(proposals[i].result.Proposal, proposals[j].result.Proposal) })
	var outcomes []ProposalOutcome
	var failures []error
	// The arbiter's maps are read and written by the index without its
	// mutex: the wave has returned and nothing else claims now.
	for _, arrival := range proposals {
		p := arrival.result.Proposal
		outcome := ProposalOutcome{Proposal: p.ID, Planner: arrival.planner, Goal: p.Goal}
		if stale := proposalStale(scope, p); stale != "" {
			outcome.Stale, outcome.Verdict = stale, BuildingReasonExpired
			if arrival.settle != nil {
				arrival.settle(outcome)
			}
			outcomes = append(outcomes, outcome)
			continue
		}
		refused := index.refusal(p)
		var demand []policy.Amount
		if refused == "" {
			refused, demand = index.shortfall(p)
		}
		if refused != "" {
			outcome.Waiting, outcome.Verdict = refused, BuildingReasonWaiting
			if demand != nil {
				outcome.Verdict, outcome.Demand = BuildingReasonDemand, demand
			}
		} else {
			index.take(p)
			plan, reason, err := p.commit(ctx)
			if err != nil {
				failures = append(failures, fmt.Errorf("%s: %w", arrival.planner, err))
				outcome.Verdict = BuildingReasonRefused
			} else {
				outcome.Admitted, outcome.Plan, outcome.Verdict = reason == BuildingReasonAdmitted, plan, reason
			}
		}
		if arrival.settle != nil {
			arrival.settle(outcome)
		}
		outcomes = append(outcomes, outcome)
	}
	return outcomes, failures
}

// String lists the claims for a log line.
func (c ResourceClaims) String() string {
	var parts []string
	for _, pawn := range c.Pawns {
		parts = append(parts, "pawn:"+string(pawn))
	}
	parts = append(parts, c.Entities...)
	for _, amount := range c.Quantities {
		parts = append(parts, fmt.Sprintf("%s:%d", amount.Resource, amount.Count))
	}
	return strings.Join(parts, ",")
}
