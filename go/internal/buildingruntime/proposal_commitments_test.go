package buildingruntime

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func wood(count int64) ResourceClaims {
	return ResourceClaims{Quantities: []policy.Amount{{Resource: "WoodLog", Count: count}}}
}

func held(plan string, urgency int, count int64, preemptible bool) store.PlanCommitment {
	return store.PlanCommitment{Plan: domain.PlanID(plan), Goal: domain.GoalID("goal-" + plan), Revision: 3, Urgency: urgency, Action: domain.ActionID(plan + "-a"),
		Amount: policy.Amount{Resource: "WoodLog", Count: count}, Since: 10, Expires: 110, Release: store.CommitmentHeld, Preemptible: preemptible}
}

// Two proposals over one stock of wood: the first is committed and the
// second is refused as demand, with the shortfall on its outcome (#628).
func TestCoordinateCommitsFirstAndRefusesSecondAsDemand(t *testing.T) {
	t.Parallel()
	var committed []string
	a := newStepArbiter()
	a.propose("bench", PlanResult{Kind: PlanProposed, Proposal: testProposal("bench", plannerFoothold, 3, wood(60), &committed)}, nil)
	a.propose("bed", PlanResult{Kind: PlanProposed, Proposal: testProposal("bed", plannerFoothold, 2, wood(60), &committed)}, nil)
	outcomes, failures := a.coordinate(context.Background(), stepBudget{Stock: map[policy.Resource]int64{"WoodLog": 100}}, proposalScope{})
	if len(failures) != 0 || len(outcomes) != 2 || len(committed) != 1 || committed[0] != "bed" {
		t.Fatalf("%+v %v %v", outcomes, failures, committed)
	}
	if !outcomes[0].Admitted || outcomes[0].Proposal != "bed" {
		t.Fatalf("%+v", outcomes[0])
	}
	lost := outcomes[1]
	if lost.Admitted || lost.Reason != BuildingMethodDemand || lost.Waiting != "WoodLog:60 of 40 held by bed" || len(lost.Demand) != 1 || lost.Demand[0] != (policy.Amount{Resource: "WoodLog", Count: 20}) {
		t.Fatalf("%+v", lost)
	}
}

// A quantity an admitted plan holds counts against the stock like a peer's
// claim: a proposal no more urgent than the holder is refused as demand
// naming the plan, and once the hold has expired out of the view the same
// proposal is admitted.
func TestCoordinateCommitmentRefusesAsDemandUntilExpiry(t *testing.T) {
	t.Parallel()
	shelter := held("shelter", 2, 70, true)
	stock := map[policy.Resource]int64{"WoodLog": 100}
	var committed []string
	a := newStepArbiter()
	a.propose("bench", PlanResult{Kind: PlanProposed, Proposal: testProposal("bench", plannerFoothold, 2, wood(40), &committed)}, nil)
	outcomes, failures := a.coordinate(context.Background(), stepBudget{Stock: stock, Commitments: store.PlanCommitments{Tick: 50, Committed: []store.PlanCommitment{shelter}}, Preempt: func(context.Context, store.PlanCommitment) error {
		t.Fatal("an equally urgent commitment must not be preempted")
		return nil
	}}, proposalScope{})
	if len(failures) != 0 || len(committed) != 0 || len(outcomes) != 1 || outcomes[0].Admitted || outcomes[0].Reason != BuildingMethodDemand || outcomes[0].Waiting != "WoodLog:40 of 30 committed to shelter" || len(outcomes[0].Demand) != 1 || outcomes[0].Demand[0].Count != 10 {
		t.Fatalf("%+v %v %v", outcomes, failures, committed)
	}
	// Past its horizon the hold is demand, not held: the view lists it
	// under Demand and the proposal's claim is covered.
	expired := shelter
	expired.Release = store.CommitmentExpired
	b := newStepArbiter()
	b.propose("bench", PlanResult{Kind: PlanProposed, Proposal: testProposal("bench", plannerFoothold, 2, wood(40), &committed)}, nil)
	if outcomes, failures = b.coordinate(context.Background(), stepBudget{Stock: stock, Commitments: store.PlanCommitments{Tick: 200, Demand: []store.PlanCommitment{expired}}}, proposalScope{}); len(failures) != 0 || len(outcomes) != 1 || !outcomes[0].Admitted || len(committed) != 1 {
		t.Fatalf("%+v %v %v", outcomes, failures, committed)
	}
}

// A more urgent proposal preempts a less urgent, undispatched commitment:
// the plan is retired through the preempt path and the released quantity
// is claimable in the same step, by the preempting proposal and by the
// next one. A commitment with dispatched work, or one the journal refuses
// to retire, leaves the proposal refused as demand.
func TestCoordinatePreemptsLessUrgentCommitmentInSameStep(t *testing.T) {
	t.Parallel()
	var committed []string
	var retired []store.PlanCommitment
	a := newStepArbiter()
	a.propose("bed", PlanResult{Kind: PlanProposed, Proposal: testProposal("bed", plannerFoothold, 1, wood(40), &committed)}, nil)
	a.propose("bench", PlanResult{Kind: PlanProposed, Proposal: testProposal("bench", plannerFoothold, 3, wood(50), &committed)}, nil)
	budget := stepBudget{Stock: map[policy.Resource]int64{"WoodLog": 100}, Commitments: store.PlanCommitments{Committed: []store.PlanCommitment{held("shelter", 3, 90, true), held("floor", 4, 5, true)}},
		Preempt: func(_ context.Context, c store.PlanCommitment) error { retired = append(retired, c); return nil }}
	outcomes, failures := a.coordinate(context.Background(), budget, proposalScope{})
	if len(failures) != 0 || len(outcomes) != 2 || len(committed) != 2 || committed[0] != "bed" || committed[1] != "bench" {
		t.Fatalf("%+v %v %v", outcomes, failures, committed)
	}
	// The least urgent plan alone does not cover the shortfall; the
	// shelter does, so only the shelter is retired.
	if len(retired) != 1 || retired[0].Plan != "shelter" || retired[0].Goal != "goal-shelter" || retired[0].Revision != 3 {
		t.Fatalf("%+v", retired)
	}
	if !outcomes[0].Admitted || len(outcomes[0].Preempted) != 1 || outcomes[0].Preempted[0] != "shelter" || outcomes[0].Reason != BuildingMethodAdmitted {
		t.Fatalf("%+v", outcomes[0])
	}
	if !outcomes[1].Admitted || len(outcomes[1].Preempted) != 0 {
		t.Fatalf("released quantity not claimable in the same step: %+v", outcomes[1])
	}
	// Dispatched work is never preempted.
	var none []string
	b := newStepArbiter()
	b.propose("bed", PlanResult{Kind: PlanProposed, Proposal: testProposal("bed", plannerFoothold, 1, wood(40), &none)}, nil)
	outcomes, failures = b.coordinate(context.Background(), stepBudget{Stock: budget.Stock, Commitments: store.PlanCommitments{Committed: []store.PlanCommitment{held("shelter", 3, 90, false)}}, Preempt: budget.Preempt}, proposalScope{})
	if len(failures) != 0 || len(none) != 0 || len(outcomes) != 1 || outcomes[0].Admitted || outcomes[0].Reason != BuildingMethodDemand || len(retired) != 1 {
		t.Fatalf("%+v %v %v", outcomes, failures, none)
	}
	// A retirement the journal refuses (the goal moved on) is an isolated
	// failure; the proposal is refused as demand and proposes again.
	stale := errors.New("conflict")
	c := newStepArbiter()
	c.propose("bed", PlanResult{Kind: PlanProposed, Proposal: testProposal("bed", plannerFoothold, 1, wood(40), &none)}, nil)
	outcomes, failures = c.coordinate(context.Background(), stepBudget{Stock: budget.Stock, Commitments: store.PlanCommitments{Committed: []store.PlanCommitment{held("shelter", 3, 90, true)}}, Preempt: func(context.Context, store.PlanCommitment) error { return stale }}, proposalScope{})
	if len(failures) != 1 || !errors.Is(failures[0], stale) || len(none) != 0 || len(outcomes) != 1 || outcomes[0].Admitted || outcomes[0].Reason != BuildingMethodDemand || len(outcomes[0].Preempted) != 0 {
		t.Fatalf("%+v %v %v", outcomes, failures, none)
	}
}

// A long plan commits only its admitted segment: the dependency-blocked
// remainder is demand in the view, not a hold, so a second project that
// the stock less the segment covers proceeds, and one it does not is
// refused as demand.
func TestCoordinateLongPlanCommitsOnlyAdmittedSegment(t *testing.T) {
	t.Parallel()
	segment := held("hut", 2, 60, true)
	remainder := held("hut", 2, 60, true)
	remainder.Action, remainder.Release = "hut-b", store.CommitmentBlocked
	view := store.PlanCommitments{Tick: 20, Committed: []store.PlanCommitment{segment}, Demand: []store.PlanCommitment{remainder}}
	if view.CommittedTotals()["WoodLog"] != 60 || view.DemandTotals()["WoodLog"] != 60 {
		t.Fatal(view)
	}
	var committed []string
	a := newStepArbiter()
	a.propose("bench", PlanResult{Kind: PlanProposed, Proposal: testProposal("bench", plannerFoothold, 3, wood(40), &committed)}, nil)
	a.propose("bed", PlanResult{Kind: PlanProposed, Proposal: testProposal("bed", plannerFoothold, 3, wood(50), &committed)}, nil)
	outcomes, failures := a.coordinate(context.Background(), stepBudget{Stock: map[policy.Resource]int64{"WoodLog": 120}, Commitments: view, Preempt: func(context.Context, store.PlanCommitment) error {
		t.Fatal("no preemption at equal urgency")
		return nil
	}}, proposalScope{})
	if len(failures) != 0 || len(outcomes) != 2 || len(committed) != 1 || committed[0] != "bed" {
		t.Fatalf("%+v %v %v", outcomes, failures, committed)
	}
	if !outcomes[0].Admitted || outcomes[0].Proposal != "bed" {
		t.Fatalf("%+v", outcomes[0])
	}
	if outcomes[1].Admitted || outcomes[1].Reason != BuildingMethodDemand || outcomes[1].Waiting != "WoodLog:40 of 10 held by bed committed to hut" || outcomes[1].Demand[0].Count != 30 {
		t.Fatalf("%+v", outcomes[1])
	}
}
