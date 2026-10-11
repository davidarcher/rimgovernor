package buildingruntime

import (
	"context"
	"reflect"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
)

// defenseHoldSource refuses every native read: a layout step that is not held
// reaches it and fails, which is enough to tell a hold from a release.
type defenseHoldSource struct{ RoundsDefenseLayoutSource }

// shelterOwedReview is a review binding MaintainHousing on its shelter phase
// with the goal's finding as given.
func shelterOwedReview(finding domain.Finding, emergency ...policy.ConcernID) *store.RoundsResult {
	review := &store.RoundsResult{Emergency: emergency}
	review.Review.Latches.Housing = policy.HousingShelter
	review.Review.Standards = []store.RoundsStandard{{Concern: policy.MaintainHousing, Standard: policy.MaintainHousing}}
	review.Standards = []store.StandardState{{Standard: domain.Standard{ID: policy.MaintainHousing, Status: domain.StandardOpen, Finding: finding}}}
	return review
}

// While the initial shelter is owed only the allowlist runs: what builds it
// and what answers an event. A concern the review calls an emergency also runs.
func TestShelterGateHoldsEveryPlannerOutsideTheAllowlist(t *testing.T) {
	names := map[string]bool{}
	for _, entry := range plannerCatalog {
		names[entry.name] = true
	}
	for name := range runsBeforeShelter {
		if !names[name] {
			t.Fatalf("runsBeforeShelter names %q, which is not in the catalog", name)
		}
	}
	owed := shelterOwedReview(domain.FindingUnmet)
	for _, entry := range plannerCatalog {
		if _, held := heldByPriority(entry, owed); held == runsBeforeShelter[entry.name] {
			t.Errorf("%s held=%v while the shelter is owed, allowlisted=%v", entry.name, held, runsBeforeShelter[entry.name])
		}
		if _, a := heldByPriority(entry, shelterOwedReview(domain.FindingMet)); a {
			t.Errorf("%s held without an owed shelter", entry.name)
		}
		if _, b := heldByPriority(entry, nil); b {
			t.Errorf("%s held without an owed shelter", entry.name)
		}
	}
	emergency := shelterOwedReview(domain.FindingUnmet, policy.MaintainMedicalReserves)
	for _, entry := range plannerCatalog {
		switch entry.name {
		case "medical":
			if _, held := heldByPriority(entry, emergency); held {
				t.Error("medical reserves are an emergency and must run")
			}
		case "surgery":
			if _, held := heldByPriority(entry, emergency); !held {
				t.Error("surgery is not the emergency and must wait")
			}
		}
	}
}

// A held planner is queued as a hold: its own run never starts and it files
// the shelter pending; an allowlisted planner runs as before.
func TestShelterGateQueuesHeldPlannerAsHold(t *testing.T) {
	ran := map[string]bool{}
	entry := func(name string, class plannerClass) plannerEntry {
		return plannerEntry{name: name, class: class, priority: plannerFoothold,
			configured: func(*ClockSchedulerConfig) bool { return true },
			run: func(*ClockScheduler, context.Context, context.Context, *ClockSchedulerResult, *stepArbiter) (Verdict, error) {
				ran[name] = true
				return BuildingReasonAdmitted, nil
			}}
	}
	s := &ClockScheduler{catalog: []plannerEntry{entry("work", classOptional), entry("cooking", classOptional)}}
	ctx := context.Background()
	wave := newPlannerWave(ctx)
	defer wave.cancelOptional()
	queued := s.queuePlanners(ctx, ctx, wave, nil, nil, false, shelterOwedReview(domain.FindingUnmet))
	if !reflect.DeepEqual(queued, []string{"work", "cooking"}) {
		t.Fatal("the held planner left the queue:", queued)
	}
	if pending := wave.group.WaitUntil(nil); len(pending) != 0 {
		t.Fatal(pending)
	}
	if !ran["work"] || ran["cooking"] {
		t.Fatal("ran:", ran)
	}
	if v, ok := wave.reason("cooking"); !ok || v != BuildingShelterPending {
		t.Fatal("held planner filed", v, ok)
	}
	if v, ok := wave.reason("work"); !ok || v != BuildingReasonAdmitted {
		t.Fatal("allowlisted planner filed", v, ok)
	}
}

// The defense layout's killbox digs are the answer to a raid: with no
// ActiveCombat incident in the world the step holds on the threat, and the
// incident releases it to read the site.
func TestRoundsDefenseLayoutWaitsForAThreat(t *testing.T) {
	p, _, _, _ := playerFixture(t)
	request := playerAcquire(t, p)
	ctx := context.Background()
	if _, err := p.Resume(ctx, request); err != nil {
		t.Fatal(err)
	}
	native := colonyCoreNative(t)
	layout := policy.DefaultRoundsPolicy()
	layout.DefensiveLayout = true
	reviewer, err := NewRounder(p, native, testkit.NewManualClock(time.Now()), layout, time.Second)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	planner := &RoundsDefenseLayoutPlanner{reviewer: reviewer, native: defenseHoldSource{}}
	held, err := planner.step(ctx, ctx, nil)
	if err != nil || held.Verdict != BuildingThreatPending {
		t.Fatal("the layout ran with no threat seen:", held.Verdict, err)
	}
	review, err := p.journal.LoadRounds(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := p.journal.OpenIncident(ctx, store.IncidentAssessment{Kind: policy.ActiveCombat, Trigger: "raid", Snapshot: review.Snapshot, Tick: 10}); err != nil {
		t.Fatal(err)
	}
	func() {
		defer func() { _ = recover() }() // a release reaches the refusing source
		if released, _ := planner.step(ctx, ctx, nil); released.Verdict == BuildingThreatPending {
			t.Fatal("the layout stayed held after a raid")
		}
	}()
}

// A ring is not placed in a stuff with no accessible stock while the start kit
// is still forbidden: the supply planner has yet to allow it.
func TestShellMaterialsWaitForTheForbiddenStartKit(t *testing.T) {
	facts := observation.ColonyProjection{Resources: domain.Known(map[policy.Resource]int64{"WoodLog": 0, "BlocksGranite": 0})}
	facts.Facts.EventLoot = domain.Known([]policy.LootItem{{Forbidden: true, SafeToHaul: true, SafetyKnown: true, Count: 200}})
	if !unstockedUntilKitAllowed(facts, "BlocksGranite", "BlocksGranite") {
		t.Fatal("an unstocked stuff was committed while the kit is forbidden")
	}
	facts.Resources = domain.Known(map[policy.Resource]int64{"WoodLog": 300})
	if unstockedUntilKitAllowed(facts, "WoodLog") {
		t.Fatal("a stocked stuff was held")
	}
	facts.Resources = domain.Known(map[policy.Resource]int64{})
	facts.Facts.EventLoot = domain.Known([]policy.LootItem{{Forbidden: false, SafeToHaul: true, SafetyKnown: true, Count: 200}})
	if unstockedUntilKitAllowed(facts, "BlocksGranite") {
		t.Fatal("a ring waited after the kit was allowed")
	}
	facts.Facts.EventLoot = domain.Unknown[[]policy.LootItem]()
	if unstockedUntilKitAllowed(facts, "BlocksGranite") {
		t.Fatal("an unread kit held the ring")
	}
}
