package buildingruntime

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// testProposal is a proposal whose commit records itself as admitted.
func testProposal(id string, priority, urgency int, claims ResourceClaims, committed *[]string) *Proposal {
	return &Proposal{ID: id, Planner: id, Goal: domain.GoalID(id), Priority: priority, Urgency: urgency, Claims: claims,
		commit: func(context.Context) (domain.PlanID, RoutineBuildingReason, error) {
			*committed = append(*committed, id)
			return domain.PlanID("plan-" + id), BuildingMethodAdmitted, nil
		}}
}

// Two proposals claiming the same pawn and the same quantity of one
// resource: the higher (priority, urgency, id) proposal is admitted and
// the loser is reported waiting on the claim, never as an error --
// whichever arrived first (#622).
func TestCoordinateAdmitsHigherRankAndReportsLoserWaiting(t *testing.T) {
	t.Parallel()
	for _, order := range [][2]string{{"high", "low"}, {"low", "high"}} {
		var committed []string
		claims := ResourceClaims{Pawns: []domain.PawnID{"builder"}, Quantities: []policy.Amount{{Resource: "Steel", Count: 25}}}
		proposals := map[string]*Proposal{
			"high": testProposal("high", plannerFoothold, 3, claims, &committed),
			"low":  testProposal("low", plannerMaintenance, 3, claims, &committed),
		}
		a := newStepArbiter()
		settled := map[string]ProposalOutcome{}
		for _, name := range order {
			a.propose(name, PlanResult{Kind: PlanProposed, Proposal: proposals[name], Reason: BuildingMethodAdmitted}, func(o ProposalOutcome) { settled[name] = o })
		}
		outcomes, failures := a.coordinate(context.Background(), stepBudget{Stock: map[policy.Resource]int64{"Steel": 25}}, proposalScope{})
		if len(failures) != 0 || len(outcomes) != 2 || len(committed) != 1 || committed[0] != "high" {
			t.Fatalf("%v: outcomes %+v failures %v committed %v", order, outcomes, failures, committed)
		}
		if !outcomes[0].Admitted || outcomes[0].Proposal != "high" || outcomes[0].Plan != "plan-high" || settled["high"].Plan != outcomes[0].Plan {
			t.Fatalf("%v: high must be admitted first: %+v", order, outcomes[0])
		}
		if outcomes[1].Admitted || outcomes[1].Reason != BuildingMethodWaiting || outcomes[1].Waiting != "pawn:builder held by high" || settled["low"].Waiting != outcomes[1].Waiting {
			t.Fatalf("%v: low must wait on the pawn: %+v", order, outcomes[1])
		}
	}
}

// With disjoint pawns the quantity ledger alone decides: a budget of one
// stack admits the higher proposal and the loser waits on the resource.
func TestCoordinateQuantityBudgetRefusesSecondClaim(t *testing.T) {
	t.Parallel()
	var committed []string
	a := newStepArbiter()
	steel := []policy.Amount{{Resource: "Steel", Count: 25}}
	a.propose("b", PlanResult{Kind: PlanProposed, Proposal: testProposal("b", plannerFoothold, 3, ResourceClaims{Pawns: []domain.PawnID{"p2"}, Quantities: steel}, &committed)}, nil)
	a.propose("a", PlanResult{Kind: PlanProposed, Proposal: testProposal("a", plannerFoothold, 3, ResourceClaims{Pawns: []domain.PawnID{"p1"}, Quantities: steel}, &committed)}, nil)
	outcomes, _ := a.coordinate(context.Background(), stepBudget{Stock: map[policy.Resource]int64{"Steel": 40}}, proposalScope{})
	if len(outcomes) != 2 || outcomes[0].Proposal != "a" || !outcomes[0].Admitted || outcomes[1].Admitted || outcomes[1].Waiting != "Steel:25 of 15 held by a" {
		t.Fatalf("%+v", outcomes)
	}
	// Without a budget for the resource the quantity is unbounded.
	var again []string
	b := newStepArbiter()
	b.propose("a", PlanResult{Kind: PlanProposed, Proposal: testProposal("a", plannerFoothold, 3, ResourceClaims{Pawns: []domain.PawnID{"p1"}, Quantities: steel}, &again)}, nil)
	b.propose("b", PlanResult{Kind: PlanProposed, Proposal: testProposal("b", plannerFoothold, 3, ResourceClaims{Pawns: []domain.PawnID{"p2"}, Quantities: steel}, &again)}, nil)
	if outcomes, _ = b.coordinate(context.Background(), stepBudget{}, proposalScope{}); len(again) != 2 || !outcomes[1].Admitted {
		t.Fatalf("%+v %v", outcomes, again)
	}
}

// Rank is (priority, urgency, id): urgency breaks a priority tie and the
// stable ID breaks an urgency tie, so the same proposals rank the same
// way every step.
func TestCoordinateRankOrder(t *testing.T) {
	t.Parallel()
	var committed []string
	a := newStepArbiter()
	claims := ResourceClaims{Entities: []string{"bench:1"}}
	a.propose("z", PlanResult{Kind: PlanProposed, Proposal: testProposal("z", plannerFoothold, 2, claims, &committed)}, nil)
	a.propose("y", PlanResult{Kind: PlanProposed, Proposal: testProposal("y", plannerFoothold, 2, claims, &committed)}, nil)
	a.propose("x", PlanResult{Kind: PlanProposed, Proposal: testProposal("x", plannerFoothold, 3, claims, &committed)}, nil)
	a.propose("w", PlanResult{Kind: PlanProposed, Proposal: testProposal("w", plannerMaintenance, 0, claims, &committed)}, nil)
	outcomes, _ := a.coordinate(context.Background(), stepBudget{}, proposalScope{})
	got := make([]string, len(outcomes))
	for i, o := range outcomes {
		got[i] = o.Proposal
	}
	if want := []string{"y", "z", "x", "w"}; len(got) != 4 || got[0] != want[0] || got[1] != want[1] || got[2] != want[2] || got[3] != want[3] || len(committed) != 1 || committed[0] != "y" {
		t.Fatalf("rank %v committed %v", got, committed)
	}
}

// A claim an un-migrated planner took first-arrival on the same arbiter
// still refuses the proposal, and a non-proposal result settles as the
// planner reported it.
func TestCoordinateHonoursArbiterClaimsAndSettlesNonProposals(t *testing.T) {
	t.Parallel()
	var committed []string
	a := newStepArbiter()
	if !a.tryClaim([]domain.PawnID{"doctor"}) {
		t.Fatal("first claim")
	}
	a.propose("tend", PlanResult{Kind: PlanProposed, Proposal: testProposal("tend", plannerCritical, 1, ResourceClaims{Pawns: []domain.PawnID{"doctor"}}, &committed)}, nil)
	var waiting ProposalOutcome
	a.propose("idle", PlanResult{Kind: PlanWaiting, Dependency: "review", Reason: BuildingMethodNoReview}, func(o ProposalOutcome) { waiting = o })
	outcomes, failures := a.coordinate(context.Background(), stepBudget{}, proposalScope{})
	if len(failures) != 0 || len(committed) != 0 || len(outcomes) != 1 || outcomes[0].Admitted || outcomes[0].Waiting != "pawn:doctor" {
		t.Fatalf("%+v %v %v", outcomes, failures, committed)
	}
	if waiting.Planner != "idle" || waiting.Reason != BuildingMethodNoReview || waiting.Admitted {
		t.Fatalf("%+v", waiting)
	}
}

// A commit that fails is isolated like any planner failure: reported
// beside the outcomes, with the proposal refused, and the peers still
// coordinated.
func TestCoordinateIsolatesCommitFailure(t *testing.T) {
	t.Parallel()
	a := newStepArbiter()
	broken := errors.New("journal closed")
	a.propose("first", PlanResult{Kind: PlanProposed, Proposal: &Proposal{ID: "first", Priority: plannerFoothold, Claims: ResourceClaims{Pawns: []domain.PawnID{"p"}},
		commit: func(context.Context) (domain.PlanID, RoutineBuildingReason, error) { return "", "", broken }}}, nil)
	var committed []string
	a.propose("second", PlanResult{Kind: PlanProposed, Proposal: testProposal("second", plannerFoothold, 3, ResourceClaims{Pawns: []domain.PawnID{"q"}}, &committed)}, nil)
	outcomes, failures := a.coordinate(context.Background(), stepBudget{}, proposalScope{})
	if len(failures) != 1 || !errors.Is(failures[0], broken) || len(outcomes) != 2 || outcomes[0].Admitted || outcomes[0].Reason != BuildingMethodRefused || !outcomes[1].Admitted {
		t.Fatalf("%+v %v", outcomes, failures)
	}
}
