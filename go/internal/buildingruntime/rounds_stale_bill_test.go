package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func staleAssessments(finding domain.Finding, stale bool) []policy.RoundsAssessment {
	return []policy.RoundsAssessment{{ID: policy.MaintainEquipment, Finding: finding, StaleBill: stale}}
}

// A bill is stale after StaleBillReviews consecutive reviews that found its
// owner Met; one Unmet review restarts the count, and a bill off the bench is
// forgotten (#2411).
func TestStaleBillsCountConsecutiveMetReviews(t *testing.T) {
	t.Parallel()
	var s staleBills
	bill := policy.StaleBill{Owner: policy.MaintainEquipment, Bench: "bench", ID: "Bill_1"}
	candidates := []policy.StaleBill{bill}
	for i := 0; i < policy.StaleBillReviews-1; i++ {
		s.observe(candidates, staleAssessments(domain.FindingMet, false))
	}
	if got := s.stale(candidates); len(got) != 0 {
		t.Fatalf("stale one review early: %v", got)
	}
	s.observe(candidates, staleAssessments(domain.FindingUnmet, false))
	s.observe(candidates, staleAssessments(domain.FindingMet, false))
	if got := s.stale(candidates); len(got) != 0 {
		t.Fatalf("an Unmet review did not restart the count: %v", got)
	}
	for i := 0; i < policy.StaleBillReviews-1; i++ {
		s.observe(candidates, staleAssessments(domain.FindingMet, false))
	}
	if got := s.stale(candidates); len(got) != 1 || got[0].ID != bill.ID {
		t.Fatalf("not stale after %d Met reviews: %v", policy.StaleBillReviews, got)
	}
	if first, ok := s.first(policy.MaintainEquipment); !ok || first.ID != bill.ID {
		t.Fatalf("first = %v %v", first, ok)
	}
	if _, ok := s.first(policy.MaintainArt); ok {
		t.Fatal("another owner's bill is due")
	}
	// The owner filed Unmet for the stale bill itself still counts as Met.
	s.observe(candidates, staleAssessments(domain.FindingUnmet, true))
	if got := s.stale(candidates); len(got) != 1 {
		t.Fatalf("the stale-bill finding reset the count: %v", got)
	}
	s.removed(bill.ID)
	if _, ok := s.first(policy.MaintainEquipment); ok || len(s.stale(candidates)) != 0 {
		t.Fatal("a removed bill stays due")
	}
	s.observe(nil, staleAssessments(domain.FindingMet, false))
	if len(s.streak) != 0 {
		t.Fatalf("a bill off the bench is remembered: %v", s.streak)
	}
}

// A bill outside its owner's demand is stale after StaleBillReviews reviews
// whatever the owner's finding; a review counts once however many steps run
// under it, a wanted bill restarts the count, a bill another planner judges is
// left alone, and the food owner's bills never count by owner Met (#2433).
func TestStaleBillsCountUnwantedReviews(t *testing.T) {
	t.Parallel()
	var s staleBills
	gear := policy.StaleBill{Owner: policy.MaintainEquipment, Bench: "bench", ID: "Bill_1", Products: []policy.Resource{"Apparel_Parka"}}
	bow := policy.StaleBill{Owner: policy.EnsureFoodSupply, Bench: "bench", ID: "Bill_2", Products: []policy.Resource{"Bow_Short"}}
	candidates := []policy.StaleBill{gear, bow}
	unmet := []policy.RoundsAssessment{{ID: policy.MaintainEquipment, Finding: domain.FindingUnmet}, {ID: policy.EnsureFoodSupply, Finding: domain.FindingMet}}
	s.observe(candidates, unmet)
	unwanted := func(policy.StaleBill) (bool, bool) { return true, false }
	for revision := uint64(1); revision < policy.StaleBillReviews; revision++ {
		s.noteWanted(revision, policy.MaintainEquipment, unwanted)
		s.noteWanted(revision, policy.MaintainEquipment, unwanted)
	}
	if _, ok := s.first(policy.MaintainEquipment); ok {
		t.Fatal("stale one review early, or a review counted twice")
	}
	s.noteWanted(policy.StaleBillReviews, policy.MaintainEquipment, func(policy.StaleBill) (bool, bool) { return false, false })
	s.noteWanted(policy.StaleBillReviews, policy.MaintainEquipment, func(policy.StaleBill) (bool, bool) { return true, true })
	if _, ok := s.first(policy.MaintainEquipment); ok {
		t.Fatal("a wanted bill kept its count")
	}
	for revision := uint64(10); revision < 10+policy.StaleBillReviews; revision++ {
		s.noteWanted(revision, policy.MaintainEquipment, unwanted)
	}
	if first, ok := s.first(policy.MaintainEquipment); !ok || first.ID != gear.ID {
		t.Fatalf("an unwanted bill is not due: %v %v", first, ok)
	}
	if _, ok := s.first(policy.EnsureFoodSupply); ok {
		t.Fatal("food's bill counted by another planner's note or by its owner Met")
	}
	// The food owner Met for any number of reviews never makes its bill stale.
	for i := 0; i < 2*policy.StaleBillReviews; i++ {
		s.observe(candidates, unmet)
	}
	if got := s.stale(candidates); len(got) != 0 {
		t.Fatalf("a Met food owner made a hunter-weapon bill stale: %v", got)
	}
	if _, ok := s.first(policy.MaintainEquipment); !ok {
		t.Fatal("observing forgot a counted bill still on the bench")
	}
	for revision := uint64(20); revision < 20+policy.StaleBillReviews; revision++ {
		s.noteWanted(revision, policy.EnsureFoodSupply, unwanted)
	}
	if first, ok := s.first(policy.EnsureFoodSupply); !ok || first.ID != bow.ID {
		t.Fatalf("a hunter-weapon bill outside the demand is not due: %v %v", first, ok)
	}
	// Removal restarts the count, and a bill off the bench is forgotten.
	s.removed(gear.ID)
	if _, ok := s.first(policy.MaintainEquipment); ok {
		t.Fatal("a removed bill stays due")
	}
	s.observe(nil, unmet)
	if len(s.unwanted) != 0 {
		t.Fatalf("bills off the bench are remembered: %v", s.unwanted)
	}
}

// removeStaleBill commits one RemoveProductionBill action under the owner,
// restarts the bill's count, and a retry after a refusal is a new method.
func TestRemoveStaleBillCommitsOneAction(t *testing.T) {
	planner, db, session := rulesPlannerFixture(t)
	reviewer := planner.reviewer
	ctx := context.Background()
	bill := policy.StaleBill{Owner: policy.EnsureFoodSupply, Bench: "bench-1", ID: "Bill_Production_77"}
	removeOnce := func() domain.PlanID {
		t.Helper()
		review, err := db.LoadRounds(ctx)
		if err != nil {
			t.Fatal(err)
		}
		goal, workable, err := db.Workable(ctx, review, policy.EnsureFoodSupply)
		if err != nil || !workable {
			t.Fatalf("EnsureFoodSupply not workable: %v", err)
		}
		call, epoch, done, err := reviewer.player.enter(ctx, "test", false)
		if err != nil {
			t.Fatal(err)
		}
		defer done()
		plan, err := reviewer.removeStaleBill(call, epoch, nil, session.State(), goal, policy.EnsureFoodSupply)
		if err != nil {
			t.Fatal(err)
		}
		return plan
	}
	if plan := removeOnce(); plan != "" {
		t.Fatalf("a plan without a stale bill: %s", plan)
	}
	reviewer.staleBills.due = []policy.StaleBill{bill}
	first := removeOnce()
	if first == "" {
		t.Fatal("no removal committed")
	}
	loaded, err := db.LoadPlan(ctx, first)
	if err != nil || len(loaded.Progress) != 1 {
		t.Fatal(loaded, err)
	}
	removal, ok := loaded.Progress[0].Action().RemoveProductionBill()
	if !ok || removal.Bench() != "bench-1" || removal.Bill() != "Bill_Production_77" {
		t.Fatalf("removal = %+v", removal)
	}
	if _, ok := reviewer.staleBills.first(policy.EnsureFoodSupply); ok {
		t.Fatal("the committed bill is still due")
	}
	// A refused attempt settles; the retry is a new method of the owner.
	if _, err = db.Cancel(ctx, first, domain.ActionID(string(first)+"-0")); err != nil {
		t.Fatal(err)
	}
	reviewer.staleBills.due = []policy.StaleBill{bill}
	if second := removeOnce(); second == "" || second == first {
		t.Fatalf("the retry was not a new method: %q %q", second, first)
	}
}
