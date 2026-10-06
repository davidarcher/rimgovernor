package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
)

// rulesPlannerFixture is an open, unmet EnsureFoodSupply Standard and the
// rules planner over it.
func rulesPlannerFixture(t *testing.T) (*RoundsRulesPlanner, *store.Store, *playerFakeSession) {
	t.Helper()
	reviewer, db, session, _, n := roundsFixture(t)
	reviewer.methods = domain.Known([]policy.ConcernID{policy.EnsureFoodSupply})
	foodPlanFixture(n.reply.GetObserved())
	if _, err := reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoundsRulesPlanner(reviewer)
	if err != nil {
		t.Fatal(err)
	}
	return planner, db, session
}

func huntChainSet(t *testing.T) policy.RuleSet {
	t.Helper()
	set, known := policy.HuntChainRules(domain.Known([]policy.AcquisitionSource{{ID: "deer", Hunt: true, Designated: true}, {ID: "elk", Hunt: true, Designated: true}}), domain.Known([]policy.PawnProfile{{ID: "hunter", Ranged: true}}))
	if !known || len(set.Rules) != 1 {
		t.Fatalf("set = %+v", set)
	}
	return set
}

// attachAt runs one Round's attachment of set at tick against the goal as the
// journal holds it now.
func attachAt(t *testing.T, planner *RoundsRulesPlanner, db *store.Store, session *playerFakeSession, tick domain.Tick, set policy.RuleSet) RoundsRulesResult {
	t.Helper()
	ctx := context.Background()
	review, err := db.LoadRounds(ctx)
	if err != nil {
		t.Fatal(err)
	}
	goal, workable, err := db.Workable(ctx, review, policy.EnsureFoodSupply)
	if err != nil || !workable {
		t.Fatalf("EnsureFoodSupply not workable: %+v %v", goal.Standard, err)
	}
	call, epoch, done, err := planner.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		t.Fatal(err)
	}
	defer done()
	result, err := planner.attach(call, epoch, session.State(), goal, tick, set)
	if err != nil {
		t.Fatal(err)
	}
	return result
}

func committedRules(t *testing.T, db *store.Store, plan domain.PlanID) domain.RulesAttach {
	t.Helper()
	loaded, err := db.LoadPlan(context.Background(), plan)
	if err != nil || len(loaded.Progress) != 1 {
		t.Fatal(loaded, err)
	}
	attach, ok := loaded.Progress[0].Action().RulesAttach()
	if !ok {
		t.Fatal("not a rules attach", loaded.Progress[0].Action())
	}
	return attach
}

// TestRulesPlannerRenewsTheLeaseEveryRound: each Round commits a fresh
// rules_attach method with the full lease, though the previous attachment is
// still open; a repeated tick is the same Round; an empty set clears only
// what an earlier Round attached.
func TestRulesPlannerRenewsTheLeaseEveryRound(t *testing.T) {
	planner, db, session := rulesPlannerFixture(t)
	set := huntChainSet(t)

	if got := attachAt(t, planner, db, session, 100, policy.RuleSet{}); got.Verdict != BuildingReasonNoDeficit {
		t.Fatalf("an empty set with nothing attached committed: %+v", got)
	}
	first := attachAt(t, planner, db, session, 100, set)
	if first.Verdict != BuildingReasonAdmitted {
		t.Fatalf("first Round: %+v", first)
	}
	if attach := committedRules(t, db, first.Plan); attach.LeaseTicks() != policy.RuleLeaseTicks || len(attach.Rules()) != 1 || attach.Rules()[0].ID != policy.HuntChainRuleID {
		t.Fatalf("attachment = %+v", attach)
	}
	if again := attachAt(t, planner, db, session, 100, set); again.Verdict.Outcome != OutcomeWaiting {
		t.Fatalf("the same Round attached twice: %+v", again)
	}
	second := attachAt(t, planner, db, session, 100+domain.Tick(policy.RuleLeaseTicks)/2, set)
	if second.Verdict != BuildingReasonAdmitted || second.Plan == first.Plan {
		t.Fatalf("renewal: %+v", second)
	}
	if attach := committedRules(t, db, second.Plan); attach.LeaseTicks() != policy.RuleLeaseTicks {
		t.Fatalf("renewed lease = %d", attach.LeaseTicks())
	}

	cleared := attachAt(t, planner, db, session, 100+domain.Tick(policy.RuleLeaseTicks), policy.RuleSet{})
	if cleared.Verdict != BuildingReasonAdmitted {
		t.Fatalf("clear: %+v", cleared)
	}
	if attach := committedRules(t, db, cleared.Plan); len(attach.Rules()) != 0 {
		t.Fatalf("clear carried rules: %+v", attach.Rules())
	}
	if again := attachAt(t, planner, db, session, 100+2*domain.Tick(policy.RuleLeaseTicks), policy.RuleSet{}); again.Verdict != BuildingReasonNoDeficit {
		t.Fatalf("a cleared set attached again: %+v", again)
	}
}

// TestRuleDecisionRows: a native firing and a lapsed lease become one rule
// decision row each, the firing keyed on the actor and the trigger word.
func TestRuleDecisionRows(t *testing.T) {
	fired := ruleDecision(&k.Event{Cursor: i64ptr(7), Event: &k.Event_RuleFired{RuleFired: &k.RuleFired{
		RuleId: strptr("hunt-chain"), Job: strptr("Hunt"), Radius: u32ptr(80), ActorId: strptr("Thing_Human1"), TargetId: strptr("Thing_Deer9"), Tick: i64ptr(412)}}})
	if fired.Kind != "rule" || fired.Verdict != "fired" || fired.Reason != "prey_killed" || fired.Target != "Thing_Human1" {
		t.Fatalf("fired = %+v", fired)
	}
	params, _ := fired.Attrs["params"].(map[string]any)
	if fired.Attrs["rule"] != "hunt-chain" || fired.Attrs["target"] != "Thing_Deer9" || params["job"] != "Hunt" || params["radius"] != uint32(80) || fired.Attrs["tick"] != int64(412) || fired.Attrs["cursor"] != int64(7) {
		t.Fatalf("fired attrs = %+v", fired.Attrs)
	}
	expired := ruleDecision(&k.Event{Cursor: i64ptr(9), Event: &k.Event_RuleLeaseExpired{RuleLeaseExpired: &k.RuleLeaseExpired{ExpiresAtTick: i64ptr(2900), Deactivated: u32ptr(1)}}})
	if expired.Kind != "rule" || expired.Verdict != "expired" || expired.Reason != "lease_expired" || expired.Attrs["expires_at_tick"] != int64(2900) || expired.Attrs["deactivated"] != uint32(1) {
		t.Fatalf("expired = %+v", expired)
	}
}

func strptr(s string) *string { return &s }
func u32ptr(v uint32) *uint32 { return &v }
func i64ptr(v int64) *int64   { return &v }
