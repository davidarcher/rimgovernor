package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func remoteLootRow(thing string, cell domain.Cell, forbidden, safe bool) LootItem {
	return LootItem{Supply: StartingSupply{Thing: thing, Definition: "Steel", Cell: cell}, Forbidden: forbidden, SafeToHaul: safe, SafetyKnown: true,
		Count: 75, PathLength: domain.Known(120.0), StorageHeadroom: domain.Known(int64(300))}
}

func steelDemand() domain.Fact[[]ResourceDemand] {
	return domain.Known([]ResourceDemand{{Key: ResourceKey{Def: "Steel"}, Count: 200, Priority: 2}})
}

func TestRemoteLootHoldsOnSafetyOnly(t *testing.T) {
	remote := domain.Cell{X: 95, Z: 95}
	for _, tt := range []struct {
		name   string
		edit   func(*RemoteSafety)
		kept   bool
		reason string
	}{
		{"quiet allows remote", func(r *RemoteSafety) {}, true, ""},
		{"threat holds", func(r *RemoteSafety) { r.Threat = domain.Known(true) }, false, "threat_present"},
		{"unknown threat holds", func(r *RemoteSafety) { r.Threat = domain.Unknown[bool]() }, false, "threat_unknown"},
		{"cold holds", func(r *RemoteSafety) { r.Hazard = domain.Known([]OutdoorHazard{{Kind: HazardExtremeCold}}) }, false, RemoteHoldHazard},
		{"cold lifted by a dash window", func(r *RemoteSafety) {
			r.Hazard, r.DashOpen = domain.Known([]OutdoorHazard{{Kind: HazardExtremeCold}}), true
		}, true, ""},
	} {
		t.Run(tt.name, func(t *testing.T) {
			r := quietSafety()
			tt.edit(&r)
			rows, holds, err := FilterLootReach(domain.Known([]LootItem{remoteLootRow("steel-1", remote, true, true)}), RemoteWorkRequest{Safety: r, Extent: tribal8Extent(), Demand: steelDemand()})
			if err != nil {
				t.Fatal(err)
			}
			kept, _ := rows.Value()
			if (len(kept) == 1) != tt.kept || (len(holds) == 1) != !tt.kept {
				t.Fatalf("kept %v holds %v", kept, holds)
			}
			if !tt.kept && holds[0].Reason != tt.reason {
				t.Fatalf("reason %q", holds[0].Reason)
			}
		})
	}
}

func TestRemoteLootKeepsSafetySemantics(t *testing.T) {
	r := quietSafety()
	r.Threat = domain.Known(true)
	remote := domain.Cell{X: 95, Z: 95}
	rows := []LootItem{
		remoteLootRow("unsafe-allowed", remote, false, true),
		remoteLootRow("unsafe-forbidden", remote, true, false),
		remoteLootRow("safe-allowed", remote, false, true),
		remoteLootRow("inside-extent", domain.Cell{X: 20, Z: 20}, true, true),
		remoteLootRow("remote-forbidden", remote, true, true),
	}
	rows[0].SafeToHaul = false
	kept, holds, err := FilterLootReach(domain.Known(rows), RemoteWorkRequest{Safety: r, Extent: tribal8Extent(), Demand: steelDemand()})
	if err != nil {
		t.Fatal(err)
	}
	got, _ := kept.Value()
	if len(got) != 4 || len(holds) != 1 || holds[0].Thing != "remote-forbidden" {
		t.Fatalf("kept %v holds %v", got, holds)
	}
	next, need, err := ReviewEventLoot(kept, EventLootHistory{})
	if err != nil || need != domain.Known(true) || len(next.Pending) != 2 {
		t.Fatal(next, need, err)
	}
	if next.Pending[0].Thing != "inside-extent" || next.Pending[0].Forbid || next.Pending[1].Thing != "unsafe-allowed" || !next.Pending[1].Forbid {
		t.Fatal(next.Pending)
	}
	next.Held = holds
	if err := next.Validate(); err != nil {
		t.Fatal(err)
	}
	if _, _, err := FilterLootReach(domain.Unknown[[]LootItem](), RemoteWorkRequest{Safety: r}); err != nil {
		t.Fatal(err)
	}
}

// A safe stack is released whatever the demand: covered, unknown or
// absent demand and an unknown path hold nothing; only storage headroom and
// urgent colony work throttle it.
func TestRemoteLootReleasesWithoutDemand(t *testing.T) {
	r := quietSafety()
	remote := domain.Cell{X: 95, Z: 95}
	noHeadroom := remoteLootRow("s", remote, true, true)
	noHeadroom.StorageHeadroom = domain.Known(int64(0))
	unknownHeadroom := remoteLootRow("s", remote, true, true)
	unknownHeadroom.StorageHeadroom = domain.Unknown[int64]()
	unknownPath := remoteLootRow("s", remote, true, true)
	unknownPath.PathLength = domain.Unknown[float64]()
	urgent := AcquisitionCompetition{UrgentPriority: UrgentWorkPriority}
	for _, tt := range []struct {
		name        string
		row         LootItem
		demand      domain.Fact[[]ResourceDemand]
		competition AcquisitionCompetition
		reason      string
	}{
		{"unknown demand", remoteLootRow("s", remote, true, true), domain.Unknown[[]ResourceDemand](), AcquisitionCompetition{}, ""},
		{"no demand", remoteLootRow("s", remote, true, true), domain.Known([]ResourceDemand{}), AcquisitionCompetition{}, ""},
		{"other demand", remoteLootRow("s", remote, true, true), domain.Known([]ResourceDemand{{Key: ResourceKey{Def: "WoodLog"}, Count: 50, Priority: 2}}), AcquisitionCompetition{}, ""},
		{"covered resource", remoteLootRow("s", remote, true, true), steelDemand(), AcquisitionCompetition{}, ""},
		{"unknown path", unknownPath, steelDemand(), AcquisitionCompetition{}, ""},
		{"no storage", noHeadroom, steelDemand(), AcquisitionCompetition{}, "missing_storage"},
		{"unknown storage", unknownHeadroom, steelDemand(), AcquisitionCompetition{}, "missing_storage"},
		{"urgent work", remoteLootRow("s", remote, true, true), steelDemand(), urgent, "urgent_competing_work"},
	} {
		t.Run(tt.name, func(t *testing.T) {
			kept, holds, err := FilterLootReach(domain.Known([]LootItem{tt.row}), RemoteWorkRequest{Safety: r, Extent: tribal8Extent(), Demand: tt.demand, Competition: tt.competition})
			if err != nil {
				t.Fatal(err)
			}
			rows, _ := kept.Value()
			if tt.reason == "" {
				if len(rows) != 1 || len(holds) != 0 {
					t.Fatalf("kept %v holds %v", rows, holds)
				}
				return
			}
			if len(rows) != 0 || len(holds) != 1 || holds[0].Reason != tt.reason {
				t.Fatalf("kept %v holds %v", rows, holds)
			}
		})
	}
}

func TestLootDemandAndReachFromFacts(t *testing.T) {
	p := DefaultRoundsPolicy()
	f := RoundsFacts{Resources: domain.Known([]Amount{{Resource: "Steel", Count: 120}, {Resource: "WoodLog", Count: 500}}), ResourceNeeds: map[Resource]int64{"Steel": 300}}
	short, err := recoveryShortResources(p, f)
	if err != nil || len(short) != 1 || !short["Steel"] {
		t.Fatal(short, err)
	}
	if short, err = recoveryShortResources(p, RoundsFacts{}); err != nil || len(short) != 0 {
		t.Fatal("unknown stock certified shortage", short, err)
	}
	f.Hostiles = domain.Known(int64(0))
	if r := LootSafety(f); remoteThreatHold(r) != "" {
		t.Fatal(remoteThreatHold(r))
	}
	f.Hostiles = domain.Known(int64(1))
	if got := remoteThreatHold(LootSafety(f)); got != RemoteHoldThreat {
		t.Fatal(got)
	}
	f.Hostiles = domain.Unknown[int64]()
	r := LootSafety(f)
	if _, known := r.Threat.Value(); known {
		t.Fatal("unknown hostiles became a threat verdict")
	}
	if got := remoteThreatHold(r); got != "threat_unknown" {
		t.Fatal(got)
	}
}

// A colony whose established extent is empty -- it owns no facility, or the
// only facility anchoring its extent was removed -- still releases a safe
// forbidden remote stack: extent geometry never holds loot.
func TestRemoteLootEmptyExtentReleases(t *testing.T) {
	row := remoteLootRow("steel-1", domain.Cell{X: 95, Z: 95}, true, true)
	kept, holds, err := FilterLootReach(domain.Known([]LootItem{row}), RemoteWorkRequest{Safety: quietSafety(), Extent: domain.Known(ColonyExtent{}), Demand: steelDemand()})
	if err != nil {
		t.Fatal(err)
	}
	rows, _ := kept.Value()
	if len(rows) != 1 || len(holds) != 0 {
		t.Fatalf("kept %v holds %v", rows, holds)
	}
	next, need, err := ReviewEventLoot(kept, EventLootHistory{})
	if err != nil || need != domain.Known(true) || len(next.Pending) != 1 || next.Pending[0].Forbid {
		t.Fatal(next, need, err)
	}
}

// The startup release: 23 starting stacks outside the colony extent
// under a threat are all pending on the first review, and the stacks left after each
// 8-action plan stay pending on the following reviews. A stack that first
// appears later is held; an unsafe stack is never pending.
func TestStartupReleaseFinishesAcrossReviews(t *testing.T) {
	r := quietSafety()
	r.Threat = domain.Known(true)
	remote := RemoteWorkRequest{Safety: r, Extent: tribal8Extent(), Demand: steelDemand()}
	cell := domain.Cell{X: 95, Z: 95}
	var rows []LootItem
	for i := 0; i < 23; i++ {
		rows = append(rows, remoteLootRow(string(rune('a'+i)), cell, true, true))
	}
	rows[3].SafeToHaul = false
	review := func(census []LootItem, previous EventLootHistory, first bool) EventLootHistory {
		t.Helper()
		kept, holds, err := FilterLootReachAdmitted(domain.Known(census), remote, previous, first)
		if err != nil {
			t.Fatal(err)
		}
		next, _, err := ReviewEventLoot(kept, previous)
		if err != nil {
			t.Fatal(err)
		}
		next.Held = holds
		return next
	}
	history := review(rows, EventLootHistory{}, true)
	if len(history.Pending) != 22 || len(history.Held) != 0 {
		t.Fatalf("first review: %d pending, held %v", len(history.Pending), history.Held)
	}
	for _, row := range history.Pending {
		if row.Thing == rows[3].Supply.Thing {
			t.Fatal("unsafe stack pending release")
		}
	}
	for want := 22 - 8; want > 0; want -= 8 {
		// A plan allows the first 8 pending stacks; the rest stay forbidden.
		allowed := map[string]bool{}
		for _, row := range history.Pending[:min(8, len(history.Pending))] {
			allowed[row.Thing] = true
		}
		for i := range rows {
			if allowed[rows[i].Supply.Thing] {
				rows[i].Forbidden = false
			}
		}
		history = review(rows, history, false)
		if len(history.Pending) != want || len(history.Held) != 0 {
			t.Fatalf("want %d pending, got %d, held %v", want, len(history.Pending), history.Held)
		}
	}
	// A stack forbidden later was never admitted: reach holds it.
	rows = append(rows, remoteLootRow("later", cell, true, true))
	history = review(rows, history, false)
	if len(history.Held) != 1 || history.Held[0].Thing != "later" {
		t.Fatalf("later forbid not held: %v", history.Held)
	}
}
