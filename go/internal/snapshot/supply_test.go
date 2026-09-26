package snapshot

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Recorded from acceptance run supply/loot-safety at 04b0a98c (#749): a
// 25-steel stack dropped 45 cells out, first on a trap, then with the trap
// removed. Both reviews ran at the case's paused tick 21.
const (
	lootOnTrap      = "testdata/supply-loot-on-trap.json"
	lootTrapRemoved = "testdata/supply-loot-trap-removed.json"
	lootSafetyThing = "Thing_Steel9442"
	lootSafetyDef   = "Steel"
)

// replayEventLoot is the review's loot pass (store.lootReachFilter then
// policy.ReviewEventLoot) over the recorded facts: the census the
// ManageSupplySafety planner forbids and allows from.
func replayEventLoot(t *testing.T, r Routine) policy.EventLootHistory {
	t.Helper()
	remote, err := policy.SalvageContext(r.Policy, r.Facts)
	if err != nil {
		t.Fatal(err)
	}
	census, held, err := policy.FilterLootReach(r.Facts.EventLoot, remote)
	if err != nil {
		t.Fatal(err)
	}
	loot, _, err := policy.ReviewEventLoot(census, policy.EventLootHistory{})
	if err != nil {
		t.Fatal(err)
	}
	loot.Held = held
	return loot
}

func pendingLoot(loot policy.EventLootHistory, thing string) (policy.StartingSupply, bool) {
	for _, row := range loot.Pending {
		if row.Thing == thing {
			return row, true
		}
	}
	return policy.StartingSupply{}, false
}

// Recorded from acceptance run supply/starting at 04b0a98c (#749) on the
// tribal8 baseline: the load review (tick 15, 23 forbidden starting stacks),
// the review with two stacks left (tick 83018) and the next one, after the
// last was allowed (tick 95886).
const (
	startingLoad       = "testdata/supply-starting-load-census.json"
	startingTwoLeft    = "testdata/supply-starting-two-left.json"
	startingAllAllowed = "testdata/supply-starting-all-allowed.json"
)

func TestStartingSuppliesLoadCensusIsTheCohort(t *testing.T) {
	r, err := Load(startingLoad)
	if err != nil {
		t.Fatal(err)
	}
	history, deficit, err := policy.ReviewStartingSupplies(r.Facts.StartingSupplies, policy.StartingSupplies{})
	if err != nil {
		t.Fatal(err)
	}
	if open, _ := deficit.Value(); !open || len(history.Pending) != 23 {
		t.Fatalf("load census: deficit %v, %d pending", deficit, len(history.Pending))
	}
	a, err := r.Assessment(policy.AllowStartingSupplies)
	if err != nil || a.Need != domain.NeedDeficit {
		t.Fatal(a, err)
	}
}

func TestStartingSuppliesRecoverOnceEveryStackIsAllowed(t *testing.T) {
	before, err := Load(startingTwoLeft)
	if err != nil {
		t.Fatal(err)
	}
	r, err := Load(startingAllAllowed)
	if err != nil {
		t.Fatal(err)
	}
	history, deficit, err := policy.ReviewStartingSupplies(r.Facts.StartingSupplies, before.Review.StartingSupplies)
	if err != nil {
		t.Fatal(err)
	}
	if open, known := deficit.Value(); !known || open || len(history.Pending) != 0 {
		t.Fatalf("after the last allow: deficit %v, pending %v", deficit, history.Pending)
	}
	// A stack forbidden later (a hunted animal's drop) is never adopted.
	later := domain.Known([]policy.StartingSupply{{Thing: "Thing_Later1", Definition: "Steel", Cell: domain.Cell{X: 5, Z: 5}}})
	if history, deficit, _ = policy.ReviewStartingSupplies(later, history); len(history.Pending) != 0 {
		t.Fatal("later forbid adopted", deficit, history.Pending)
	}
	a, err := r.Assessment(policy.AllowStartingSupplies)
	if err != nil || a.Need != domain.NeedRecovered {
		t.Fatal(a, err)
	}
}

// Recorded from acceptance run supply/loot-remote at 04b0a98c (#749): a
// forbidden, safe Steel stack near the far map edge under a Steel:2000
// target, first with one free hauler (base reach), then after the fixture
// raised readiness to five. Both reviews ran at the case's paused tick 21.
const (
	lootRemoteBase   = "testdata/supply-loot-remote-base-reach.json"
	lootRemoteRaised = "testdata/supply-loot-remote-reach-raised.json"
	lootRemoteThing  = "Thing_Steel9441"
)

func TestRemoteLootHeldUnderBaseReach(t *testing.T) {
	r, err := Load(lootRemoteBase)
	if err != nil {
		t.Fatal(err)
	}
	loot := replayEventLoot(t, r)
	if _, ok := pendingLoot(loot, lootRemoteThing); ok {
		t.Fatal("remote loot queued for Allow under base reach")
	}
	for _, hold := range loot.Held {
		if hold.Thing == lootRemoteThing {
			if hold.Reason != "outside_base:insufficient_defense" {
				t.Fatal("hold reason", hold.Reason)
			}
			return
		}
	}
	t.Fatal("remote loot not held", loot.Held)
}

func TestRemoteLootAllowedOnceReachRises(t *testing.T) {
	r, err := Load(lootRemoteRaised)
	if err != nil {
		t.Fatal(err)
	}
	row, ok := pendingLoot(replayEventLoot(t, r), lootRemoteThing)
	if !ok || row.Forbid {
		t.Fatalf("remote loot not queued for Allow: %+v %v", row, ok)
	}
}

func TestLootOnATrapIsForbidden(t *testing.T) {
	r, err := Load(lootOnTrap)
	if err != nil {
		t.Fatal(err)
	}
	row, ok := pendingLoot(replayEventLoot(t, r), lootSafetyThing)
	if !ok || !row.Forbid || row.Definition != lootSafetyDef {
		t.Fatalf("trapped loot not queued for Forbid: %+v %v", row, ok)
	}
	a, err := r.Assessment(policy.ManageSupplySafety)
	if err != nil || a.Need != domain.NeedDeficit {
		t.Fatal(a, err)
	}
}

func TestLootIsAllowedOnceItsTrapIsGone(t *testing.T) {
	r, err := Load(lootTrapRemoved)
	if err != nil {
		t.Fatal(err)
	}
	loot := replayEventLoot(t, r)
	row, ok := pendingLoot(loot, lootSafetyThing)
	if !ok || row.Forbid {
		t.Fatalf("safe loot not queued for Allow: %+v %v", row, ok)
	}
	for _, hold := range loot.Held {
		if hold.Thing == lootSafetyThing {
			t.Fatal("safe loot held", hold)
		}
	}
	a, err := r.Assessment(policy.ManageSupplySafety)
	if err != nil || a.Need != domain.NeedDeficit {
		t.Fatal(a, err)
	}
}
