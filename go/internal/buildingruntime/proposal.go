package buildingruntime

import (
	"context"
	"fmt"
	"sort"
	"strings"

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
	Reason          RoutineBuildingReason
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
	commit    func(context.Context) (domain.PlanID, RoutineBuildingReason, error)
}

// ProposalOutcome is one proposal's fate on the step row: admitted with
// its plan, or waiting on the claim a peer holds. A loser is never an
// error; it proposes again next step.
type ProposalOutcome struct {
	Proposal string
	Planner  string
	Goal     domain.GoalID
	Admitted bool
	Plan     domain.PlanID
	// Waiting names the claim that refused the proposal and who holds it.
	Waiting string
	Reason  RoutineBuildingReason
}

// BuildingMethodWaiting is a migrated planner's result when the
// coordinator gave a claim it needs to a higher-ranked proposal; the step
// row's proposal outcome names the claim.
const BuildingMethodWaiting RoutineBuildingReason = "waiting_on_claim"

// proposalArrival is one PlanResult as the wave delivered it: the result,
// the callback that writes it to the step's result, and (for a proposal)
// the verdict the first-arrival path would have given, so the coordinator
// can log where the two paths disagree during the migration.
type proposalArrival struct {
	planner string
	result  PlanResult
	settle  func(ProposalOutcome)
	legacy  bool
}

// propose records a migrated planner's result for the coordinator. The
// planner has already finished every read; nothing is claimed here. For a
// proposal the shadow arbiter records the first-arrival verdict beside it.
func (a *stepArbiter) propose(planner string, result PlanResult, settle func(ProposalOutcome)) {
	a.mu.Lock()
	defer a.mu.Unlock()
	arrival := proposalArrival{planner: planner, result: result, settle: settle}
	if result.Kind == PlanProposed && result.Proposal != nil {
		if a.shadow == nil {
			a.shadow = newStepArbiter()
		}
		arrival.legacy = a.shadow.tryClaim(result.Proposal.Claims.Pawns, result.Proposal.Claims.Entities...)
	}
	a.arrivals = append(a.arrivals, arrival)
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
// arrival claims are honoured) plus a quantity ledger against budget. A
// resource absent from budget is unbounded: the admission path beneath the
// commit still checks stock.
type claimIndex struct {
	arbiter *stepArbiter
	budget  map[policy.Resource]int64
	used    map[policy.Resource]int64
	holders map[string]string
}

// refusal names the first claim of p already held, and by whom; empty
// when every claim is free.
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
	for _, amount := range p.Claims.Quantities {
		limit, bounded := c.budget[amount.Resource]
		if bounded && c.used[amount.Resource]+amount.Count > limit {
			return fmt.Sprintf("%s:%d of %d", amount.Resource, amount.Count, limit-c.used[amount.Resource]) + c.holder(string(amount.Resource))
		}
	}
	return ""
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

// coordinate arbitrates the wave's proposals after every planner has
// returned: proposals sorted by (priority, urgency, ID) take their claims
// against the step's index in that order, and a proposal whose claims are
// free commits through its planner's own admission path. A proposal that
// loses a claim is reported waiting on it, never failed. Non-proposal
// results settle as they were reported. The outcomes are returned in rank
// order; commit errors are returned beside them for the step to isolate.
//
// During the migration the coordinator also logs every proposal whose fate
// differs from the first-arrival verdict the shadow arbiter recorded.
func (a *stepArbiter) coordinate(ctx context.Context, budget map[policy.Resource]int64) ([]ProposalOutcome, []error) {
	a.mu.Lock()
	arrivals := append([]proposalArrival(nil), a.arrivals...)
	a.arrivals = nil
	a.mu.Unlock()
	index := &claimIndex{arbiter: a, budget: budget, used: map[policy.Resource]int64{}, holders: map[string]string{}}
	var proposals []proposalArrival
	for _, arrival := range arrivals {
		if arrival.result.Kind == PlanProposed && arrival.result.Proposal != nil {
			proposals = append(proposals, arrival)
			continue
		}
		if arrival.settle != nil {
			arrival.settle(ProposalOutcome{Planner: arrival.planner, Reason: arrival.result.Reason})
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
		if refused := index.refusal(p); refused != "" {
			outcome.Waiting, outcome.Reason = refused, BuildingMethodWaiting
		} else {
			index.take(p)
			plan, reason, err := p.commit(ctx)
			if err != nil {
				failures = append(failures, fmt.Errorf("%s: %w", arrival.planner, err))
				outcome.Reason = BuildingMethodRefused
			} else {
				outcome.Admitted, outcome.Plan, outcome.Reason = reason == BuildingMethodAdmitted, plan, reason
			}
		}
		if outcome.Admitted != arrival.legacy {
			clockSchedulerLog("proposal %s: coordinator %s, first-arrival would have %s (claims %s)", p.ID, proposalVerdict(outcome.Admitted), proposalVerdict(arrival.legacy), p.Claims)
		}
		if arrival.settle != nil {
			arrival.settle(outcome)
		}
		outcomes = append(outcomes, outcome)
	}
	return outcomes, failures
}

func proposalVerdict(admitted bool) string {
	if admitted {
		return "admitted"
	}
	return "refused"
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
