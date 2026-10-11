package buildingruntime

import (
	"context"
	"testing"
	"time"

	"google.golang.org/protobuf/proto"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
)

// defenseHoldSource refuses every native read: a layout step that is not held
// reaches it and fails, which is enough to tell a hold from a release.
type defenseHoldSource struct{ RoundsDefenseLayoutSource }

// The defense layout's killbox digs wait for the initial shelter (#2818): the
// held step reports the shelter pending, the released one goes on to read.
func TestRoundsDefenseLayoutWaitsForInitialShelter(t *testing.T) {
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
	v := native.reply.GetObserved()
	v.ColonistCount, v.WorkerCount = proto.Uint32(2), proto.Uint32(2)
	v.BedCapacity, v.IndoorSleepingCapacity = proto.Uint32(0), proto.Uint32(0)
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	planner := &RoundsDefenseLayoutPlanner{reviewer: reviewer, native: defenseHoldSource{}}
	held, err := planner.step(ctx, ctx, nil)
	if err != nil || held.Verdict != BuildingShelterPending {
		t.Fatal("the layout ran while the shelter was owed:", held.Verdict, err)
	}
	v.BedCapacity, v.IndoorSleepingCapacity = proto.Uint32(2), proto.Uint32(2)
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	func() {
		defer func() { _ = recover() }() // a release reaches the refusing source
		if released, _ := planner.step(ctx, ctx, nil); released.Verdict == BuildingShelterPending {
			t.Fatal("the layout stayed held after the shelter stood")
		}
	}()
}

// While the initial shelter is owed, map-wide clearance waits; the review that
// sees the shelter stand releases it (#2814).
func TestRoundsClearanceWaitsForInitialShelter(t *testing.T) {
	reviewer, _, planner, source := clearanceBatchFixture(t)
	ctx := context.Background()
	v := source.reply.GetObserved()
	v.BedCapacity, v.IndoorSleepingCapacity = proto.Uint32(0), proto.Uint32(0)
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	held, err := planner.Step(ctx)
	if err != nil || held.Verdict != BuildingShelterPending {
		t.Fatal("clearance ran while the shelter was owed:", held.Verdict, err)
	}
	v.BedCapacity, v.IndoorSleepingCapacity = proto.Uint32(2), proto.Uint32(2)
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	released, err := planner.Step(ctx)
	if err != nil || released.Verdict != BuildingReasonAdmitted {
		t.Fatal("clearance stayed held after the shelter stood:", released.Verdict, err)
	}
}

// Another concern's room ring is held while the initial shelter is owed, and
// the shelter's own ring proceeds under the same review.
func TestRoundsOtherRingsWaitWhileShelterRingProceeds(t *testing.T) {
	r, db, _ := shelterSiteFixture(t)
	ctx := context.Background()
	review, err := db.LoadRounds(ctx)
	if err != nil {
		t.Fatal(err)
	}
	for _, concern := range []policy.ConcernID{policy.MaintainStockpiles, policy.MaintainResource, policy.MaintainBurial, policy.MaintainIncineration, policy.EnsureCooking, policy.MaintainAnimalContainment} {
		other := &RoundsBuildingPlanner{reviewer: r.reviewer, native: r.native, concern: concern}
		got, err := other.reconcileRoom(ctx, ctx, ControlState{}, review, nil, observation.RoundsReading{}, nil, roomReconcile{})
		if err != nil || got.Verdict != BuildingShelterPending {
			t.Fatal(concern, "raised a ring while the shelter was owed:", got.Verdict, err)
		}
	}
	shelter, err := r.Step(ctx)
	if err != nil || shelter.Verdict == BuildingShelterPending {
		t.Fatal("the shelter was held by its own hold:", shelter.Verdict, err)
	}
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
