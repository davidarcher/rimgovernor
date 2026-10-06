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
func replayEventLoot(t *testing.T, r Rounds, first bool) policy.EventLootHistory {
	t.Helper()
	remote, err := policy.SalvageContext(r.Policy, r.Facts)
	if err != nil {
		t.Fatal(err)
	}
	released, held := policy.FilterLootRelease(r.Facts.EventLoot, r.Facts.DangerSeeds)
	census, reach, err := policy.FilterLootReachAdmitted(released, remote, policy.EventLootHistory{}, first)
	if err != nil {
		t.Fatal(err)
	}
	held = append(held, reach...)
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
// and the review after the last was allowed (tick 95886). ManageSupplySafety releases them from the
// loot census like any other safe forbidden stack (#2188).
const (
	startingLoad       = "testdata/supply-starting-load-census.json"
	startingAllAllowed = "testdata/supply-starting-all-allowed.json"
)

func TestStartingStacksNearHomeAreReleasedBySupplySafety(t *testing.T) {
	r, err := Load(startingLoad)
	if err != nil {
		t.Fatal(err)
	}
	loot := replayEventLoot(t, r, true)
	if len(loot.Pending) != 23 || len(loot.Held) != 0 {
		t.Fatalf("load census: %d pending, held %v", len(loot.Pending), loot.Held)
	}
	for _, row := range loot.Pending {
		if row.Forbid {
			t.Fatal("starting stack pending a forbid", row)
		}
	}
	r.Facts.EventLootPending = domain.Known(true)
	a, err := r.Assessment(policy.ManageSupplySafety)
	if err != nil || a.Finding != domain.FindingUnmet {
		t.Fatal(a, err)
	}
	// An unsafe stack stays forbidden: it is never pending release.
	rows, _ := r.Facts.EventLoot.Value()
	rows[0].SafeToHaul = false
	unsafe := rows[0].Supply.Thing
	r.Facts.EventLoot = domain.Known(rows)
	loot = replayEventLoot(t, r, true)
	if _, pending := pendingLoot(loot, unsafe); pending || len(loot.Pending) != 22 {
		t.Fatalf("unsafe stack released: %d pending", len(loot.Pending))
	}
}

func TestSupplySafetyRecoversOnceEveryStartingStackIsAllowed(t *testing.T) {
	r, err := Load(startingAllAllowed)
	if err != nil {
		t.Fatal(err)
	}
	if loot := replayEventLoot(t, r, false); len(loot.Pending) != 0 {
		t.Fatalf("after the last allow: pending %v", loot.Pending)
	}
	r.Facts.EventLootPending = domain.Known(false)
	a, err := r.Assessment(policy.ManageSupplySafety)
	if err != nil || a.Finding != domain.FindingMet {
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
	loot := replayEventLoot(t, r, false)
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
	row, ok := pendingLoot(replayEventLoot(t, r, false), lootRemoteThing)
	if !ok || row.Forbid {
		t.Fatalf("remote loot not queued for Allow: %+v %v", row, ok)
	}
}

func TestLootOnATrapIsForbidden(t *testing.T) {
	r, err := Load(lootOnTrap)
	if err != nil {
		t.Fatal(err)
	}
	row, ok := pendingLoot(replayEventLoot(t, r, false), lootSafetyThing)
	if !ok || !row.Forbid || row.Definition != lootSafetyDef {
		t.Fatalf("trapped loot not queued for Forbid: %+v %v", row, ok)
	}
	a, err := r.Assessment(policy.ManageSupplySafety)
	if err != nil || a.Finding != domain.FindingUnmet {
		t.Fatal(a, err)
	}
}

func TestLootIsAllowedOnceItsTrapIsGone(t *testing.T) {
	r, err := Load(lootTrapRemoved)
	if err != nil {
		t.Fatal(err)
	}
	loot := replayEventLoot(t, r, false)
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
	if err != nil || a.Finding != domain.FindingUnmet {
		t.Fatal(a, err)
	}
}

// The recorded safe stack, with a hive recorded beside it, stays forbidden:
// the danger gate holds it back (#1802).
func TestLootNextToAHiveStaysForbidden(t *testing.T) {
	r, err := Load(lootTrapRemoved)
	if err != nil {
		t.Fatal(err)
	}
	rows, _ := r.Facts.EventLoot.Value()
	var at domain.Cell
	for _, row := range rows {
		if row.Supply.Thing == lootSafetyThing {
			at = row.Supply.Cell
		}
	}
	hive := policy.EmergencyThreat{ID: "hive", Kind: policy.HostileBuilding, Definition: "Hive", Passive: domain.Known(true), Cells: []domain.Cell{{X: at.X + 3, Z: at.Z}}}
	r.Facts.DangerSeeds = domain.Known(policy.DangerSeeds([]policy.EmergencyThreat{hive}))
	loot := replayEventLoot(t, r, false)
	if _, ok := pendingLoot(loot, lootSafetyThing); ok {
		t.Fatal("loot beside a hive queued for Allow")
	}
	held := false
	for _, hold := range loot.Held {
		held = held || hold.Thing == lootSafetyThing && hold.Reason == policy.LootHoldDanger
	}
	if !held {
		t.Fatal("no danger hold", loot.Held)
	}
}
